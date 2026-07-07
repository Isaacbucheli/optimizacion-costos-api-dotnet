using System.Globalization;

namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>
/// Catálogo declarativo de todas las hojas de servicio del exportador Excel v3 (código, no
/// plantilla) + la hoja "Detalle de recursos". Cada <see cref="SheetSpec"/> combina columnas de
/// identificación, columnas específicas del servicio y el bloque de columnas base (dinero + estado)
/// que comparten todas las hojas de servicio.
///
/// Las claves de extracción (ej. "os_type", "vm_size") son las mismas que usa el exportador viejo
/// (<c>ClosedXmlCostExcelExporter</c>) en sus queries y métodos Fill*: los datos de la Tarea 7 vienen
/// de esas mismas queries, así que las claves deben coincidir exactamente para no romper el mapeo.
/// </summary>
public static class SheetCatalog
{
    /// <summary>Clave del data source (misma clave que usa el exportador viejo en FetchRowsAsync) → spec de hoja.
    /// El orden de la lista es el orden de las hojas en el workbook.</summary>
    public static IReadOnlyList<(string DataKey, SheetSpec Spec)> ServiceSheets() =>
    [
        ("vms", VmsSheet()),
        ("disks", StoppedDisksSheet()),
        ("disks", OrphanDisksSheet()),
        ("sql", SqlDatabaseSheet()),
        ("sql_managed_instance", SqlManagedInstanceSheet()),
        ("appservice", AppServicePlansSheet()),
        ("mysql", MysqlFlexibleSheet()),
        ("cosmos", CosmosDbSheet()),
        ("redis", RedisSheet()),
        ("public_ip", PublicIpSheet()),
    ];

    /// <summary>Hoja genérica (identificación + columnas base, sin específicas) para servicios del
    /// catálogo sin spec propio (p.ej. synapse_dw) o cualquier service_key futuro sin mapeo dedicado.</summary>
    public static SheetSpec GenericServiceSheet(string sheetName) =>
        new(sheetName, [.. Identity(), .. BaseColumns()]);

    /// <summary>Hoja "Detalle de recursos": una fila por cada resultado de costo del análisis,
    /// cualquiera sea su servicio. ComparableTotals = true (mismo bloque de totales que las demás).</summary>
    public static SheetSpec ResourceDetailSheet()
    {
        var columns = new List<ColumnSpec>
        {
            new("Servicio", ColKind.Text, r => Get(r, "display_name") ?? Get(r, "service_key")),
            new("Recurso", ColKind.Text, r => Get(r, "resource_name")),
            new("Tipo Azure", ColKind.Text, r => Get(r, "resource_type")),
            new("Suscripción", ColKind.Text, r => Get(r, "subscription_name")),
            new("Grupo de recursos", ColKind.Text, r => Get(r, "resource_group")),
            new("Región", ColKind.Text, r => Get(r, "location")),
        };
        columns.AddRange(BaseColumns());
        return new SheetSpec("Detalle de recursos", columns, ComparableTotals: true);
    }

    // =================================================================================
    // Hojas por servicio
    // =================================================================================

