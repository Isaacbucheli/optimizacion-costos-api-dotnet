using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de SELECCIÓN de precio/GB de snapshot de managed disk (<see cref="SqlPriceRepository.GetSnapshotPricePerGb"/>).
/// Meters reales del Retail API (eastus): "Standard HDD Managed Disks" / "LRS Snapshots" = 0.05;
/// "Premium SSD Managed Disks" / "LRS Snapshots" = 0.132. Unidad "1 GB/Month".
///
/// Igual que <see cref="PriceSelection_Managed_diskTests"/>: la cache se marca fresca
/// (<c>IsCacheFreshForFn</c> / <c>IsFetchQueryFreshFn</c> = true) para que no se dispare el fetch,
/// y <c>QueryCachedFn</c> devuelve directamente las filas "cacheadas".
/// </summary>
public sealed class PriceSelection_SnapshotTests
{
    private static SqlPriceRepository BuildRepo(FakePriceCache cache) =>
        new SqlPriceRepository(cache, new FakeRetailPriceClient(), new FakePricingConstants());

    private static readonly (string, object?)[][] Cached =
    {
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Standard HDD Managed Disks"),
            ("meter_name", "LRS Snapshots"), ("price_type", "Consumption"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.05),
        },
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Premium SSD Managed Disks"),
            ("meter_name", "LRS Snapshots"), ("price_type", "Consumption"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.132),
        },
    };

    private static FakePriceCache CacheWith(IReadOnlyList<PriceRow> rows) => new()
    {
        IsCacheFreshForFn = (_, _, _, _) => true,
        IsFetchQueryFreshFn = _ => true,
        QueryCachedFn = (_, _, _, _) => rows,
    };

    [Fact]
    public void StandardLrs_TomaStandardHddLrsSnapshots()
    {
        var cache = CacheWith(PriceRowFactory.Many(Cached));
        var repo = BuildRepo(cache);
        Assert.Equal(0.05, repo.GetSnapshotPricePerGb("eastus", "Standard_LRS"));
    }

    [Fact]
    public void PremiumLrs_TomaPremiumSsdLrsSnapshots()
    {
        var cache = CacheWith(PriceRowFactory.Many(Cached));
        var repo = BuildRepo(cache);
        Assert.Equal(0.132, repo.GetSnapshotPricePerGb("eastus", "Premium_LRS"));
    }

    [Fact]
    public void SkuNulo_DefaultAStandardHdd()
    {
        var cache = CacheWith(PriceRowFactory.Many(Cached));
        var repo = BuildRepo(cache);
        Assert.Equal(0.05, repo.GetSnapshotPricePerGb("eastus", null));
    }

    [Fact]
    public void SinMeter_DevuelveNull()
    {
        var cache = CacheWith(System.Array.Empty<PriceRow>());
        var repo = BuildRepo(cache);
        Assert.Null(repo.GetSnapshotPricePerGb("eastus", "Standard_LRS"));
    }

    // -------------------- FIX 3: nunca aceptar un retail_price $0.00 --------------------

    private static readonly (string, object?)[][] CachedConMeterEnCero =
    {
        new (string, object?)[]
        {
            // Mismo shape que "Azure Files Provisioned v2" (StorageFilesRetailFixture.md §1.6):
            // un producto real con su único meter en $0.00 en la MISMA consulta de región que
            // trae los snapshots — sin la guarda de precio positivo, este meter pasaría el resto
            // de filtros (producto/meter/unidad) si alguna vez coincidiera por nombre.
            ("service_name", "Storage"), ("product_name", "Standard HDD Managed Disks"),
            ("meter_name", "LRS Snapshots"), ("price_type", "Consumption"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.0),
        },
    };

    [Fact]
    public void MeterConRetailPriceCero_DevuelveNull_NuncaCeroSilencioso()
    {
        var cache = CacheWith(PriceRowFactory.Many(CachedConMeterEnCero));
        var repo = BuildRepo(cache);
        Assert.Null(repo.GetSnapshotPricePerGb("eastus", "Standard_LRS"));
    }
}
