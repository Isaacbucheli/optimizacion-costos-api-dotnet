using System.Globalization;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>
/// Fuente de datos del exportador Excel v3 (código, no plantilla): trae las filas de
/// cost_results + azure_resources (+ tablas *_details) y los escenarios ya calculados, listos
/// para que SheetCatalog/SheetWriter/SummarySheetWriter/ScenariosSheetWriter los pinten.
///
/// COPIA CONSCIENTE: <see cref="FetchRowsAsync"/> (con sus queries), <see cref="EnsurePowerUsageSchemaAsync"/>
/// y los helpers <see cref="Number"/>/<see cref="Text"/>/<see cref="Monthly"/>/<see cref="NeutralizeReservedRows"/>
/// están copiados VERBATIM (namespaces adaptados) de <c>ClosedXmlCostExcelExporter.cs</c>
/// (Features/Reports/, NO modificado por esta tarea) para no arriesgar el exportador viejo mientras
/// convive con el v3 detrás del flag EXCEL_EXPORT_ENGINE. El viejo exportador se elimina en el
/// follow-up posterior al switch (cuando el v3 sea el único motor). Única diferencia deliberada:
/// la query "discovered_services" (hoja "Servicios no catalogados") NO se copia ni se ejecuta aquí
/// — queda fuera del entregable v3 por decisión de la Tarea 7.
/// </summary>
public sealed record ExcelV3Data(
    string ClientName,
    string AnalysisName,
    DateTime? CreatedAt,
    Dictionary<string, List<Dictionary<string, object?>>> RowsByService,
    IReadOnlyList<ScenarioV3> Scenarios,
    IReadOnlyList<ScenarioLineV3> BaselineLines,
    double PaygBaselineMonthly);

public interface ICostExcelDataSourceV3
{
    Task<ExcelV3Data> LoadAsync(int analysisId, CancellationToken ct);
}

