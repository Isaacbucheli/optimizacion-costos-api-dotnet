using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.Inventory;

/// <summary>
/// Inserters de inventario. Port 1:1 de app/inventory_inserters.py: crea las filas en
/// dbo.azure_resources y en la tabla de detalle por servicio a partir de una fila de Resource
/// Graph. Opera sobre la conexión/transacción del import (no abre la suya).
/// </summary>
public static class InventoryInserter
{
    /// <summary>12 tablas de detalle conocidas (KNOWN_DETAIL_TABLES).</summary>
    public static readonly string[] KnownDetailTables =
    [
        "vm_details", "disk_details", "sql_db_details", "appservice_plan_details",
        "mysql_flex_details", "cosmos_details", "redis_details", "public_ip_details",
        "sql_vm_details", "sql_managed_instance_details", "elastic_pool_details", "snapshot_details",
    ];

    /// <summary>inserter_key registrados (equivale a las claves de DETAIL_INSERTERS de Python).</summary>
    public static readonly string[] InserterKeys =
    [
        "vm", "disk", "sql_db", "appservice_plan", "mysql_flex", "cosmos",
        "redis", "public_ip", "sql_vm", "sql_managed_instance", "elastic_pool", "snapshot",
    ];

    private static readonly HashSet<string> DtuPoolTiers = new(StringComparer.OrdinalIgnoreCase) { "basic", "standard", "premium" };

