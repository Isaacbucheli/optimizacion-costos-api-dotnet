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
    /// <summary>Clave del data source (RowsByService de CostExcelDataSourceV3; la mayoría coincide con
    /// las claves del exportador viejo en FetchRowsAsync, excepto "disks_stopped_vms"/"disks_orphan_asr",
    /// que son la partición v3 de la única query "disks" del viejo — ver Decisión B) → spec de hoja.
    /// El orden de la lista es el orden de las hojas en el workbook.</summary>
    public static IReadOnlyList<(string DataKey, SheetSpec Spec)> ServiceSheets() =>
    [
        ("vms", VmsSheet()),
        // Decisión B (Tarea 7): la query "disks" se parte en dos listas ya filtradas en
        // CostExcelDataSourceV3.SplitDiskRows (mismo criterio que FillDisks del exportador viejo)
        // porque una sola lista "disks" no alcanza para dos hojas con criterios de fila distintos.
        ("disks_stopped_vms", StoppedDisksSheet()),
        ("disks_orphan_asr", OrphanDisksSheet()),
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
        columns.AddRange(BaseColumns(includeRi: false)); // discos no son elegibles a RI: sin bloque RI/ahorro
        return new SheetSpec("VMs apagadas – discos", columns);
    }

    /// <summary>Discos huérfanos (sin VM dueña activa) o de ASR. La etiqueta "ASR (no eliminar)" vs
    /// "Huérfano" replica el ternario inline de FillDisks (ClosedXmlCostExcelExporter.cs líneas
    /// 646-647) — NO el método DiskRole (línea 176), que solo distingue OS/Data/Managed Disk y no
    /// se usa en esta hoja. El filtrado de filas (qué cae en "orphan" vs "stopped") lo hace
    /// CostExcelDataSourceV3.SplitDiskRows con el mismo criterio que el exportador viejo.</summary>
    private static SheetSpec OrphanDisksSheet()
    {
        var columns = new List<ColumnSpec>();
        columns.AddRange(Identity());
        columns.Add(new("Rol", ColKind.Text, r => DiskRole(Get(r, "resource_name") as string ?? "")));
        columns.Add(new("Tier", ColKind.Text, r => Get(r, "disk_sku") ?? Get(r, "sku_name") ?? Get(r, "disk_tier")));
        columns.Add(new("GB", ColKind.Number, r => Get(r, "disk_size_gb")));
        columns.AddRange(BaseColumns(includeRi: false)); // discos no son elegibles a RI: sin bloque RI/ahorro
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
        // Decisión A (Tarea 7): se retiró la columna "Apps"/app_count. appservice_plan_details
        // (ver InventoryInserter.cs) solo guarda sku_name, sku_tier, sku_capacity, is_linux,
        // per_site_scaling, zone_redundant, kind — no hay conteo de sitios/apps por plan en ninguna
        // tabla del repo .NET (ni en el exportador viejo, que dejaba la columna fija en ""). Una
        // columna sin dato real es el ruido que este proyecto elimina; se puede reincorporar si algún
        // día se agrega number_of_sites a la ingesta de inventario.
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
        columns.AddRange(BaseColumns(includeRi: false)); // IP pública no es elegible a RI: sin bloque RI/ahorro
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

    /// <summary>Columnas base de dinero/estado que comparten las hojas de servicio, en el orden fijo
    /// con el que se muestran al final de cada hoja.
    ///
    /// <paramref name="includeRi"/> = false OMITE el bloque de Reserved Instances (columna "Reservado"
    /// + RI 1A/3A mes + "Elegible a RI" + los cuatro Ahorros 1A/3A $/%): en recursos que por naturaleza
    /// NO son reservables (discos de VMs apagadas, discos huérfanos/ASR, IP pública) esas columnas
    /// siempre saldrían vacías/0 y solo son ruido que el consultor termina borrando a mano (decisión del
    /// usuario 2026-07-07). Se conserva PAYG + estado; la fila TOTAL de esas hojas suma solo PAYG
    /// (SUBTOTAL 9). La columna "Nota" (texto genérico por recurso, sin valor real) se eliminó de TODAS
    /// las hojas por la misma decisión.</summary>
    private static List<ColumnSpec> BaseColumns(bool includeRi = true)
    {
        var cols = new List<ColumnSpec>();

        // "Reservado" (cobertura de RI ya confirmada) solo aplica a recursos reservables.
        if (includeRi)
            cols.Add(new("Reservado", ColKind.Text, r => CostLabels.ReservedLabel(r)));

        cols.Add(new("PAYG mes", ColKind.Money, r => Get(r, "payg_monthly") ?? Get(r, "manual_monthly_cost"), Role: MoneyRole.Payg));

        if (includeRi)
        {
            cols.Add(new("RI 1A mes", ColKind.Money, r => Get(r, "ri_1y_monthly"), Role: MoneyRole.Ri1));
            cols.Add(new("RI 3A mes", ColKind.Money, r => Get(r, "ri_3y_monthly"), Role: MoneyRole.Ri3));
            // Marcador visible de elegibilidad (spec §3.6, adenda fórmulas vivas): regla canónica de
            // ComparableTotalsCalculator.IsEligible, no una copia — la fórmula viva de Ahorro por fila
            // filtra sobre esta columna en vez de reimplementar el criterio.
            cols.Add(new("Elegible a RI", ColKind.Eligibility, r =>
                ComparableTotalsCalculator.IsEligible(
                    AsDoubleOrNull(Get(r, "ri_1y_monthly")),
                    AsDoubleOrNull(Get(r, "ri_3y_monthly")),
                    string.Equals(Get(r, "ri_coverage") as string, "confirmed", StringComparison.OrdinalIgnoreCase))
                    ? "Sí" : "No",
                Width: 12));
            cols.Add(new("Ahorro 1A $", ColKind.Money, r => Get(r, "savings_1y_monthly"), Role: MoneyRole.Savings1Usd));
            cols.Add(new("Ahorro 3A $", ColKind.Money, r => Get(r, "savings_3y_monthly"), Role: MoneyRole.Savings3Usd));
            cols.Add(new("Ahorro 1A %", ColKind.Percent, r => Get(r, "savings_1y_pct"), Role: MoneyRole.Savings1Pct));
            cols.Add(new("Ahorro 3A %", ColKind.Percent, r => Get(r, "savings_3y_pct"), Role: MoneyRole.Savings3Pct));
        }

        cols.Add(new("Estado del cálculo", ColKind.Text, r => CostLabels.StatusEs(Get(r, "calculation_status") as string)));
        cols.Add(new("Origen del precio", ColKind.Text, r => CostLabels.PriceOrigin(r)));
        return cols; // "Nota" eliminada: texto genérico por recurso sin valor (decisión usuario 2026-07-07).
    }

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
    /// Modo Cosmos DB. Decisión A (Tarea 7): se retiró la rama "autoscale" (key "is_autoscale") que
    /// tenía el catálogo original — cosmos_details (ver InventoryInserter.cs) solo guarda api_kind,
    /// default_experience, consistency_level, is_serverless, backup_policy_type,
    /// multi_region_writes, region_count; no existe ninguna columna de autoscale en el repo .NET ni
    /// en el exportador viejo (FillCosmos, línea 884, que solo distingue serverless/provisioned).
    /// La columna "Modo" en sí SÍ tiene dato real (is_serverless) y se conserva.
    /// </summary>
    private static string CosmosMode(IDictionary<string, object?> row) =>
        AsBool(Get(row, "is_serverless")) ? "serverless" : "provisioned";

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

    /// <summary>Convierte un valor proveniente de SQL (object?) a double?, defensivamente. Mismo
    /// criterio que el helper privado de SheetWriter.AsDouble (no se comparte porque son archivos
    /// distintos con este mismo patrón local ya establecido en el catálogo).</summary>
    private static double? AsDoubleOrNull(object? value)
    {
        if (value is null || value is DBNull) return null;
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }
}
