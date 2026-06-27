using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Caché SQL de precios en <c>dbo.azure_retail_prices</c>. Port de las funciones de cache
/// de app/pricing/price_repository.py:
///   <c>_query_cached</c>, <c>_is_cache_fresh_for</c>, <c>_is_fetch_query_fresh</c>,
///   <c>_insert_prices_batch</c>, <c>_delete_stale_prices</c>, <c>_delete_prices_by_fetch_query</c>
/// más el ensure de schema (la tabla no existía como CREATE explícito en el FastAPI; aquí se
/// crea de forma idempotente igual que <see cref="AlertCatalog.AlertCatalogSchema"/>).
///
/// <see cref="IPriceCache"/> es síncrona (igual que el pyodbc del Python), pero el
/// <see cref="ISqlConnectionFactory"/> solo expone <c>OpenAsync</c>; se puentea con
/// <c>GetAwaiter().GetResult()</c>. Todo el pipeline de precios es síncrono, así que no hay
/// reentrada de contexto que bloquear.
///
/// SQL SIEMPRE parametrizado. Los nombres de columna del filtro nunca vienen del usuario.
/// </summary>
public sealed class SqlPriceCache : IPriceCache
{
    private const int CacheTtlHours = 24; // CACHE_TTL_HOURS en Python.

    private readonly ISqlConnectionFactory _factory;
    private bool _schemaEnsured;

