using System.Globalization;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Storage;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Port fiel de app/excel_exporter.py (openpyxl). Rellena la plantilla de costos hoja por hoja con
/// ClosedXML y devuelve los bytes del .xlsx. NO recalcula: lee cost_results + azure_resources (+
/// tablas *_details) tal cual los dejó el motor de costos.
///
/// FILAS COMO DICCIONARIO: el Python hace `dict(zip(columns, row))` y accede a las columnas por
/// nombre (incluido `cr.*` = todas las columnas de cost_results: payg_monthly, payg_hourly,
/// ri_1y_monthly, ri_3y_monthly, savings_1y_pct/_monthly, manual_monthly_cost, ri_coverage,
/// sql_addon_monthly, storage_monthly, calculation_status/_notes, is_variable_pricing,
/// manual_cost_note, service_key, ...). Aquí se replica leyendo cada fila a un
/// Dictionary&lt;string,object?&gt; case-insensitive con TODAS las columnas del SELECT, para que el
/// acceso por nombre sea idéntico al Python.
///
/// RIESGOS ClosedXML (documentados, ver final del archivo):
///  - ClosedXML reescribe TODO el paquete OOXML: NO preserva imágenes/drawings ni comentarios VML
///    de la plantilla (openpyxl en este exporter SÍ los conserva porque solo edita celdas). El WAF
///    exporter resuelve esto con un parche ZIP/XML; aquí la plantilla de costos es tabular (sin
///    imágenes incrustadas conocidas), así que se omite ese parche — si la plantilla llega a tener
///    branding gráfico habría que portar RestoreTemplatePackage de ClosedXmlWafExporter.
///  - delete_rows + copia de estilo de fila plantilla: ClosedXML copia el Style por celda
///    (equivalente a _copy_cell_format de openpyxl: número/alineación/protección/fuente).
/// </summary>
public sealed class ClosedXmlCostExcelExporter(
    ISqlConnectionFactory factory,
    IBlobStorageService blobs,
    AppConfig config) : ICostExcelExporter
{
    private const int HoursPerMonth = 730;
    private const string ReservedHeader = "Reservado";

    public async Task<byte[]> GenerateAsync(int analysisId, CancellationToken ct)
    {
        var rows = await FetchRowsAsync(analysisId, ct);
        NeutralizeReservedRows(rows);
        var analysis = rows.TryGetValue("analysis", out var aList) && aList.Count > 0 ? aList[0] : null;

        var templateBytes = await blobs.DownloadAsync(
            config.StorageContainerTemplates, config.TemplateFileName, ct);

        // CPU/IO síncrono (ClosedXML). Se ejecuta directo sobre los bytes ya descargados.
        using var templateStream = new MemoryStream(templateBytes, writable: false);
        using var wb = new XLWorkbook(templateStream);

        var totals = new Dictionary<string, int>
        {
            ["vms"] = FillVms(wb, rows["vms"]),
            ["stopped_disks"] = 0,
            ["orphan_disks"] = 0,
            ["sql"] = FillSql(wb, rows["sql"]),
            ["appservice"] = FillAppService(wb, rows["appservice"]),
            ["mysql"] = FillMysql(wb, rows["mysql"]),
            ["sql_managed_instance"] = FillSqlManagedInstance(wb, rows["sql_managed_instance"]),
            ["cosmos"] = FillCosmos(wb, rows["cosmos"]),
            ["redis"] = FillRedis(wb, rows["redis"]),
            ["public_ip"] = FillPublicIp(wb, rows["public_ip"]),
        };
        var (stopped, orphan) = FillDisks(wb, rows["disks"]);
        totals["stopped_disks"] = stopped;
        totals["orphan_disks"] = orphan;
        FillSummary(wb, totals, analysis);
        FillRawDetail(wb, rows["results"]);
        FillDiscoveredServices(wb, rows["discovered_services"]);

        using var output = new MemoryStream();
        wb.SaveAs(output);
        return output.ToArray();
    }

    // =================================================================================
    // Helpers de conversión (port 1:1 de las funciones módulo-nivel del Python)
    // =================================================================================

    private static double Number(object? value, double @default = 0.0)
    {
        if (value is null || value is DBNull) return @default;
        if (value is decimal d) return (double)d;
        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return @default;
        }
    }

    private static string Text(object? value, string @default = "")
    {
        if (value is null || value is DBNull) return @default;
        return value.ToString() ?? @default;
    }

    /// <summary>_monthly: manual_monthly_cost si existe (no None), si no la clave dada.</summary>
    private static double Monthly(IDictionary<string, object?> row, string key = "payg_monthly")
    {
        var manual = Get(row, "manual_monthly_cost");
        if (manual is not null && manual is not DBNull) return Number(manual);
        return Number(Get(row, key));
    }

    private static double HourlyFromMonthly(object? value) =>
        Math.Round(Number(value) / HoursPerMonth, 6);

    /// <summary>_percent: None -&gt; "N/A"; si &gt;1 divide entre 100; si no, tal cual.</summary>
    private static object Percent(object? value)
    {
        if (value is null || value is DBNull) return "N/A";
        return Number(value) > 1 ? Number(value) / 100 : Number(value);
    }

    /// <summary>_na_number: None -&gt; "N/A"; si no, float.</summary>
    private static object NaNumber(object? value)
    {
        if (value is null || value is DBNull) return "N/A";
        return Number(value);
    }

    /// <summary>_safe_excel_text: prefija ' a strings que empiezan con =,+,-,@,\t,\r.</summary>
    private static object? SafeExcelText(object? value)
    {
        if (value is not string s) return value;
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            return "'" + s;
        return s;
    }

    private static string SumFormula(string colLetter, int startRow, int endRow) =>
        endRow < startRow ? "=0" : $"=SUM({colLetter}{startRow}:{colLetter}{endRow})";

    private static string CountFormula(string sheet, string col, int startRow, int endRow) =>
        endRow < startRow ? "=0" : $"=COUNTA('{sheet}'!{col}{startRow}:{col}{endRow})";

    private static string StatusLabel(object? value)
    {
        var text = Text(value).ToLowerInvariant();
        if (text.Contains("running")) return "Running";
        if (text.Contains("deallocated")) return "Stopped (deallocated)";
        if (text.Contains("stopped")) return "Stopped";
        return Text(value);
    }

    private static string SqlLicenseLabel(object? value)
    {
        var normalized = Text(value).Trim().ToUpperInvariant();
        if (normalized is "AHUB" or "AHB") return "Azure Hybrid Benefit";
        if (normalized == "PAYG") return "Pay As You Go";
        return Text(value);
    }

    /// <summary>_vcpu_from_size: extrae el número del 2º token de "Standard_D4s_v5" -&gt; 4 (o "").</summary>
    private static object VcpuFromSize(object? sizeValue)
    {
        var size = sizeValue as string;
        if (string.IsNullOrEmpty(size)) return "";
        var parts = size.Split('_');
        if (parts.Length < 2) return "";
        var token = parts[1];
        var digits = "";
        foreach (var ch in token)
        {
            if (char.IsDigit(ch)) digits += ch;
            else if (digits.Length > 0) break;
        }
        return digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : (object)"";
    }

    private static string DiskRole(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("osdisk")) return "OS Disk";
        if (lower.Contains("datadisk")) return "Data Disk";
        return "Managed Disk";
    }

    private static string Region(object? value)
    {
        var mapping = new Dictionary<string, string>
        {
            ["eastus"] = "East US",
            ["eastus2"] = "East US 2",
            ["centralus"] = "Central US",
            ["westus2"] = "West US 2",
        };
        var raw = Text(value);
        var key = raw.Replace(" ", "").ToLowerInvariant();
        return mapping.TryGetValue(key, out var v) ? v : raw;
    }

    /// <summary>_neutralize_reserved: iguala RI 1Y/3Y al PAYG y anula ahorro para confirmados.</summary>
    private static void NeutralizeReserved(IDictionary<string, object?> row)
    {
        var payg = Get(row, "payg_monthly");
        row["ri_1y_monthly"] = payg;
        row["ri_3y_monthly"] = payg;
        row["savings_1y_monthly"] = 0;
        row["savings_3y_monthly"] = 0;
        row["savings_1y_pct"] = 0;
        row["savings_3y_pct"] = 0;
    }

    private static void NeutralizeReservedRows(IDictionary<string, List<Dictionary<string, object?>>> rows)
    {
        foreach (var value in rows.Values)
            foreach (var row in value)
                if (Text(Get(row, "ri_coverage")) == "confirmed")
                    NeutralizeReserved(row);
    }

    /// <summary>_reserved_label: "Sí — &lt;reserva&gt; · &lt;term&gt;" solo si ri_coverage=confirmed.</summary>
    private static string ReservedLabel(IDictionary<string, object?> row)
    {
        if (Text(Get(row, "ri_coverage")) != "confirmed") return "";
        var parts = new[] { Get(row, "ri_reservation_name"), Get(row, "ri_term") }
            .Where(p => p is not null && p is not DBNull && !string.IsNullOrEmpty(p.ToString()))
            .Select(p => p!.ToString()!);
        var extra = string.Join(" · ", parts);
        return extra.Length > 0 ? $"Sí — {extra}" : "Sí";
    }

    // =================================================================================
    // Acceso a datos (port de _fetch_rows). cr.* -> todas las columnas del SELECT a dict.
    // =================================================================================

    private async Task<Dictionary<string, List<Dictionary<string, object?>>>> FetchRowsAsync(
        int analysisId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        // El Python asegura el esquema de vm_power_usage (LEFT JOIN en la hoja de VMs). Aquí se
        // crea best-effort por si nunca se corrió el refresh de encendido/apagado.
        await EnsurePowerUsageSchemaAsync(conn, ct);

        var queries = new (string Key, string Sql)[]
        {
            ("results", """
                SELECT cr.*, r.resource_name, r.resource_type, r.subscription_name,
                       r.resource_group, r.location, r.region_name, r.sku_name,
                       r.sku_tier, r.size_name, r.status, r.os_type, r.license_type,
                       sc.display_name
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.service_catalog sc ON sc.service_key = cr.service_key
                WHERE cr.analysis_id = @id
                ORDER BY cr.service_key, r.resource_name
                """),
            ("vms", """
                SELECT cr.*, r.resource_name, r.subscription_name, r.resource_group,
                       r.location, r.status, r.os_type, r.license_type, r.sku_name,
                       d.vm_size, d.power_state, d.os_license_benefit, d.vcpu_count,
                       d.has_sql_server, d.sql_edition, d.sql_license_type,
                       s.sql_image_sku, s.sql_license_type AS sql_vm_license_type,
                       s.sql_image_offer, s.sql_management,
                       pu.running_hours, pu.uptime_pct
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.vm_details d ON d.resource_id = r.resource_id
                LEFT JOIN dbo.sql_vm_details s
                    ON LOWER(s.vm_azure_resource_id) = LOWER(r.azure_resource_id)
                LEFT JOIN dbo.vm_power_usage pu
                    ON pu.resource_id = r.resource_id AND pu.analysis_id = cr.analysis_id
                WHERE cr.analysis_id = @id AND cr.service_key = 'vms'
                ORDER BY r.resource_name
                """),
            ("disks", """
                SELECT cr.*, r.resource_name, r.subscription_name, r.resource_group,
                       r.location, r.status, r.sku_name, d.disk_sku, d.disk_tier,
                       d.disk_size_gb, d.managed_by, d.attached_vm_name, d.disk_category,
                       vm.power_state AS attached_vm_power_state
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.disk_details d ON d.resource_id = r.resource_id
                LEFT JOIN dbo.azure_resources vmr
                    ON vmr.analysis_id = r.analysis_id
                   AND vmr.resource_name = d.attached_vm_name
                   AND vmr.resource_type = 'microsoft.compute/virtualmachines'
                LEFT JOIN dbo.vm_details vm ON vm.resource_id = vmr.resource_id
                WHERE cr.analysis_id = @id AND cr.service_key = 'disks'
                ORDER BY d.disk_category, r.resource_name
                """),
            ("sql", """
                SELECT cr.*, r.resource_name, r.subscription_name, r.location,
                       d.server_name, d.sku_name AS detail_sku_name, d.sku_tier,
                       d.sku_capacity, d.compute_tier
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.sql_db_details d ON d.resource_id = r.resource_id
                WHERE cr.analysis_id = @id AND cr.service_key = 'sql'
                ORDER BY d.server_name, r.resource_name
                """),
            ("appservice", """
                SELECT cr.*, r.resource_name, r.subscription_name, r.resource_group,
                       r.location, d.sku_name AS detail_sku_name, d.sku_tier,
                       d.sku_capacity, d.is_linux, d.kind
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.appservice_plan_details d ON d.resource_id = r.resource_id
                WHERE cr.analysis_id = @id AND cr.service_key = 'appservice'
                ORDER BY r.resource_name
                """),
            ("mysql", """
                SELECT cr.*, r.resource_name, r.subscription_name, r.location,
                       d.sku_name AS detail_sku_name, d.sku_tier, d.storage_size_gb,
                       d.storage_iops, d.ha_mode, d.backup_retention_days
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.mysql_flex_details d ON d.resource_id = r.resource_id
                WHERE cr.analysis_id = @id AND cr.service_key = 'mysql'
                ORDER BY r.resource_name
                """),
            ("sql_managed_instance", """
                SELECT cr.*, r.resource_name, r.subscription_name, r.resource_group,
                       r.location, d.sku_name AS detail_sku_name, d.sku_tier,
                       d.sku_family, d.vcore_count, d.storage_size_gb,
                       d.license_type, d.zone_redundant, d.public_data_endpoint_enabled
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.sql_managed_instance_details d ON d.resource_id = r.resource_id
                WHERE cr.analysis_id = @id AND cr.service_key = 'sql_managed_instance'
                ORDER BY r.resource_name
                """),
            ("cosmos", """
                SELECT cr.*, r.resource_name, r.subscription_name, r.resource_group,
                       r.location, d.api_kind, d.default_experience, d.is_serverless,
                       d.multi_region_writes, d.region_count
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.cosmos_details d ON d.resource_id = r.resource_id
                WHERE cr.analysis_id = @id AND cr.service_key = 'cosmos'
                ORDER BY r.resource_name
                """),
            ("redis", """
                SELECT cr.*, r.resource_name, r.subscription_name, r.resource_group,
                       r.location, d.sku_name AS detail_sku_name, d.sku_family,
                       d.sku_capacity, d.shard_count
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.redis_details d ON d.resource_id = r.resource_id
                WHERE cr.analysis_id = @id AND cr.service_key = 'redis'
                ORDER BY r.resource_name
                """),
            ("public_ip", """
                SELECT cr.*, r.resource_name, r.location, d.ip_address, d.ip_version,
                       d.sku_name AS detail_sku_name, d.associated_to
                FROM dbo.cost_results cr
                INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
                LEFT JOIN dbo.public_ip_details d ON d.resource_id = r.resource_id
                WHERE cr.analysis_id = @id AND cr.service_key = 'public_ip'
                ORDER BY r.resource_name
                """),
            ("analysis", """
                SELECT ca.analysis_id, ca.analysis_name, ca.created_at, c.client_name
                FROM dbo.cost_analysis ca
                INNER JOIN dbo.clients c ON c.client_id = ca.client_id
                WHERE ca.analysis_id = @id
                """),
            ("discovered_services", """
                SELECT dst.resource_type, dst.resource_count, dst.status,
                       dst.catalog_service_key, dst.sample_names, dst.last_seen_at
                FROM dbo.discovered_service_types dst
                INNER JOIN dbo.cost_analysis ca ON ca.client_id = dst.client_id
                WHERE ca.analysis_id = @id
                ORDER BY
                    CASE WHEN dst.status = 'uncataloged' THEN 0 ELSE 1 END,
                    dst.resource_count DESC,
                    dst.resource_type
                """),
        };

        var output = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var (key, sql) in queries)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqlParameter("@id", analysisId));
            var list = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // dict(zip(columns, row)) con clave case-insensitive.
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    var v = reader.GetValue(i);
                    dict[name] = v is DBNull ? null : v;
                }
                list.Add(dict);
            }
            output[key] = list;
        }
        return output;
    }

    /// <summary>
    /// Port best-effort de power_history.ensure_power_usage_schema: crea la tabla vm_power_usage si
    /// no existe (la hoja de VMs hace LEFT JOIN). Si ya existe, no toca nada.
    /// </summary>
    private static async Task EnsurePowerUsageSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'vm_power_usage' AND schema_id = SCHEMA_ID('dbo'))
            CREATE TABLE dbo.vm_power_usage (
                resource_id   INT          NOT NULL,
                analysis_id   INT          NOT NULL,
                running_hours FLOAT        NULL,
                uptime_pct    FLOAT        NULL,
                CONSTRAINT PK_vm_power_usage PRIMARY KEY (resource_id, analysis_id)
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object? Get(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) ? v : null;

    // =================================================================================
    // _replace_body y manejo de plantilla de fila (port de openpyxl con ClosedXML)
    // =================================================================================

    /// <summary>
    /// Port de _replace_body: toma como plantilla la fila 2 (datos) y la última fila (TOTAL),
    /// borra el cuerpo (filas 2..max-1), reescribe las filas de datos aplicando el estilo de la
    /// fila 2, y añade una fila TOTAL aplicando el estilo de la fila TOTAL original. Devuelve el
    /// número de fila del TOTAL (1-based). totals: columna(1-based) -&gt; valor (string fórmula).
    /// </summary>
    private static int ReplaceBody(IXLWorksheet ws, List<List<object?>> dataRows,
        IDictionary<int, object?>? totals = null, string totalLabel = "TOTAL")
    {
        var maxCol = ws.LastColumnUsed()?.ColumnNumber() ?? (dataRows.Count > 0 ? dataRows[0].Count : 1);
        var maxRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        // Snapshot de estilos de las filas plantilla ANTES de borrar.
        var dataTemplate = SnapshotRowStyles(ws, 2, maxCol);
        var totalTemplate = SnapshotRowStyles(ws, maxRow, maxCol);
        var headerFont = ws.Cell(1, 1).Style.Font; // copia de la fuente de la cabecera (A1)

        // delete_rows(2, max_row - 1): borra las filas 2..maxRow inclusive (= maxRow-1 filas),
        // dejando solo la cabecera. ClosedXML.Rows(2, maxRow).Delete() corre las filas de abajo.
        if (maxRow >= 2)
            ws.Rows(2, maxRow).Delete();

        var currentRow = 2;
        foreach (var data in dataRows)
        {
            ApplyRowTemplate(ws, currentRow, dataTemplate);
            for (var col = 0; col < data.Count; col++)
                SetCellValue(ws.Cell(currentRow, col + 1), SafeExcelText(data[col]));
            currentRow++;
        }

        var totalRow = currentRow;
        ApplyRowTemplate(ws, totalRow, totalTemplate);
        SetCellValue(ws.Cell(totalRow, 1), totalLabel);
        ws.Cell(totalRow, 1).Style.Font = headerFont; // ws.cell(1,1).font
        if (totals is not null)
            foreach (var (col, value) in totals)
                SetCellValue(ws.Cell(totalRow, col), value);
        return totalRow;
    }

    private static IXLStyle[] SnapshotRowStyles(IXLWorksheet ws, int row, int maxCol)
    {
        var styles = new IXLStyle[maxCol];
        for (var col = 1; col <= maxCol; col++)
            styles[col - 1] = ws.Cell(row, col).Style; // IXLStyle es inmutable -> snapshot válido
        return styles;
    }

    private static void ApplyRowTemplate(IXLWorksheet ws, int row, IXLStyle[] template)
    {
        for (var i = 0; i < template.Length; i++)
            ws.Cell(row, i + 1).Style = template[i];
    }

    /// <summary>
    /// Escribe el valor en la celda. Si es string que empieza con "=" lo trata como fórmula
    /// (openpyxl interpreta "=SUM(...)" como fórmula automáticamente). Los strings ya pasaron por
    /// SafeExcelText, así que un "=..." aquí es intencionalmente una fórmula (totals).
    /// </summary>
    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                cell.Clear(XLClearOptions.Contents);
                return;
            case string s when s.StartsWith('='):
                cell.FormulaA1 = s;
                return;
            case string s:
                cell.SetValue(s);
                return;
            case int i:
                cell.SetValue(i);
                return;
            case long l:
                cell.SetValue(l);
                return;
            case double d:
                cell.SetValue(d);
                return;
            case decimal m:
                cell.SetValue(m);
                return;
            case bool b:
                cell.SetValue(b);
                return;
            case DateTime dt:
                cell.SetValue(dt);
                return;
            default:
                cell.SetValue(value.ToString() ?? "");
                return;
        }
    }

    // =================================================================================
    // Columnas extra al final de una hoja poblada (port de _append_column y familia)
    // =================================================================================

    private static int AppendColumn(IXLWorksheet ws, string header, IEnumerable<object?> values,
        int width = 30, string? numberFormat = null)
    {
        var col = (ws.LastColumnUsed()?.ColumnNumber() ?? 0) + 1;
        var headerCell = ws.Cell(1, col);
        headerCell.SetValue(header);
        headerCell.Style = ws.Cell(1, 1).Style; // hereda estilo de la cabecera A1
        ws.Column(col).Width = width;
        var index = 0;
        foreach (var value in values)
        {
            var target = ws.Cell(2 + index, col);
            SetCellValue(target, value);
            // openpyxl aplica el number_format solo si el valor es int/float (no a "N/A").
            if (numberFormat is not null && value is int or double or decimal or long)
                target.Style.NumberFormat.Format = numberFormat;
            index++;
        }
        return col;
    }

    private static void AppendReservedColumn(IXLWorksheet ws, IList<string> labels) =>
        AppendColumn(ws, ReservedHeader, labels.Select(l => SafeExcelText(l)));

    private static void AppendPowerColumns(IXLWorksheet ws, List<Dictionary<string, object?>> rows)
    {
        AppendColumn(ws, "Horas encendida (mes ant.)",
            rows.Select(r => (object?)NaNumber(Get(r, "running_hours"))),
            width: 22, numberFormat: "0.0");
        AppendColumn(ws, "% Uptime (mes ant.)",
            rows.Select(r => (object?)NaNumber(Get(r, "uptime_pct"))),
            width: 18, numberFormat: "0.0\"%\"");
    }

    // =================================================================================
    // Hojas por servicio (port 1:1 de _fill_*)
    // =================================================================================

    private static int FillVms(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        var ws = wb.Worksheet("Optimizacion VMs");
        var data = new List<List<object?>>();
        foreach (var row in rows)
        {
            var sqlMonthly = Number(Get(row, "sql_addon_monthly"));
            var sqlEdition = Get(row, "sql_image_sku") ?? Get(row, "sql_edition") ?? "";
            var sqlLicense = SqlLicenseLabel(Get(row, "sql_vm_license_type") ?? Get(row, "sql_license_type"));
            data.Add(new List<object?>
            {
                Get(row, "resource_name"),
                Region(Get(row, "location")),
                StatusLabel(Get(row, "power_state") ?? Get(row, "status")),
                Get(row, "os_type"),
                Get(row, "os_license_benefit") ?? Get(row, "license_type") ?? "Not enabled",
                Get(row, "vm_size") ?? Get(row, "sku_name"),
                Get(row, "vcpu_count") ?? VcpuFromSize(Get(row, "vm_size") ?? Get(row, "sku_name")),
                sqlEdition,
                sqlLicense,
                HourlyFromMonthly(sqlMonthly),
                Number(Get(row, "payg_hourly")),
                Monthly(row),
                NaNumber(Get(row, "ri_1y_monthly")),
                NaNumber(Get(row, "ri_3y_monthly")),
                Percent(Get(row, "savings_1y_pct")),
                Percent(Get(row, "savings_3y_pct")),
            });
        }
        var n = data.Count;
        var totalRow = ReplaceBody(ws, data, new Dictionary<int, object?>
        {
            [10] = SumFormula("J", 2, n + 1),
            [11] = SumFormula("K", 2, n + 1),
            [12] = SumFormula("L", 2, n + 1),
            [13] = SumFormula("M", 2, n + 1),
            [14] = SumFormula("N", 2, n + 1),
            [15] = $"=IFERROR(1-M{n + 2}/L{n + 2},0)",
            [16] = $"=IFERROR(1-N{n + 2}/L{n + 2},0)",
        }, "TOTAL VMs");
        AppendReservedColumn(ws, rows.Select(ReservedLabel).ToList());
        AppendPowerColumns(ws, rows);
        return totalRow;
    }

    private static (int Stopped, int Orphan) FillDisks(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        var stopped = new List<List<object?>>();
        var stoppedReserved = new List<string>();
        var orphan = new List<List<object?>>();
        var orphanReserved = new List<string>();
        foreach (var row in rows)
        {
            var category = Text(Get(row, "disk_category")).ToLowerInvariant();
            var attachedState = Text(Get(row, "attached_vm_power_state")).ToLowerInvariant();
            if (attachedState.Contains("stopped") || attachedState.Contains("deallocated"))
            {
                stopped.Add(new List<object?>
                {
                    Get(row, "attached_vm_name") ?? "",
                    Get(row, "subscription_name"),
                    Get(row, "resource_group"),
                    Region(Get(row, "location")),
                    "",
                    DiskRole(Text(Get(row, "resource_name"))),
                    Get(row, "resource_name"),
                    Get(row, "disk_sku") ?? Get(row, "sku_name"),
                    Get(row, "disk_size_gb"),
                    Get(row, "disk_tier"),
                    Monthly(row),
                });
                stoppedReserved.Add(ReservedLabel(row));
            }
            // Python: `not row.get("attached_vm_name")` es True para null, ausente Y cadena vacía.
            var attachedVm = Get(row, "attached_vm_name");
            if (category != "attached" || string.IsNullOrEmpty(attachedVm?.ToString()))
            {
                var label = Text(Get(row, "resource_name")).ToLowerInvariant().Contains("asr")
                    ? "ASR (NO ELIMINAR)" : "HUERFANO";
                orphan.Add(new List<object?>
                {
                    Get(row, "resource_name"),
                    label,
                    Get(row, "subscription_name"),
                    Get(row, "resource_group"),
                    Region(Get(row, "location")),
                    Get(row, "disk_sku") ?? Get(row, "sku_name"),
                    Get(row, "disk_size_gb"),
                    Get(row, "disk_tier"),
                    Monthly(row),
                    Get(row, "calculation_notes"),
                });
                orphanReserved.Add(ReservedLabel(row));
            }
        }

        var stoppedWs = wb.Worksheet("VMs Apagadas - Discos");
        var stoppedTotal = ReplaceBody(stoppedWs, stopped, new Dictionary<int, object?>
        {
            [11] = SumFormula("K", 2, stopped.Count + 1),
        }, "TOTAL");
        AppendReservedColumn(stoppedWs, stoppedReserved);

        var orphanWs = wb.Worksheet("Discos Huérfanos y ASR");
        var orphanTotal = ReplaceBody(orphanWs, orphan, new Dictionary<int, object?>
        {
            [9] = SumFormula("I", 2, orphan.Count + 1),
        }, "TOTAL");
        AppendReservedColumn(orphanWs, orphanReserved);
        return (stoppedTotal, orphanTotal);
    }

    private static int FillSql(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        var data = new List<List<object?>>();
        foreach (var row in rows)
        {
            var sku = Text(Get(row, "detail_sku_name") ?? Get(row, "sku_name"));
            var tierObj = Get(row, "sku_tier");
            var tier = tierObj is not null && tierObj is not DBNull && !string.IsNullOrEmpty(tierObj.ToString())
                ? tierObj.ToString()! : sku;
            var tierLower = tier.ToLowerInvariant();
            var model = (tierLower is "basic" or "standard" or "premium") && !sku.ToLowerInvariant().Contains("gen")
                ? "DTU" : "vCore";
            data.Add(new List<object?>
            {
                $"{Get(row, "resource_name")} ({Get(row, "server_name")}/{Get(row, "resource_name")})",
                Get(row, "server_name"),
                Get(row, "subscription_name"),
                Region(Get(row, "location")),
                $"{tier}: {Get(row, "compute_tier") ?? ""}".Trim(),
                model,
                tier,
                Get(row, "sku_capacity") ?? "",
                Monthly(row),
                NaNumber(Get(row, "ri_1y_monthly")),
                NaNumber(Get(row, "ri_3y_monthly")),
                Percent(Get(row, "savings_1y_pct")),
                Percent(Get(row, "savings_3y_pct")),
            });
        }
        var ws = wb.Worksheet("SQL Database");
        var n = data.Count;
        var totalRow = ReplaceBody(ws, data, new Dictionary<int, object?>
        {
            [9] = SumFormula("I", 2, n + 1),
            [10] = SumFormula("J", 2, n + 1),
            [11] = SumFormula("K", 2, n + 1),
            [12] = $"=IFERROR(1-J{n + 2}/I{n + 2},0)",
            [13] = $"=IFERROR(1-K{n + 2}/I{n + 2},0)",
        }, "TOTAL SQL");
        AppendReservedColumn(ws, rows.Select(ReservedLabel).ToList());
        return totalRow;
    }

    private static int FillAppService(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        var data = new List<List<object?>>();
        foreach (var row in rows)
        {
            var sku = Text(Get(row, "detail_sku_name") ?? Get(row, "sku_name"));
            var capacity = Get(row, "sku_capacity");
            var capacityStr = capacity is null or DBNull ? "" : capacity.ToString();
            var isLinux = Get(row, "is_linux");
            data.Add(new List<object?>
            {
                Get(row, "resource_name"),
                Get(row, "resource_group"),
                Get(row, "subscription_name"),
                Region(Get(row, "location")),
                IsTruthy(isLinux) ? "Linux" : "Windows",
                "",
                $"{Get(row, "sku_tier") ?? ""} ({sku}: {capacityStr})",
                sku,
                capacity is null or DBNull ? 1 : capacity,
                Number(Get(row, "payg_hourly")),
                Monthly(row),
                NaNumber(Get(row, "ri_1y_monthly")),
                NaNumber(Get(row, "ri_3y_monthly")),
                Percent(Get(row, "savings_1y_pct")),
                Percent(Get(row, "savings_3y_pct")),
            });
        }
        var ws = wb.Worksheet("App Service Plans");
        var n = data.Count;
        var totalRow = ReplaceBody(ws, data, new Dictionary<int, object?>
        {
            [11] = SumFormula("K", 2, n + 1),
            [12] = SumFormula("L", 2, n + 1),
            [13] = SumFormula("M", 2, n + 1),
            [14] = $"=IFERROR(1-L{n + 2}/K{n + 2},0)",
            [15] = $"=IFERROR(1-M{n + 2}/K{n + 2},0)",
        }, "TOTAL App Service");
        AppendReservedColumn(ws, rows.Select(ReservedLabel).ToList());
        return totalRow;
    }

    private static int FillMysql(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        var data = new List<List<object?>>();
        foreach (var row in rows)
        {
            var computeMonthly = Number(Get(row, "payg_monthly")) - Number(Get(row, "storage_monthly"));
            data.Add(new List<object?>
            {
                Get(row, "resource_name"),
                Get(row, "subscription_name"),
                Region(Get(row, "location")),
                Get(row, "detail_sku_name") ?? Get(row, "sku_name"),
                Get(row, "sku_tier"),
                "",
                Get(row, "storage_size_gb"),
                Get(row, "storage_iops"),
                Get(row, "ha_mode"),
                Get(row, "backup_retention_days"),
                computeMonthly,
                Number(Get(row, "storage_monthly")),
                Monthly(row),
                NaNumber(Get(row, "ri_1y_monthly")),
                NaNumber(Get(row, "ri_3y_monthly")),
                Percent(Get(row, "savings_1y_pct")),
                Percent(Get(row, "savings_3y_pct")),
            });
        }
        var ws = wb.Worksheet("MySQL Flexible");
        var n = data.Count;
        var totalRow = ReplaceBody(ws, data, new Dictionary<int, object?>
        {
            [11] = SumFormula("K", 2, n + 1),
            [12] = SumFormula("L", 2, n + 1),
            [13] = SumFormula("M", 2, n + 1),
            [14] = SumFormula("N", 2, n + 1),
            [15] = SumFormula("O", 2, n + 1),
            [16] = $"=IFERROR(1-N{n + 2}/M{n + 2},0)",
            [17] = $"=IFERROR(1-O{n + 2}/M{n + 2},0)",
        }, "TOTAL MySQL");
        AppendReservedColumn(ws, rows.Select(ReservedLabel).ToList());
        return totalRow;
    }

    private static int FillSqlManagedInstance(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        const string title = "SQL Managed Instance";
        if (wb.Worksheets.TryGetWorksheet(title, out var existing)) existing.Delete();
        var ws = wb.Worksheets.Add(title);
        WriteHeaders(ws, new[]
        {
            "NAME", "SUBSCRIPTION", "RESOURCE GROUP", "LOCATION", "TIER", "FAMILY/SKU",
            "vCORES", "STORAGE GB", "LICENSE TYPE", "ZONE REDUNDANT",
            "PAYG MONTHLY", "RI 1Y MONTHLY", "RI 3Y MONTHLY",
            "SAVINGS 1Y", "SAVINGS 3Y", "STATUS", "NOTES", ReservedHeader,
        });
        StyleHeader(ws);

        var rowNum = 2;
        foreach (var row in rows)
        {
            var values = new List<object?>
            {
                Get(row, "resource_name"),
                Get(row, "subscription_name"),
                Get(row, "resource_group"),
                Region(Get(row, "location")),
                Get(row, "sku_tier"),
                Get(row, "sku_family") ?? Get(row, "detail_sku_name") ?? Get(row, "sku_name"),
                Get(row, "vcore_count"),
                Get(row, "storage_size_gb"),
                Get(row, "license_type"),
                IsTruthy(Get(row, "zone_redundant")) ? "Si" : "No",
                Monthly(row),
                NaNumber(Get(row, "ri_1y_monthly")),
                NaNumber(Get(row, "ri_3y_monthly")),
                Percent(Get(row, "savings_1y_pct")),
                Percent(Get(row, "savings_3y_pct")),
                Get(row, "calculation_status"),
                Get(row, "calculation_notes"),
                SafeExcelText(ReservedLabel(row)),
            };
            for (var col = 0; col < values.Count; col++)
                SetCellValue(ws.Cell(rowNum, col + 1), values[col]);
            rowNum++;
        }

        var totalRow = rows.Count + 2;
        var totalValues = new object?[]
        {
            "TOTAL SQL MI", "", "", "", "", "", "", "", "", "",
            SumFormula("K", 2, rows.Count + 1),
            SumFormula("L", 2, rows.Count + 1),
            SumFormula("M", 2, rows.Count + 1),
            $"=IFERROR(1-L{totalRow}/K{totalRow},0)",
            $"=IFERROR(1-M{totalRow}/K{totalRow},0)",
            "", "", "",
        };
        for (var col = 0; col < totalValues.Length; col++)
            SetCellValue(ws.Cell(totalRow, col + 1), totalValues[col]);
        Autosize(ws);
        return totalRow;
    }

    private static int FillCosmos(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        var data = new List<List<object?>>();
        foreach (var row in rows)
        {
            var variable = IsTruthy(Get(row, "is_variable_pricing"));
            var regionCount = Get(row, "region_count");
            var multiRegion = regionCount is not null && regionCount is not DBNull && Number(regionCount) > 1;
            data.Add(new List<object?>
            {
                Get(row, "resource_name"),
                Get(row, "subscription_name"),
                Get(row, "resource_group"),
                Region(Get(row, "location")),
                Get(row, "default_experience") ?? Get(row, "api_kind"),
                IsTruthy(Get(row, "is_serverless")) ? "Serverless" : "Provisioned",
                "",
                "",
                "",
                multiRegion ? "Si" : "No",
                "No",
                variable ? "Variable" : (object)Monthly(row),
                variable ? "Variable" : (object)Monthly(row),
                "",
                NaNumber(Get(row, "ri_1y_monthly")),
            });
        }
        var ws = wb.Worksheet("Cosmos DB");
        var n = data.Count;
        var totalRow = ReplaceBody(ws, data, new Dictionary<int, object?>
        {
            [12] = SumFormula("L", 2, n + 1),
            [13] = SumFormula("M", 2, n + 1),
            [14] = SumFormula("N", 2, n + 1),
            [15] = SumFormula("O", 2, n + 1),
        }, "TOTAL Cosmos");
        AppendReservedColumn(ws, rows.Select(ReservedLabel).ToList());
        return totalRow;
    }

    private static int FillRedis(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        var data = new List<List<object?>>();
        foreach (var row in rows)
        {
            var family = Text(Get(row, "sku_family"));
            var capacity = Get(row, "sku_capacity");
            var skuName = Text(Get(row, "detail_sku_name") ?? Get(row, "sku_name"));
            var skuLabel = capacity is not null && capacity is not DBNull
                ? $"{skuName} {family}{capacity}" : skuName;
            data.Add(new List<object?>
            {
                Get(row, "resource_name"),
                Get(row, "subscription_name"),
                Get(row, "resource_group"),
                Region(Get(row, "location")),
                "Azure Cache for Redis (Classic)",
                skuLabel,
                capacity is null or DBNull ? "" : capacity,
                "No",
                "No",
                Number(Get(row, "payg_hourly")),
                Monthly(row),
                NaNumber(Get(row, "ri_1y_monthly")),
                NaNumber(Get(row, "ri_3y_monthly")),
                Percent(Get(row, "savings_1y_pct")),
                Percent(Get(row, "savings_3y_pct")),
            });
        }
        var ws = wb.Worksheet("Redis");
        var n = data.Count;
        var totalRow = ReplaceBody(ws, data, new Dictionary<int, object?>
        {
            [11] = SumFormula("K", 2, n + 1),
            [12] = SumFormula("L", 2, n + 1),
            [13] = SumFormula("M", 2, n + 1),
            [14] = $"=IFERROR(1-L{n + 2}/K{n + 2},0)",
            [15] = $"=IFERROR(1-M{n + 2}/K{n + 2},0)",
        }, "TOTAL Redis");
        AppendReservedColumn(ws, rows.Select(ReservedLabel).ToList());
        return totalRow;
    }

    private static int FillPublicIp(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        var data = new List<List<object?>>();
        foreach (var row in rows)
        {
            data.Add(new List<object?>
            {
                Get(row, "resource_name"),
                Get(row, "ip_address") ?? "-",
                Get(row, "ip_version"),
                Get(row, "detail_sku_name") ?? Get(row, "sku_name"),
                Get(row, "associated_to") ?? "-",
                Region(Get(row, "location")),
                Monthly(row),
            });
        }
        var ws = wb.Worksheet("IP Pública");
        var n = data.Count;
        var totalRow = ReplaceBody(ws, data, new Dictionary<int, object?>
        {
            [7] = SumFormula("G", 2, n + 1),
        }, "TOTAL IP Pública");
        AppendReservedColumn(ws, rows.Select(ReservedLabel).ToList());
        return totalRow;
    }

    // =================================================================================
    // Hojas creadas desde cero (port de _fill_raw_detail / _fill_discovered_services)
    // =================================================================================

    private static void FillRawDetail(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        const string title = "Detalle Recursos";
        if (wb.Worksheets.TryGetWorksheet(title, out var existing)) existing.Delete();
        var ws = wb.Worksheets.Add(title);
        WriteHeaders(ws, new[]
        {
            "Servicio", "Recurso", "Tipo Azure", "Suscripcion", "Grupo", "Region",
            "SKU", "Estado", "Reservado", "PAYG mensual", "RI 1Y mensual", "RI 3Y mensual",
            "Ahorro 1Y", "Ahorro 3Y", "Estado calculo", "Notas",
        });
        StyleHeader(ws);
        var rowNum = 2;
        foreach (var row in rows)
        {
            var values = new List<object?>
            {
                Get(row, "display_name") ?? Get(row, "service_key"),
                Get(row, "resource_name"),
                Get(row, "resource_type"),
                Get(row, "subscription_name"),
                Get(row, "resource_group"),
                Get(row, "location"),
                Get(row, "sku_name") ?? Get(row, "size_name"),
                Get(row, "status"),
                ReservedLabel(row),
                Monthly(row),
                Get(row, "ri_1y_monthly"),
                Get(row, "ri_3y_monthly"),
                Get(row, "savings_1y_monthly"),
                Get(row, "savings_3y_monthly"),
                Get(row, "calculation_status"),
                Get(row, "calculation_notes") ?? Get(row, "manual_cost_note"),
            };
            for (var col = 0; col < values.Count; col++)
                SetCellValue(ws.Cell(rowNum, col + 1), values[col]);
            rowNum++;
        }
        Autosize(ws);
    }

    private static void FillDiscoveredServices(XLWorkbook wb, List<Dictionary<string, object?>> rows)
    {
        const string title = "Servicios no catalogados";
        if (wb.Worksheets.TryGetWorksheet(title, out var existing)) existing.Delete();
        var ws = wb.Worksheets.Add(title);
        WriteHeaders(ws, new[]
        {
            "Resource Type", "Cantidad", "Estado", "Servicio Catalogo",
            "Ejemplos", "Ultima deteccion",
        });
        StyleHeader(ws);
        var rowNum = 2;
        foreach (var row in rows)
        {
            var values = new List<object?>
            {
                Get(row, "resource_type"),
                Get(row, "resource_count"),
                Get(row, "status"),
                Get(row, "catalog_service_key") ?? "",
                Get(row, "sample_names") ?? "",
                Text(Get(row, "last_seen_at")),
            };
            for (var col = 0; col < values.Count; col++)
                SetCellValue(ws.Cell(rowNum, col + 1), values[col]);
            rowNum++;
        }
        Autosize(ws);
    }

    private static void FillSummary(XLWorkbook wb, IDictionary<string, int> totals,
        IDictionary<string, object?>? analysis)
    {
        var ws = wb.Worksheet("Resumen Ejecutivo");
        if (analysis is not null)
        {
            ws.Cell("A1").SetValue($"{Get(analysis, "client_name")} - OPTIMIZACION DE COSTOS AZURE - RESUMEN EJECUTIVO");
            ws.Cell("A3").SetValue("Fuente: Azure Retail Prices API. Export generado desde la plataforma Business IT.");
        }

        // (row, label, sheet, total_row_key, count_col, payg_col, ri1_col, ri3_col)
        var serviceRows = new (int Row, string Label, string Sheet, int TotalRow, string CountCol, string PaygCol, string? Ri1Col, string? Ri3Col)[]
        {
            (6, "Virtual Machines (Running)", "Optimizacion VMs", totals["vms"], "B", "L", "M", "N"),
            (7, "Azure SQL Database", "SQL Database", totals["sql"], "A", "I", "J", "K"),
            (8, "App Service Plans", "App Service Plans", totals["appservice"], "A", "K", "L", "M"),
            (9, "MySQL Flexible Server", "MySQL Flexible", totals["mysql"], "A", "M", "N", "O"),
            (10, "SQL Managed Instance", "SQL Managed Instance", totals["sql_managed_instance"], "A", "K", "L", "M"),
            (11, "Cosmos DB", "Cosmos DB", totals["cosmos"], "A", "L", null, null),
            (12, "Redis", "Redis", totals["redis"], "A", "K", "L", "M"),
            (13, "Discos VMs Apagadas", "VMs Apagadas - Discos", totals["stopped_disks"], "A", "K", null, null),
            (14, "Discos Huerfanos y ASR", "Discos Huérfanos y ASR", totals["orphan_disks"], "A", "I", null, null),
            (15, "IP Publica", "IP Pública", totals["public_ip"], "A", "G", null, null),
        };
        foreach (var s in serviceRows)
        {
            ws.Cell(s.Row, 1).SetValue(s.Label);
            ws.Cell(s.Row, 2).FormulaA1 = CountFormula(s.Sheet, s.CountCol, 2, Math.Max(s.TotalRow - 1, 1));
            ws.Cell(s.Row, 3).FormulaA1 = $"='{s.Sheet}'!{s.PaygCol}{s.TotalRow}";
            if (s.Ri1Col is not null) ws.Cell(s.Row, 4).FormulaA1 = $"='{s.Sheet}'!{s.Ri1Col}{s.TotalRow}";
            else ws.Cell(s.Row, 4).SetValue("N/A");
            if (s.Ri3Col is not null) ws.Cell(s.Row, 5).FormulaA1 = $"='{s.Sheet}'!{s.Ri3Col}{s.TotalRow}";
            else ws.Cell(s.Row, 5).SetValue("N/A");
        }
    }

    // =================================================================================
    // Estilos (port de _style_header / _autosize)
    // =================================================================================

    /// <summary>Escribe los encabezados de la fila 1 (port de ws.append([...]) sobre una hoja nueva).</summary>
    private static void WriteHeaders(IXLWorksheet ws, IReadOnlyList<string> headers)
    {
        for (var col = 0; col < headers.Count; col++)
            ws.Cell(1, col + 1).SetValue(headers[col]);
    }

    private static void StyleHeader(IXLWorksheet ws)
    {
        var headerRow = ws.Row(1);
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
        for (var col = 1; col <= lastCol; col++)
        {
            var cell = headerRow.Cell(col);
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.FromHtml("#FFFFFF");
            cell.Style.Fill.PatternType = XLFillPatternValues.Solid;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#5CB531");
        }
    }

    private static void Autosize(IXLWorksheet ws)
    {
        // openpyxl: ancho = min(max(max_len + 2, 12), 48). ClosedXML AdjustToContents no replica
        // exactamente el cálculo de openpyxl; se porta el mismo cálculo manual celda a celda.
        var used = ws.RangeUsed();
        if (used is null) return;
        var firstCol = used.FirstColumn().ColumnNumber();
        var lastCol = used.LastColumn().ColumnNumber();
        var firstRow = used.FirstRow().RowNumber();
        var lastRow = used.LastRow().RowNumber();
        for (var col = firstCol; col <= lastCol; col++)
        {
            var maxLen = 0;
            for (var row = firstRow; row <= lastRow; row++)
            {
                var cell = ws.Cell(row, col);
                var text = cell.IsEmpty() ? "" : cell.GetFormattedString();
                if (text.Length > maxLen) maxLen = text.Length;
            }
            ws.Column(col).Width = Math.Min(Math.Max(maxLen + 2, 12), 48);
        }
    }

    /// <summary>
    /// Verdad estilo Python (bool(value)): None/0/""/"0"? En el Python solo se usa sobre flags de BD
    /// (is_linux, zone_redundant, is_serverless, is_variable_pricing) que llegan como 0/1/bool. Se
    /// replica esa semántica: null/DBNull/0/false/"" -> false; resto -> true.
    /// </summary>
    private static bool IsTruthy(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return false;
            case bool b:
                return b;
            case string s:
                return s.Length > 0;
        }
        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return true; // objeto no numérico no vacío -> truthy
        }
    }
}