    // Port de ensure_inventory_schema (crea sql_managed_instance_details + elastic_pool_details).
    public static async Task EnsureSchemaAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            IF OBJECT_ID('dbo.sql_managed_instance_details', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.sql_managed_instance_details (
                    sql_mi_detail_id INT IDENTITY(1,1) PRIMARY KEY,
                    resource_id INT NOT NULL,
                    sku_name NVARCHAR(100) NULL, sku_tier NVARCHAR(100) NULL, sku_family NVARCHAR(100) NULL,
                    vcore_count INT NULL, storage_size_gb INT NULL, license_type NVARCHAR(100) NULL,
                    collation NVARCHAR(200) NULL, zone_redundant BIT NULL, public_data_endpoint_enabled BIT NULL,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT FK_sql_managed_instance_details_resources FOREIGN KEY (resource_id) REFERENCES dbo.azure_resources(resource_id)
                );
            END
            IF OBJECT_ID('dbo.elastic_pool_details', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.elastic_pool_details (
                    elastic_pool_detail_id INT IDENTITY(1,1) PRIMARY KEY,
                    resource_id INT NOT NULL,
                    server_name NVARCHAR(200) NULL, sku_name NVARCHAR(100) NULL, sku_tier NVARCHAR(100) NULL,
                    sku_family NVARCHAR(100) NULL, capacity INT NULL, compute_model NVARCHAR(20) NULL,
                    per_db_min FLOAT NULL, per_db_max FLOAT NULL, zone_redundant BIT NULL, license_type NVARCHAR(100) NULL,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT FK_elastic_pool_details_resources FOREIGN KEY (resource_id) REFERENCES dbo.azure_resources(resource_id)
                );
            END
            IF OBJECT_ID('dbo.snapshot_details', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.snapshot_details (
                    snapshot_detail_id INT IDENTITY(1,1) PRIMARY KEY,
                    resource_id INT NOT NULL,
                    snapshot_sku NVARCHAR(100) NULL,
                    disk_size_gb INT NULL,
                    incremental BIT NULL,
                    time_created DATETIME2 NULL,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT FK_snapshot_details_resources FOREIGN KEY (resource_id) REFERENCES dbo.azure_resources(resource_id)
                );
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Port de insert_azure_resource → devuelve el resource_id insertado.
    public static async Task<int> InsertResourceAsync(
        SqlConnection conn, SqlTransaction tx, int analysisId,
        IReadOnlyDictionary<string, string?> subNameMap, RgRow row, string serviceCategory, CancellationToken ct)
    {
        var subId = row.Str("subscriptionId");
        var properties = row.Node("properties");
        var propertiesJson = properties is JsonObject or JsonArray ? properties.ToJsonString() : null;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO dbo.azure_resources (
                analysis_id, azure_resource_id, subscription_id, subscription_name,
                resource_group, resource_name, resource_type, service_category,
                location, region_name, sku_name, sku_tier, size_name, status,
                os_type, license_type, properties_json, is_active)
            OUTPUT INSERTED.resource_id
            VALUES (@analysis, @rid, @sub, @subName, @rg, @name, @type, @cat,
                    @loc, @region, @sku, @tier, @size, @status, @os, @lic, @props, 1)
            """;
        void P(string n, object? v) => cmd.Parameters.Add(new SqlParameter(n, v ?? DBNull.Value));
        P("@analysis", analysisId);
        P("@rid", row.Str("id"));
        P("@sub", subId);
        P("@subName", subId is not null && subNameMap.TryGetValue(subId, out var sn) ? sn : null);
        P("@rg", row.Str("resourceGroup"));
        P("@name", row.Str("name"));
        P("@type", row.Str("type"));
        P("@cat", serviceCategory);
        P("@loc", row.Str("location"));
        P("@region", row.Str("location"));
        P("@sku", row.Str("skuName") ?? row.Str("vmSize"));
        P("@tier", row.Str("skuTier"));
        P("@size", row.Str("vmSize") ?? row.Str("skuName"));
        P("@status", row.Str("powerState") ?? row.Str("diskState"));
        P("@os", row.Str("osType"));
        P("@lic", row.Str("licenseType"));
        P("@props", propertiesJson);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    // Port de insert_detail: despacha por inserter_key.
    public static async Task InsertDetailAsync(
        SqlConnection conn, SqlTransaction tx, string inserterKey, int resourceId, RgRow row, CancellationToken ct)
    {
        Func<SqlConnection, SqlTransaction, int, RgRow, CancellationToken, Task>? inserter = inserterKey switch
        {
            "vm" => Vm, "disk" => Disk, "sql_db" => SqlDb, "appservice_plan" => AppServicePlan,
            "mysql_flex" => MySqlFlex, "cosmos" => Cosmos, "redis" => Redis, "public_ip" => PublicIp,
            "sql_vm" => SqlVm, "sql_managed_instance" => SqlMi, "elastic_pool" => ElasticPool, "snapshot" => Snapshot,
            _ => null,
        };
        if (inserter is null)
            throw new ArgumentException($"No inserter registered for key: {inserterKey}");
        await inserter(conn, tx, resourceId, row, ct);
    }

    private static SqlCommand New(SqlConnection conn, SqlTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static object Bit(bool? b) => b is null ? DBNull.Value : (b.Value ? 1 : 0);
    private static object Nz(object? v) => v ?? DBNull.Value;

    private static async Task Vm(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        await using var cmd = New(c, t, """
            INSERT INTO dbo.vm_details (resource_id, vm_size, power_state, os_type, os_license_benefit,
                vcpu_count, memory_gb, has_sql_server, sql_edition, sql_license_type)
            VALUES (@rid, @size, @power, @os, @lic, NULL, NULL, 0, NULL, NULL)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@size", r.Str("vmSize") ?? ""),
            new SqlParameter("@power", Nz(r.Str("powerState"))), new SqlParameter("@os", Nz(r.Str("osType"))),
            new SqlParameter("@lic", Nz(r.Str("licenseType"))),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task Disk(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        var managedBy = r.Str("managedByVm");
        var attached = managedBy?.Split('/').LastOrDefault();
        await using var cmd = New(c, t, """
            INSERT INTO dbo.disk_details (resource_id, disk_sku, disk_tier, disk_size_gb,
                disk_iops, disk_mbps, managed_by, attached_vm_name, disk_category)
            VALUES (@rid, @sku, @tier, @size, @iops, @mbps, @mby, @avm, @cat)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@sku", Nz(r.Str("skuName"))),
            new SqlParameter("@tier", Nz(r.Str("skuTier"))), new SqlParameter("@size", Nz(r.Int("diskSizeGB"))),
            new SqlParameter("@iops", Nz(r.Int("diskIOPS"))), new SqlParameter("@mbps", Nz(r.Int("diskMBps"))),
            new SqlParameter("@mby", Nz(managedBy)), new SqlParameter("@avm", Nz(attached)),
            new SqlParameter("@cat", managedBy is not null ? "attached" : "unattached"),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SqlDb(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        var maxBytes = r.Long("maxSizeBytes");
        var maxGb = maxBytes is not null ? (object)Math.Round(maxBytes.Value / (1024d * 1024 * 1024), 2) : DBNull.Value;
        var sku = r.Str("skuName") ?? "";
        var computeTier = sku.Contains("_S_") || sku.EndsWith("_S") ? "Serverless" : "Provisioned";
        var elasticPool = r.Str("elasticPoolName");
        await using var cmd = New(c, t, """
            INSERT INTO dbo.sql_db_details (resource_id, server_name, sku_name, sku_tier, sku_capacity,
                compute_tier, max_size_gb, collation, zone_redundant, license_type, elastic_pool_name)
            VALUES (@rid, @srv, @sku, @tier, @cap, @ctier, @maxgb, @coll, @zr, @lic, @ep)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@srv", Nz(r.Str("serverName"))),
            new SqlParameter("@sku", Nz(r.Str("skuName"))), new SqlParameter("@tier", Nz(r.Str("skuTier"))),
            new SqlParameter("@cap", Nz(r.Int("skuCapacity"))), new SqlParameter("@ctier", computeTier),
            new SqlParameter("@maxgb", maxGb), new SqlParameter("@coll", Nz(r.Str("collation"))),
            new SqlParameter("@zr", Bit(r.BoolN("zoneRedundant"))), new SqlParameter("@lic", Nz(r.Str("licenseType"))),
            new SqlParameter("@ep", string.IsNullOrEmpty(elasticPool) ? DBNull.Value : elasticPool),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AppServicePlan(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        await using var cmd = New(c, t, """
            INSERT INTO dbo.appservice_plan_details (resource_id, sku_name, sku_tier, sku_capacity,
                is_linux, per_site_scaling, zone_redundant, kind)
            VALUES (@rid, @sku, @tier, @cap, @linux, @pss, @zr, @kind)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@sku", Nz(r.Str("skuName"))),
            new SqlParameter("@tier", Nz(r.Str("skuTier"))), new SqlParameter("@cap", Nz(r.Int("skuCapacity"))),
            new SqlParameter("@linux", r.BoolFalse("isLinux") ? 1 : 0), new SqlParameter("@pss", Bit(r.BoolN("perSiteScaling"))),
            new SqlParameter("@zr", Bit(r.BoolN("zoneRedundant"))), new SqlParameter("@kind", Nz(r.Str("kind"))),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MySqlFlex(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        await using var cmd = New(c, t, """
            INSERT INTO dbo.mysql_flex_details (resource_id, mysql_version, sku_name, sku_tier,
                storage_size_gb, storage_iops, storage_autogrow, ha_enabled, ha_mode, backup_retention_days, geo_redundant_backup)
            VALUES (@rid, @ver, @sku, @tier, @ssize, @siops, @sauto, @ha, @hamode, @bkp, @geo)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@ver", Nz(r.Str("mysqlVersion"))),
            new SqlParameter("@sku", Nz(r.Str("skuName"))), new SqlParameter("@tier", Nz(r.Str("skuTier"))),
            new SqlParameter("@ssize", Nz(r.Int("storageSizeGB"))), new SqlParameter("@siops", Nz(r.Int("storageIops"))),
            new SqlParameter("@sauto", Nz(r.Str("storageAutogrow"))), new SqlParameter("@ha", r.BoolFalse("haEnabled") ? 1 : 0),
            new SqlParameter("@hamode", Nz(r.Str("haMode"))), new SqlParameter("@bkp", Nz(r.Int("backupRetentionDays"))),
            new SqlParameter("@geo", Nz(r.Str("geoRedundantBackup"))),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task Cosmos(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        await using var cmd = New(c, t, """
            INSERT INTO dbo.cosmos_details (resource_id, api_kind, default_experience, consistency_level,
                is_serverless, backup_policy_type, multi_region_writes, region_count)
            VALUES (@rid, @api, @exp, @cons, @sl, @bkp, @mrw, @rc)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@api", Nz(r.Str("apiKind"))),
            new SqlParameter("@exp", Nz(r.Str("defaultExperience"))), new SqlParameter("@cons", Nz(r.Str("consistencyLevel"))),
            new SqlParameter("@sl", r.BoolFalse("isServerless") ? 1 : 0), new SqlParameter("@bkp", Nz(r.Str("backupPolicyType"))),
            new SqlParameter("@mrw", r.BoolFalse("multiRegionWrites") ? 1 : 0), new SqlParameter("@rc", Nz(r.Int("regionCount"))),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task Redis(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        await using var cmd = New(c, t, """
            INSERT INTO dbo.redis_details (resource_id, sku_name, sku_family, sku_capacity,
                redis_version, shard_count, enable_non_ssl_port)
            VALUES (@rid, @sku, @fam, @cap, @ver, @shard, @nonssl)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@sku", Nz(r.Str("skuName"))),
            new SqlParameter("@fam", Nz(r.Str("skuFamily"))), new SqlParameter("@cap", Nz(r.Int("skuCapacity"))),
            new SqlParameter("@ver", Nz(r.Str("redisVersion"))), new SqlParameter("@shard", Nz(r.Int("shardCount"))),
            new SqlParameter("@nonssl", Bit(r.BoolN("enableNonSslPort"))),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task PublicIp(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        var assoc = r.Str("associatedTo");
        await using var cmd = New(c, t, """
            INSERT INTO dbo.public_ip_details (resource_id, ip_address, ip_version, sku_name, sku_tier,
                allocation_method, associated_to, is_orphan)
            VALUES (@rid, @ip, @ver, @sku, @tier, @alloc, @assoc, @orphan)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@ip", Nz(r.Str("ipAddress"))),
            new SqlParameter("@ver", Nz(r.Str("ipVersion"))), new SqlParameter("@sku", Nz(r.Str("skuName"))),
            new SqlParameter("@tier", Nz(r.Str("skuTier"))), new SqlParameter("@alloc", Nz(r.Str("allocationMethod"))),
            new SqlParameter("@assoc", string.IsNullOrEmpty(assoc) ? DBNull.Value : assoc),
            new SqlParameter("@orphan", string.IsNullOrEmpty(assoc) ? 1 : 0),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SqlVm(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        await using var cmd = New(c, t, """
            INSERT INTO dbo.sql_vm_details (resource_id, vm_azure_resource_id,
                sql_license_type, sql_image_sku, sql_image_offer, sql_management)
            VALUES (@rid, @vmid, @lic, @sku, @offer, @mgmt)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@vmid", Nz(r.Str("vmResourceId"))),
            new SqlParameter("@lic", Nz(r.Str("sqlLicenseType"))), new SqlParameter("@sku", Nz(r.Str("sqlImageSku"))),
            new SqlParameter("@offer", Nz(r.Str("sqlImageOffer"))), new SqlParameter("@mgmt", Nz(r.Str("sqlManagement"))),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SqlMi(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        await using var cmd = New(c, t, """
            INSERT INTO dbo.sql_managed_instance_details (resource_id, sku_name, sku_tier, sku_family, vcore_count,
                storage_size_gb, license_type, collation, zone_redundant, public_data_endpoint_enabled)
            VALUES (@rid, @sku, @tier, @fam, @vc, @ssize, @lic, @coll, @zr, @pde)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@sku", Nz(r.Str("skuName"))),
            new SqlParameter("@tier", Nz(r.Str("skuTier"))), new SqlParameter("@fam", Nz(r.Str("skuFamily"))),
            new SqlParameter("@vc", Nz(r.Int("vCores"))), new SqlParameter("@ssize", Nz(r.Int("storageSizeGB"))),
            new SqlParameter("@lic", Nz(r.Str("licenseType"))), new SqlParameter("@coll", Nz(r.Str("collation"))),
            new SqlParameter("@zr", Bit(r.BoolN("zoneRedundant"))), new SqlParameter("@pde", Bit(r.BoolN("publicDataEndpointEnabled"))),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ElasticPool(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        var tier = r.Str("skuTier") ?? "";
        var computeModel = DtuPoolTiers.Contains(tier) ? "DTU" : "vCore";
        await using var cmd = New(c, t, """
            INSERT INTO dbo.elastic_pool_details (resource_id, server_name, sku_name, sku_tier, sku_family,
                capacity, compute_model, per_db_min, per_db_max, zone_redundant, license_type)
            VALUES (@rid, @srv, @sku, @tier, @fam, @cap, @cm, @min, @max, @zr, @lic)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@srv", Nz(r.Str("serverName"))),
            new SqlParameter("@sku", Nz(r.Str("skuName"))), new SqlParameter("@tier", tier),
            new SqlParameter("@fam", Nz(r.Str("skuFamily"))), new SqlParameter("@cap", Nz(r.Int("skuCapacity"))),
            new SqlParameter("@cm", computeModel), new SqlParameter("@min", Nz(r.Dbl("perDbMin"))),
            new SqlParameter("@max", Nz(r.Dbl("perDbMax"))), new SqlParameter("@zr", Bit(r.BoolN("zoneRedundant"))),
            new SqlParameter("@lic", Nz(r.Str("licenseType"))),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task Snapshot(SqlConnection c, SqlTransaction t, int rid, RgRow r, CancellationToken ct)
    {
        object timeCreated = DateTime.TryParse(
            r.Str("timeCreated"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var tc) ? tc : DBNull.Value;
        await using var cmd = New(c, t, """
            INSERT INTO dbo.snapshot_details (resource_id, snapshot_sku, disk_size_gb, incremental, time_created)
            VALUES (@rid, @sku, @size, @inc, @tc)
            """);
        cmd.Parameters.AddRange([
            new SqlParameter("@rid", rid), new SqlParameter("@sku", Nz(r.Str("skuName"))),
            new SqlParameter("@size", Nz(r.Int("diskSizeGB"))), new SqlParameter("@inc", Bit(r.BoolN("incremental"))),
            new SqlParameter("@tc", timeCreated),
        ]);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
