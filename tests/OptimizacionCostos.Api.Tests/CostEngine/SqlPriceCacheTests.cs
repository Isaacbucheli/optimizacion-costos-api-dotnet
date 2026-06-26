using System.Reflection;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de la lógica PURA de <see cref="SqlPriceCache"/> que no requiere BD: el TTL de
/// freshness (port de <c>_cache_is_fresh</c> de app/pricing/price_repository.py) y el WHERE
/// parametrizado que comparten _query_cached / _is_cache_fresh_for / _delete_stale_prices.
///
/// LÍMITES (no cubierto sin BD):
///   - QueryCached / Insert / DeleteStale / DeleteByFetchQuery / EnsureSchema y el mapeo
///     de filas ejecutan SQL real contra Azure SQL. El factory <see cref="OptimizacionCostos.Api.Data.ISqlConnectionFactory"/>
///     devuelve un SqlConnection concreto (no falseable sin servidor), así que esos caminos
///     se ejercitan en la corrida de integración (BIT_INTEGRATION_DB=1), no aquí.
///   - Lo que SÍ se prueba sin BD: (1) la frontera exacta del TTL de 24h sobre CacheIsFresh,
///     incluida la normalización de Kind=Unspecified→UTC (lo que devuelve DATETIME2 de SQL);
///     (2) que el WHERE se arma con los parámetros correctos según qué filtros opcionales se
///     pasen (vía reflexión sobre el método privado BuildFilter), replicando los `if arm_sku_name:`
///     / `if sku_name:` del Python.
/// </summary>
public sealed class SqlPriceCacheTests
{
    // ============ TTL / freshness (CacheIsFresh) ============

    [Fact]
    public void CacheIsFresh_Null_NoEsFresco()
    {
        // Python: cached_at is None -> False.
        Assert.False(SqlPriceCache.CacheIsFresh(null));
    }

    [Fact]
    public void CacheIsFresh_Reciente_EsFresco()
    {
        var hace1h = DateTime.UtcNow.AddHours(-1);
        Assert.True(SqlPriceCache.CacheIsFresh(hace1h));
    }

    [Fact]
    public void CacheIsFresh_MasDe24h_NoEsFresco()
    {
        var hace25h = DateTime.UtcNow.AddHours(-25);
        Assert.False(SqlPriceCache.CacheIsFresh(hace25h));
    }

    [Fact]
    public void CacheIsFresh_JustoBajoLas24h_EsFresco()
    {
        // age < 24h estricto: 23h59m sigue fresco.
        var casi24h = DateTime.UtcNow.AddHours(-23).AddMinutes(-59);
        Assert.True(SqlPriceCache.CacheIsFresh(casi24h));
    }

    [Fact]
    public void CacheIsFresh_UnspecifiedKind_SeTrataComoUtc()
    {
        // DATETIME2 de SQL llega como Kind=Unspecified; el port lo interpreta como UTC
        // (igual que el replace(tzinfo=utc) del Python). Una marca de hace 1h debe ser fresca
        // aun en máquinas con offset local distinto de UTC.
        var unspecifiedHace1h = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-1), DateTimeKind.Unspecified);
        Assert.True(SqlPriceCache.CacheIsFresh(unspecifiedHace1h));

        var unspecifiedHace25h = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-25), DateTimeKind.Unspecified);
        Assert.False(SqlPriceCache.CacheIsFresh(unspecifiedHace25h));
    }

    [Fact]
    public void CacheIsFresh_TtlPersonalizado_RespetaLaVentana()
    {
        var hace2h = DateTime.UtcNow.AddHours(-2);
        Assert.True(SqlPriceCache.CacheIsFresh(hace2h, ttlHours: 3));
        Assert.False(SqlPriceCache.CacheIsFresh(hace2h, ttlHours: 1));
    }

    // ============ WHERE parametrizado (BuildFilter) ============
    // Se ejercita por reflexión: arma un SqlCommand de juguete y verifica el texto del WHERE
    // y los parámetros agregados, sin abrir conexión.

    [Fact]
    public void BuildFilter_SoloServiceYRegion()
    {
        var (where, names) = InvokeBuildFilter("Virtual Machines", "eastus2", null, null);

        Assert.Equal("service_name = @service_name AND arm_region_name = @region", where);
        Assert.Equal(new[] { "@service_name", "@region" }, names);
    }

    [Fact]
    public void BuildFilter_ConArmSkuName_AgregaClausula()
    {
        var (where, names) = InvokeBuildFilter("Virtual Machines", "eastus2", "Standard_D2as_v5", null);

        Assert.Equal(
            "service_name = @service_name AND arm_region_name = @region AND arm_sku_name = @arm_sku_name",
            where);
        Assert.Equal(new[] { "@service_name", "@region", "@arm_sku_name" }, names);
    }

    [Fact]
    public void BuildFilter_ConSkuName_AgregaClausula()
    {
        var (where, names) = InvokeBuildFilter("Storage", "eastus2", null, "P10 LRS");

        Assert.Equal(
            "service_name = @service_name AND arm_region_name = @region AND sku_name = @sku_name",
            where);
        Assert.Equal(new[] { "@service_name", "@region", "@sku_name" }, names);
    }

    [Fact]
    public void BuildFilter_ConAmbosSku_AgregaAmbasClausulas()
    {
        var (where, names) = InvokeBuildFilter("Azure App Service", "eastus2", "P1v3", "P1v3");

        Assert.Equal(
            "service_name = @service_name AND arm_region_name = @region "
            + "AND arm_sku_name = @arm_sku_name AND sku_name = @sku_name",
            where);
        Assert.Equal(new[] { "@service_name", "@region", "@arm_sku_name", "@sku_name" }, names);
    }

    [Fact]
    public void BuildFilter_StringsVacios_NoAgreganClausula()
    {
        // Paridad con `if arm_sku_name:` / `if sku_name:` de Python: "" es falsy => no filtra.
        var (where, names) = InvokeBuildFilter("Virtual Machines", "eastus2", "", "");

        Assert.Equal("service_name = @service_name AND arm_region_name = @region", where);
        Assert.Equal(new[] { "@service_name", "@region" }, names);
    }

    // -------------------- Helpers de reflexión --------------------

    private static (string Where, string[] ParamNames) InvokeBuildFilter(
        string serviceName, string region, string? armSkuName, string? skuName)
    {
        var method = typeof(SqlPriceCache).GetMethod(
            "BuildFilter", BindingFlags.NonPublic | BindingFlags.Static)!;
        // SqlCommand sin conexión: solo se le agregan parámetros; no se ejecuta.
        using var cmd = new Microsoft.Data.SqlClient.SqlCommand();
        var where = (string)method.Invoke(null, new object?[] { cmd, serviceName, region, armSkuName, skuName })!;
        var names = cmd.Parameters.Cast<Microsoft.Data.SqlClient.SqlParameter>()
            .Select(p => p.ParameterName).ToArray();
        return (where, names);
    }
}