    private static SheetSpec VmsSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        // Claves verificadas contra FillVms viejo (ClosedXmlCostExcelExporter.cs líneas 580-586):
        // "vm_size" (vm_details) con fallback a "sku_name" (azure_resources) cuando no hay detalle.
        columns.Add(new("SO", ColKind.Text, r => Get(r, "os_type")));
        columns.Add(new("Tamaño", ColKind.Text, r => Get(r, "vm_size") ?? Get(r, "sku_name")));
        columns.Add(new("vCPUs", ColKind.Number, r => Get(r, "vcpu_count") ?? VcpuFromSize(Get(r, "vm_size") ?? Get(r, "sku_name"))));
        columns.Add(new("Estado VM", ColKind.Text, r => Get(r, "power_state") ?? Get(r, "status")));
        // "Licencia SQL": el viejo exportador arma esta etiqueta con SqlLicenseLabel() sobre
        // sql_vm_license_type (sql_vm_details) con fallback a sql_license_type (vm_details) —
        // línea 577. No existe una clave "sql_license" en ninguna query ni tabla del repo .NET
        // (verificado con búsqueda literal); se usan las claves reales del viejo exportador.
        columns.Add(new("Licencia SQL", ColKind.Text, r => Get(r, "sql_vm_license_type") ?? Get(r, "sql_license_type")));
        columns.Add(new("AHB $", ColKind.Money, r => Get(r, "ahb_discount_monthly")));
        columns.AddRange(BaseColumns());
        // Claves reales del exportador viejo (pu.running_hours, pu.uptime_pct de vm_power_usage;
        // ver AppendPowerColumns en ClosedXmlCostExcelExporter.cs línea 555) — NO llevan prefijo "power_".
        columns.Add(new("Horas ON (mes ant.)", ColKind.Hours, r => Get(r, "running_hours")));
        columns.Add(new("% Uptime (mes ant.)", ColKind.Percent, r => AsPercentFraction(Get(r, "uptime_pct"))));
        return new SheetSpec("Optimización VMs", columns);
    }

    /// <summary>Discos cuya VM dueña está apagada/deallocated (mismo criterio del FillVms viejo,
    /// zona de discos apagados). El filtrado de filas ocurre en la Tarea 7; aquí solo se declara la
    /// spec de columnas.</summary>
    private static SheetSpec StoppedDisksSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("Disco", ColKind.Text, r => Get(r, "resource_name")));
        columns.Add(new("VM dueña", ColKind.Text, r => Get(r, "attached_vm_name")));
        columns.Add(new("Tier", ColKind.Text, r => Get(r, "disk_sku") ?? Get(r, "sku_name") ?? Get(r, "disk_tier")));
        columns.Add(new("GB", ColKind.Number, r => Get(r, "disk_size_gb")));
        columns.AddRange(BaseColumns());
        return new SheetSpec("VMs apagadas – discos", columns);
    }

    /// <summary>Discos huérfanos (sin VM dueña activa) o de ASR (DiskRole del exportador viejo,
    /// línea 176). El filtrado de filas ocurre en la Tarea 7.</summary>
    private static SheetSpec OrphanDisksSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("Rol", ColKind.Text, r => DiskRole(Get(r, "resource_name") as string ?? "")));
        columns.Add(new("Tier", ColKind.Text, r => Get(r, "disk_sku") ?? Get(r, "sku_name") ?? Get(r, "disk_tier")));
        columns.Add(new("GB", ColKind.Number, r => Get(r, "disk_size_gb")));
        columns.AddRange(BaseColumns());
        return new SheetSpec("Discos huérfanos y ASR", columns);
    }

    private static SheetSpec SqlDatabaseSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("Servidor", ColKind.Text, r => Get(r, "server_name")));
        columns.Add(new("Tier/SKU", ColKind.Text, r => Get(r, "detail_sku_name") ?? Get(r, "sku_name")));
        columns.Add(new("Modelo", ColKind.Text, r => SqlPurchaseModel(r)));
        columns.AddRange(BaseColumns());
        return new SheetSpec("SQL Database", columns);
    }

    private static SheetSpec SqlManagedInstanceSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("vCores", ColKind.Number, r => Get(r, "vcore_count")));
        columns.Add(new("Tier", ColKind.Text, r => Get(r, "sku_tier")));
        columns.AddRange(BaseColumns());
        return new SheetSpec("SQL Managed Instance", columns);
    }

    private static SheetSpec AppServicePlansSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("SKU", ColKind.Text, r => Get(r, "detail_sku_name") ?? Get(r, "sku_name")));
        columns.Add(new("Instancias", ColKind.Number, r => Get(r, "sku_capacity")));
        // NOTA: el exportador viejo (FillAppService, línea 740) no cuenta apps por plan (deja "" fijo);
        // no existe una clave verificada para esto todavía. Se deja "app_count" como key esperada a
        // futuro (Tarea 7 deberá agregar el conteo a la query si se quiere poblar esta columna).
        columns.Add(new("Apps", ColKind.Number, r => Get(r, "app_count")));
        columns.AddRange(BaseColumns());
        return new SheetSpec("App Service Plans", columns);
    }

    private static SheetSpec MysqlFlexibleSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("SKU", ColKind.Text, r => Get(r, "detail_sku_name") ?? Get(r, "sku_name")));
        columns.Add(new("Tier", ColKind.Text, r => Get(r, "sku_tier")));
        columns.Add(new("Storage GB", ColKind.Number, r => Get(r, "storage_size_gb")));
        columns.AddRange(BaseColumns());
        return new SheetSpec("MySQL Flexible", columns);
    }

    private static SheetSpec CosmosDbSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("Modo", ColKind.Text, r => CosmosMode(r)));
        columns.Add(new("Regiones", ColKind.Number, r => Get(r, "region_count")));
        columns.AddRange(BaseColumns());
        return new SheetSpec("Cosmos DB", columns);
    }

    private static SheetSpec RedisSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("SKU/Tier", ColKind.Text, r => Get(r, "detail_sku_name") ?? Get(r, "sku_name")));
        columns.Add(new("Capacidad", ColKind.Number, r => Get(r, "sku_capacity")));
        columns.AddRange(BaseColumns());
        return new SheetSpec("Redis", columns);
    }

    private static SheetSpec PublicIpSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("Dirección IP", ColKind.Text, r => Get(r, "ip_address")));
        columns.Add(new("Versión", ColKind.Text, r => Get(r, "ip_version")));
        columns.Add(new("Asociada a", ColKind.Text, r => Get(r, "associated_to")));
        columns.AddRange(BaseColumns());
        return new SheetSpec("IP Pública", columns);
    }

    // =================================================================================
    // Builders privados compartidos (DRY)
    // =================================================================================

    /// <summary>Columnas de identificación: primeras columnas de toda hoja (de servicio o detalle).</summary>
    private static List<ColumnSpec> Identity() =>
    [
        new("Recurso", ColKind.Text, r => Get(r, "resource_name")),
        new("Grupo de recursos", ColKind.Text, r => Get(r, "resource_group")),
        new("Región", ColKind.Text, r => Get(r, "location")),
    ];

    /// <summary>Columnas base de dinero/estado que comparten todas las hojas de servicio, en el
    /// orden fijo con el que se muestran al final de cada hoja.</summary>
    private static List<ColumnSpec> BaseColumns() =>
    [
        new("Reservado", ColKind.Text, r => CostLabels.ReservedLabel(r)),
        new("PAYG mes", ColKind.Money, r => Get(r, "payg_monthly") ?? Get(r, "manual_monthly_cost"), Role: MoneyRole.Payg),
        new("RI 1A mes", ColKind.Money, r => Get(r, "ri_1y_monthly"), Role: MoneyRole.Ri1),
        new("RI 3A mes", ColKind.Money, r => Get(r, "ri_3y_monthly"), Role: MoneyRole.Ri3),
        new("Ahorro 1A $", ColKind.Money, r => Get(r, "savings_1y_monthly"), Role: MoneyRole.Savings1Usd),
        new("Ahorro 3A $", ColKind.Money, r => Get(r, "savings_3y_monthly"), Role: MoneyRole.Savings3Usd),
        new("Ahorro 1A %", ColKind.Percent, r => Get(r, "savings_1y_pct"), Role: MoneyRole.Savings1Pct),
        new("Ahorro 3A %", ColKind.Percent, r => Get(r, "savings_3y_pct"), Role: MoneyRole.Savings3Pct),
        new("Estado del cálculo", ColKind.Text, r => CostLabels.StatusEs(Get(r, "calculation_status") as string)),
        new("Origen del precio", ColKind.Text, r => CostLabels.PriceOrigin(r)),
        new("Nota", ColKind.Text, r => CostLabels.NoteEs(r)),
    ];

    // =================================================================================
    // Helpers de extracción (ports puntuales del exportador viejo)
    // =================================================================================

    private static object? Get(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) ? v : null;

    /// <summary>Port literal de VcpuFromSize (ClosedXmlCostExcelExporter.cs línea ~160): extrae el
    /// número del 2º token de "Standard_D4s_v5" -&gt; 4 (o null si no hay tamaño o no hay dígitos).</summary>
    private static object? VcpuFromSize(object? sizeValue)
    {
        var size = sizeValue as string;
        if (string.IsNullOrEmpty(size)) return null;
        var parts = size.Split('_');
        if (parts.Length < 2) return null;
        var token = parts[1];
        var digits = "";
        foreach (var ch in token)
        {
            if (char.IsDigit(ch)) digits += ch;
            else if (digits.Length > 0) break;
        }
        return digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Port literal de DiskRole (ClosedXmlCostExcelExporter.cs línea 176).</summary>
    private static string DiskRole(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("asr")) return "ASR (no eliminar)";
        return "Huérfano";
    }

    /// <summary>Modelo de compra SQL (DTU/vCore), mismo criterio que FillSql viejo (línea ~691).</summary>
    private static string SqlPurchaseModel(IDictionary<string, object?> row)
    {
        var sku = (Get(row, "detail_sku_name") ?? Get(row, "sku_name"))?.ToString() ?? "";
        var tierObj = Get(row, "sku_tier");
        var tier = tierObj is not null && !string.IsNullOrEmpty(tierObj.ToString()) ? tierObj.ToString()! : sku;
        var tierLower = tier.ToLowerInvariant();
        var isDtuTier = tierLower is "basic" or "standard" or "premium";
        return isDtuTier && !sku.ToLowerInvariant().Contains("gen") ? "DTU" : "vCore";
    }

    /// <summary>
    /// Modo Cosmos DB. El exportador viejo (FillCosmos, línea 884) solo distingue
    /// serverless/provisioned via "is_serverless"; no existe una clave "is_autoscale" verificada en
    /// ninguna query del viejo exportador ni en el resto del repo .NET. Se agrega el caso "autoscale"
    /// para cuando la Tarea 7 amplíe la query de cosmos_details con ese dato; hasta entonces esta
    /// rama nunca se activa (AsBool sobre una clave ausente es false).
    /// </summary>
    private static string CosmosMode(IDictionary<string, object?> row)
    {
        if (AsBool(Get(row, "is_serverless"))) return "serverless";
        if (AsBool(Get(row, "is_autoscale"))) return "autoscale";
        return "provisioned";
    }

    /// <summary>_percent del exportador viejo, sin el caso "N/A": null se deja null (celda vacía);
    /// si &gt;1 se asume porcentaje entero y se divide entre 100.</summary>
    private static object? AsPercentFraction(object? value)
    {
        if (value is null || value is DBNull) return null;
        var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return d > 1 ? d / 100 : d;
    }

    private static bool AsBool(object? value)
    {
        if (value is null || value is DBNull) return false;
        if (value is bool b) return b;
        return Convert.ToBoolean(value);
    }
}