    public SqlPriceCache(ISqlConnectionFactory factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>
    /// Port de <c>_cache_is_fresh(cached_at, ttl_hours)</c>: lógica pura del TTL, sin BD.
    /// Null => no fresco. Compara contra UtcNow; trata cached_at sin tz como UTC.
    /// </summary>
    public static bool CacheIsFresh(DateTime? cachedAt, int ttlHours = CacheTtlHours)
    {
        if (cachedAt is null)
            return false;
        var value = cachedAt.Value;
        // Normaliza a UTC: una fila Unspecified (lo que devuelve DATETIME2 de SQL) se
        // interpreta como UTC, igual que el replace(tzinfo=utc) del Python.
        var asUtc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        var age = DateTime.UtcNow - asUtc;
        return age < TimeSpan.FromHours(ttlHours);
    }

    // -------------------- IPriceCache --------------------

    public IReadOnlyList<PriceRow> QueryCached(
        string serviceName, string region, string? armSkuName = null, string? skuName = null)
    {
        using var conn = OpenWithSchema();
        using var cmd = conn.CreateCommand();

        var where = BuildFilter(cmd, serviceName, region, armSkuName, skuName);
        cmd.CommandText = $"""
            SELECT price_id, service_name, product_name, sku_name, arm_sku_name,
                   meter_name, meter_id, arm_region_name, price_type, reservation_term,
                   currency_code, unit_price, retail_price, unit_of_measure,
                   tier_minimum_units, is_primary_meter, fetch_query, cached_at
            FROM dbo.azure_retail_prices
            WHERE {where}
            """;

        var rows = new List<PriceRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(Map(reader));
        return rows;
    }

    public bool IsCacheFreshFor(
        string serviceName, string region, string? armSkuName = null, string? skuName = null)
    {
        using var conn = OpenWithSchema();
        using var cmd = conn.CreateCommand();

        var where = BuildFilter(cmd, serviceName, region, armSkuName, skuName);
        cmd.CommandText =
            $"SELECT MAX(cached_at) FROM dbo.azure_retail_prices WHERE {where}";

        return CacheIsFresh(ReadMaxCachedAt(cmd));
    }

    public bool IsFetchQueryFresh(string fetchQuery)
    {
        using var conn = OpenWithSchema();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT MAX(cached_at) FROM dbo.azure_retail_prices WHERE fetch_query = @fetch_query";
        cmd.Parameters.Add(new SqlParameter("@fetch_query", (object?)fetchQuery ?? DBNull.Value));

        return CacheIsFresh(ReadMaxCachedAt(cmd));
    }

    public void Insert(IEnumerable<PriceRow> rows, string fetchQuery)
    {
        var list = rows as IReadOnlyList<PriceRow> ?? rows.ToList();
        if (list.Count == 0)
            return; // No-op si vacío (paridad con _insert_prices_batch).

        using var conn = OpenWithSchema();
        foreach (var item in list)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dbo.azure_retail_prices (
                    service_name, product_name, sku_name, arm_sku_name,
                    meter_name, meter_id, arm_region_name,
                    price_type, reservation_term, currency_code,
                    unit_price, retail_price, unit_of_measure, tier_minimum_units,
                    is_primary_meter, fetch_query
                )
                VALUES (@service_name, @product_name, @sku_name, @arm_sku_name,
                        @meter_name, @meter_id, @arm_region_name,
                        @price_type, @reservation_term, @currency_code,
                        @unit_price, @retail_price, @unit_of_measure, @tier_minimum_units,
                        @is_primary_meter, @fetch_query)
                """;
            AddParam(cmd, "@service_name", item.ServiceName);
            AddParam(cmd, "@product_name", item.ProductName);
            AddParam(cmd, "@sku_name", item.SkuName);
            AddParam(cmd, "@arm_sku_name", item.ArmSkuName);
            AddParam(cmd, "@meter_name", item.MeterName);
            AddParam(cmd, "@meter_id", item.MeterId);
            AddParam(cmd, "@arm_region_name", item.ArmRegionName);
            AddParam(cmd, "@price_type", item.PriceType);
            AddParam(cmd, "@reservation_term", item.ReservationTerm);
            AddParam(cmd, "@currency_code", item.CurrencyCode ?? "USD"); // default "USD" como Python.
            AddParam(cmd, "@unit_price", item.UnitPrice ?? 0d);          // or 0 como Python.
            AddParam(cmd, "@retail_price", item.RetailPrice ?? 0d);      // or 0 como Python.
            AddParam(cmd, "@unit_of_measure", item.UnitOfMeasure);
            AddParam(cmd, "@tier_minimum_units", item.TierMinimumUnits);
            AddParam(cmd, "@is_primary_meter", item.IsPrimaryMeter ? 1 : 0);
            AddParam(cmd, "@fetch_query", fetchQuery);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteStale(
        string serviceName, string region, string? armSkuName = null, string? skuName = null)
    {
        using var conn = OpenWithSchema();
        using var cmd = conn.CreateCommand();

        var where = BuildFilter(cmd, serviceName, region, armSkuName, skuName);
        cmd.CommandText = $"DELETE FROM dbo.azure_retail_prices WHERE {where}";
        cmd.ExecuteNonQuery();
    }

    public void DeleteByFetchQuery(string fetchQuery)
    {
        using var conn = OpenWithSchema();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.azure_retail_prices WHERE fetch_query = @fetch_query";
        cmd.Parameters.Add(new SqlParameter("@fetch_query", (object?)fetchQuery ?? DBNull.Value));
        cmd.ExecuteNonQuery();
    }

    // -------------------- Helpers --------------------

    /// <summary>
    /// Construye el WHERE compartido por _query_cached / _is_cache_fresh_for / _delete_stale_prices:
    /// service_name + arm_region_name siempre; arm_sku_name y/o sku_name si se proveen (no vacíos).
    /// Agrega los parámetros al comando. Los nombres de columna son constantes (no input de usuario).
    /// </summary>
    private static string BuildFilter(
        SqlCommand cmd, string serviceName, string region, string? armSkuName, string? skuName)
    {
        var clauses = new List<string> { "service_name = @service_name", "arm_region_name = @region" };
        cmd.Parameters.Add(new SqlParameter("@service_name", (object?)serviceName ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@region", (object?)region ?? DBNull.Value));

        // Paridad con Python: `if arm_sku_name:` / `if sku_name:` => solo filtra si truthy (no vacío).
        if (!string.IsNullOrEmpty(armSkuName))
        {
            clauses.Add("arm_sku_name = @arm_sku_name");
            cmd.Parameters.Add(new SqlParameter("@arm_sku_name", armSkuName));
        }
        if (!string.IsNullOrEmpty(skuName))
        {
            clauses.Add("sku_name = @sku_name");
            cmd.Parameters.Add(new SqlParameter("@sku_name", skuName));
        }
        return string.Join(" AND ", clauses);
    }

    private static DateTime? ReadMaxCachedAt(SqlCommand cmd)
    {
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : (DateTime)result;
    }

    private static void AddParam(SqlCommand cmd, string name, object? value)
        => cmd.Parameters.Add(new SqlParameter(name, value ?? DBNull.Value));

    private SqlConnection OpenWithSchema()
    {
        // Puente sync sobre el factory async (el pipeline de precios es síncrono).
        var conn = _factory.OpenAsync().GetAwaiter().GetResult();
        if (!_schemaEnsured)
        {
            EnsureSchema(conn);
            _schemaEnsured = true;
        }
        return conn;
    }

    /// <summary>Crea la tabla de cache de precios de forma idempotente.</summary>
    private static void EnsureSchema(SqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.azure_retail_prices', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.azure_retail_prices (
                    price_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                    service_name NVARCHAR(200) NULL,
                    product_name NVARCHAR(300) NULL,
                    sku_name NVARCHAR(200) NULL,
                    arm_sku_name NVARCHAR(200) NULL,
                    meter_name NVARCHAR(300) NULL,
                    meter_id NVARCHAR(100) NULL,
                    arm_region_name NVARCHAR(100) NULL,
                    price_type NVARCHAR(40) NULL,
                    reservation_term NVARCHAR(40) NULL,
                    currency_code NVARCHAR(10) NULL,
                    unit_price FLOAT NULL,
                    retail_price FLOAT NULL,
                    unit_of_measure NVARCHAR(80) NULL,
                    tier_minimum_units FLOAT NULL,
                    is_primary_meter BIT NULL,
                    fetch_query NVARCHAR(300) NULL,
                    raw_json NVARCHAR(MAX) NULL,
                    cached_at DATETIME2 NOT NULL CONSTRAINT DF_azure_retail_prices_cached DEFAULT SYSUTCDATETIME()
                );
                CREATE INDEX IX_azure_retail_prices_lookup
                    ON dbo.azure_retail_prices (service_name, arm_region_name);
                CREATE INDEX IX_azure_retail_prices_fetch_query
                    ON dbo.azure_retail_prices (fetch_query);
            END
            """;
        cmd.ExecuteNonQuery();
    }

    private static PriceRow Map(SqlDataReader r) => new()
    {
        PriceId = r.IsDBNull(0) ? null : r.GetInt64(0),
        ServiceName = r.IsDBNull(1) ? null : r.GetString(1),
        ProductName = r.IsDBNull(2) ? null : r.GetString(2),
        SkuName = r.IsDBNull(3) ? null : r.GetString(3),
        ArmSkuName = r.IsDBNull(4) ? null : r.GetString(4),
        MeterName = r.IsDBNull(5) ? null : r.GetString(5),
        MeterId = r.IsDBNull(6) ? null : r.GetString(6),
        ArmRegionName = r.IsDBNull(7) ? null : r.GetString(7),
        PriceType = r.IsDBNull(8) ? null : r.GetString(8),
        ReservationTerm = r.IsDBNull(9) ? null : r.GetString(9),
        CurrencyCode = r.IsDBNull(10) ? null : r.GetString(10),
        UnitPrice = ReadDouble(r, 11),
        RetailPrice = ReadDouble(r, 12),
        UnitOfMeasure = r.IsDBNull(13) ? null : r.GetString(13),
        TierMinimumUnits = ReadDouble(r, 14),
        IsPrimaryMeter = !r.IsDBNull(15) && r.GetBoolean(15),
        FetchQuery = r.IsDBNull(16) ? null : r.GetString(16),
        CachedAt = r.IsDBNull(17) ? null : r.GetDateTime(17),
    };

    /// <summary>
    /// Lee un numérico tolerante al tipo de columna. CRÍTICO: la tabla azure_retail_prices
    /// real (creada por el FastAPI) usa DECIMAL/NUMERIC; SqlDataReader.GetDouble SOLO acepta
    /// FLOAT y lanza InvalidCastException sobre DECIMAL. En coexistencia .NET+Python comparten
    /// esa tabla, así que hay que convertir desde el tipo real (decimal/double/etc.).
    /// </summary>
    private static double? ReadDouble(SqlDataReader r, int i)
        => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
}