public sealed class CostExcelDataSourceV3(
    ISqlConnectionFactory factory,
    ICostResultsQuery scenarioQuery) : ICostExcelDataSourceV3
{
    public async Task<ExcelV3Data> LoadAsync(int analysisId, CancellationToken ct)
    {
        var rows = await FetchRowsAsync(analysisId, ct);
        NeutralizeReservedRows(rows);

        var analysis = rows.TryGetValue("analysis", out var aList) && aList.Count > 0 ? aList[0] : null;
        var clientName = Text(Get(analysis, "client_name"));
        var analysisName = Text(Get(analysis, "analysis_name"));
        var createdAt = Get(analysis, "created_at") is DateTime dt ? dt : (DateTime?)null;

        var scenarioDtos = await scenarioQuery.GetScenariosAsync(analysisId, ct);
        var scenarios = scenarioDtos.Select(MapScenario).ToList();

        var results = rows.TryGetValue("results", out var resultsList) ? resultsList : [];
        var paygBaselineMonthly = results.Sum(r => Monthly(r));
        var baselineLines = BuildBaselineLines(results);

        return new ExcelV3Data(clientName, analysisName, createdAt, rows, scenarios, baselineLines, paygBaselineMonthly);
    }

    // =================================================================================
    // Escenarios: DTO de ICostResultsQuery.GetScenariosAsync -> ScenarioV3/ScenarioLineV3.
    // =================================================================================

    private static ScenarioV3 MapScenario(ScenarioReadDto dto)
    {
        var breakdown = dto.Breakdown
            .Select(b => new ScenarioLineV3(b.LineLabel ?? b.ServiceKey ?? "", b.MonthlyCost, b.Note))
            .ToList();
        return new ScenarioV3(
            dto.Number, dto.Name ?? $"Escenario {dto.Number}", dto.Description,
            dto.TotalMonthly, dto.TotalAnnual, dto.SavingsMonthly, dto.SavingsAnnual, dto.SavingsPct,
            breakdown);
    }

    /// <summary>
    /// Baseline PAYG agregado por servicio legible (una línea por service_key visible, ordenada por
    /// costo descendente). "sql_vm" es interno (metadata de SQL Server sobre VM): se agrupa bajo
    /// "vms", igual que VisibleServiceKey/CostLabels en el dashboard (innovacion-CDC/src/lib/costs.ts).
    /// La etiqueta usa display_name (service_catalog) del propio service_key visible cuando existe
    /// una fila con ese key; si no, cae al service_key crudo.
    /// </summary>
    private static List<ScenarioLineV3> BuildBaselineLines(List<Dictionary<string, object?>> results)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in results)
        {
            var rawKey = Text(Get(row, "service_key"));
            if (string.IsNullOrEmpty(rawKey)) continue;
            var visibleKey = VisibleServiceKey(rawKey);
            var monthly = Monthly(row);

            totals[visibleKey] = totals.GetValueOrDefault(visibleKey) + monthly;

            // display_name de la fila propia del service_key visible tiene prioridad (evita que
            // una fila sql_vm "preste" su display_name a la línea agregada de vms).
            if (!labels.ContainsKey(visibleKey) || string.Equals(rawKey, visibleKey, StringComparison.OrdinalIgnoreCase))
            {
                var displayName = Text(Get(row, "display_name"));
                if (!string.IsNullOrEmpty(displayName)) labels[visibleKey] = displayName;
            }
        }

        return totals
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ScenarioLineV3(labels.GetValueOrDefault(kv.Key, kv.Key), kv.Value, null))
            .ToList();
    }

    /// <summary>sql_vm es interno: se agrupa bajo "vms" (mismo criterio que CostLabels.VisibleServiceKey,
    /// privado en esa clase; se replica aquí porque el mapeo también aplica a agregados por servicio).</summary>
    private static string VisibleServiceKey(string key) =>
        string.Equals(key, "sql_vm", StringComparison.OrdinalIgnoreCase) ? "vms" : key;

    // =================================================================================
    // Helpers de conversión (copia VERBATIM de ClosedXmlCostExcelExporter.cs líneas 82-113).
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

    // =================================================================================
    // _neutralize_reserved: copia VERBATIM de ClosedXmlCostExcelExporter.cs líneas 199-217.
    // =================================================================================

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

    // =================================================================================
    // Acceso a datos (copia VERBATIM de ClosedXmlCostExcelExporter.cs líneas 230-405, MENOS la
    // query "discovered_services": fuera del entregable v3, ver comentario del header del archivo).
    // =================================================================================

    private async Task<Dictionary<string, List<Dictionary<string, object?>>>> FetchRowsAsync(
        int analysisId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        // El viejo exportador asegura el esquema de vm_power_usage (LEFT JOIN en la hoja de VMs).
        // Aquí se crea best-effort por si nunca se corrió el refresh de encendido/apagado.
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

        SplitDiskRows(output);
        return output;
    }

    /// <summary>
    /// Decisión B (revisión Tarea 5): separa la única query "disks" en las dos particiones que el
    /// exportador viejo arma en FillDisks (ClosedXmlCostExcelExporter.cs líneas ~614-663) para
    /// poblar dos hojas distintas. Se replican EXACTAMENTE los mismos criterios de filtrado:
    ///   - "disks_stopped_vms": la VM dueña (attached_vm_power_state) está detenida/deallocated.
    ///   - "disks_orphan_asr": disk_category != "attached" O no hay attached_vm_name (Python:
    ///     `not row.get("attached_vm_name")` es True para null, ausente y cadena vacía).
    /// Una fila puede caer en ninguna, una o ambas listas (igual que el viejo: son dos "if"
    /// independientes, no un switch). SheetCatalog.ServiceSheets() consume estas dos claves nuevas
    /// en vez de "disks" para las hojas "VMs apagadas – discos" y "Discos huérfanos y ASR".
    /// </summary>
    private static void SplitDiskRows(Dictionary<string, List<Dictionary<string, object?>>> output)
    {
        var disks = output.TryGetValue("disks", out var d) ? d : [];
        var stoppedVms = new List<Dictionary<string, object?>>();
        var orphanAsr = new List<Dictionary<string, object?>>();

        foreach (var row in disks)
        {
            var attachedState = Text(Get(row, "attached_vm_power_state")).ToLowerInvariant();
            if (attachedState.Contains("stopped") || attachedState.Contains("deallocated"))
                stoppedVms.Add(row);

            var category = Text(Get(row, "disk_category")).ToLowerInvariant();
            var attachedVm = Get(row, "attached_vm_name");
            if (category != "attached" || string.IsNullOrEmpty(attachedVm?.ToString()))
                orphanAsr.Add(row);
        }

        output["disks_stopped_vms"] = stoppedVms;
        output["disks_orphan_asr"] = orphanAsr;
    }

    /// <summary>
    /// Copia VERBATIM (namespace adaptado) de ClosedXmlCostExcelExporter.cs líneas 407-421.
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

    private static object? Get(IDictionary<string, object?>? row, string key) =>
        row is not null && row.TryGetValue(key, out var v) ? v : null;
}
