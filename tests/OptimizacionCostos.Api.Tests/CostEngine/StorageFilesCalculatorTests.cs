using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// StorageFilesCalculator (service_key "storage_files", spec 2026-07-24). El corte de
/// 10 TiB se aplica en la IMPORTACIÓN (StorageFilesEnricher); aquí se costea lo insertado:
/// Σ por tier de GiB × precio(tier, redundancia, región), con RI 1y/3y solo si TODOS los
/// tiers con capacidad tienen reserva, y provisioned v2 → manual_required.
/// </summary>
public sealed class StorageFilesCalculatorTests
{
    private static StorageFilesCalculator NewCalc(FakePriceRepository prices)
        => new(prices, new FakePricingConstants());

    private static ResourceRow Account(
        string sku = "Standard_LRS", string kind = "StorageV2", string? tiersJson = null,
        double billable = 15000.0, int id = 1)
        => Res.Row(
            ("resource_id", id), ("resource_name", "stgcliente01"), ("files_sku", sku),
            ("kind", kind), ("share_count", 3), ("used_gib", 14000.0),
            ("provisioned_gib", 20480.0), ("billable_gib", billable),
            ("tier_breakdown_json", tiersJson ?? """{"hot":15000.0}"""),
            ("location", "eastus"));

    [Fact]
    public void HotLrs_CosteaGibPorPrecio_ConRi()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, red) => tier == "hot" && red == "LRS"
                ? new StorageFilesPrices(0.0255, 0.02133, 0.01706, "meter-hot-lrs")
                : new StorageFilesPrices(null, null, null, null),
        };

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(Account()), 99));

        Assert.Equal("storage_files", r.ServiceKey);
        Assert.Equal(15000.0 * 0.0255, r.PaygMonthly!.Value, 6);
        Assert.Equal(r.PaygMonthly, r.StorageMonthly);
        Assert.Equal(15000.0 * 0.02133, r.Ri1yMonthly!.Value, 6);
        Assert.Equal(15000.0 * 0.01706, r.Ri3yMonthly!.Value, 6);
        Assert.True(r.RiApplies);
        Assert.NotNull(r.Savings1yMonthly);
        Assert.Equal("meter-hot-lrs", r.PaygMeterId);
        Assert.Equal("calculated", r.CalculationStatus);
        Assert.Contains("No incluye transacciones", r.CalculationNotes);
        Assert.Contains("bloques de 10/100 TiB", r.CalculationNotes);
    }

    [Fact]
    public void MezclaDeTiers_SumaCadaUnoConSuPrecio()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(0.0255, null, null, "m-hot"),
                "cool" => new StorageFilesPrices(0.015, null, null, "m-cool"),
                "transaction_optimized" => new StorageFilesPrices(0.06, null, null, "m-txopt"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(tiersJson: """{"hot":8000.0,"cool":4000.0,"transaction_optimized":3000.0}""");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Equal(8000 * 0.0255 + 4000 * 0.015 + 3000 * 0.06, r.PaygMonthly!.Value, 6);
        // Ningún tier tiene RI publicada → sin RI, con razón.
        Assert.False(r.RiApplies);
        Assert.Null(r.Ri1yMonthly);
    }

    [Fact]
    public void RiParcial_NoSeEmite_ElAhorroNoSeInventa()
    {
        // hot tiene RI 1y; cool no → Ri1yMonthly debe quedar null (suma parcial inventaría ahorro).
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(0.0255, 0.0213, null, "m-hot"),
                "cool" => new StorageFilesPrices(0.015, null, null, "m-cool"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(tiersJson: """{"hot":8000.0,"cool":4000.0}""");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.NotNull(r.PaygMonthly);
        Assert.Null(r.Ri1yMonthly);
        Assert.Null(r.Ri3yMonthly);
        Assert.False(r.RiApplies);
    }

    [Fact]
    public void Premium_UsaTierPremiumYRedundanciaDelSku()
    {
        var seen = new List<(string Tier, string Red)>();
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, red) =>
            {
                seen.Add((tier, red));
                return new StorageFilesPrices(0.16, null, null, "m-prem");
            },
        };
        var row = Account(sku: "Premium_ZRS", kind: "FileStorage",
            tiersJson: """{"premium":12000.0}""", billable: 12000.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal(12000.0 * 0.16, r.PaygMonthly!.Value, 6);
        Assert.Equal(("premium", "ZRS"), Assert.Single(seen));
    }

    [Fact]
    public void RedundanciaRaGrs_FacturaComoGrs()
    {
        // Fixture §5: RA-GRS/RA-GZRS no tienen meter propio en Azure Files, se facturan
        // bajo el meter GRS/GZRS — el calculador debe mapear antes de consultar precios.
        string? seenRed = null;
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, red) => { seenRed = red; return new StorageFilesPrices(0.04, null, null, null); },
        };
        NewCalc(prices).Calculate(Res.Rows(Account(sku: "Standard_RAGRS")), 99);
        Assert.Equal("GRS", seenRed);
    }

    [Fact]
    public void ProvisionedV2_ManualRequired_SinNumeros()
    {
        var prices = new FakePriceRepository();
        var row = Account(sku: "StandardV2_LRS");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal("manual_required", r.CalculationStatus);
        Assert.Null(r.PaygMonthly);
        Assert.Contains("provisioned v2", r.CalculationNotes);
    }

    [Fact]
    public void TierSinPrecio_PriceNotFound_SinSumaParcial()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier == "hot"
                ? new StorageFilesPrices(0.0255, null, null, "m-hot")
                : new StorageFilesPrices(null, null, null, null),
        };
        var row = Account(tiersJson: """{"hot":8000.0,"cool":4000.0}""");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
        Assert.Null(r.PaygMonthly);
        Assert.Contains("cool", r.CalculationNotes);
    }

    [Fact]
    public void SinDesglose_PriceNotFound()
    {
        var prices = new FakePriceRepository();
        var row = Account(tiersJson: """{}""");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
    }

    [Fact]
    public void RiMayorQuePayg_SeDescarta()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, _) => new StorageFilesPrices(0.0255, 0.99, 0.99, "m"),
        };

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(Account()), 99));
        Assert.Null(r.Ri1yMonthly);
        Assert.False(r.RiApplies);
        Assert.Contains("RI descartada", r.CalculationNotes);
    }

    [Fact]
    public void DesgloseConTiersEnCero_PriceNotFound_NuncaCeroSilencioso()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, _) => new StorageFilesPrices(0.0255, 0.0213, 0.017, "m"),
        };
        var row = Account(tiersJson: """{"hot":0.0,"cool":0.0}""");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
        Assert.Null(r.PaygMonthly);
        Assert.Null(r.Ri1yMonthly);
        Assert.False(r.RiApplies);
    }
}
