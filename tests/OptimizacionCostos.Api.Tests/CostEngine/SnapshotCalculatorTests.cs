using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// SnapshotCalculator (service_key "snapshots", spec 2026-07-24): todos los snapshots se
/// costean a disk_size_gb × precio/GB (techo referencial; Azure factura por GB ocupado),
/// sin RI, con nota explícita.
/// </summary>
public sealed class SnapshotCalculatorTests
{
    private static SnapshotCalculator NewCalc(FakePriceRepository prices)
        => new(prices, new FakePricingConstants());

    [Fact]
    public void StandardLrs_CosteaPorTamanoDeDisco_SinRi()
    {
        var prices = new FakePriceRepository { GetSnapshotPricePerGbFn = (_, _) => 0.05 };
        var resources = Res.Rows(Res.Row(
            ("resource_id", 1), ("resource_name", "snap-1"), ("snapshot_sku", "Standard_LRS"),
            ("disk_size_gb", 128), ("incremental", false), ("location", "eastus")));

        var r = Assert.Single(NewCalc(prices).Calculate(resources, 99));

        Assert.Equal("snapshots", r.ServiceKey);
        Assert.Equal(128 * 0.05, r.PaygMonthly);
        Assert.Equal(r.PaygMonthly, r.StorageMonthly);
        Assert.NotNull(r.PaygHourly);
        Assert.Equal(r.PaygMonthly!.Value / 730.0, r.PaygHourly!.Value, 10);
        Assert.False(r.RiApplies);
        Assert.Equal("Los snapshots no tienen reserva", r.RiNotApplicableReason);
        Assert.Equal("calculated", r.CalculationStatus);
        Assert.Contains("referencial", r.CalculationNotes);
        Assert.Contains("128 GB", r.CalculationNotes);
    }

    [Fact]
    public void Incremental_LoDeclaraEnNotas()
    {
        var prices = new FakePriceRepository { GetSnapshotPricePerGbFn = (_, _) => 0.05 };
        var resources = Res.Rows(Res.Row(
            ("resource_id", 1), ("snapshot_sku", "Standard_LRS"),
            ("disk_size_gb", 64), ("incremental", true), ("location", "eastus")));

        var r = Assert.Single(NewCalc(prices).Calculate(resources, 99));
        Assert.Contains("incremental", r.CalculationNotes);
    }

    [Fact]
    public void PasaElSkuYLaRegionNormalizadaAlRepositorio()
    {
        (string Region, string? Sku)? seen = null;
        var prices = new FakePriceRepository
        {
            GetSnapshotPricePerGbFn = (region, sku) => { seen = (region, sku); return 0.132; },
        };
        var resources = Res.Rows(Res.Row(
            ("resource_id", 1), ("snapshot_sku", "Premium_LRS"),
            ("disk_size_gb", 256), ("incremental", false), ("location", "East US 2")));

        NewCalc(prices).Calculate(resources, 99);
        Assert.Equal(("eastus2", "Premium_LRS"), seen);
    }

    [Fact]
    public void SinPrecio_PriceNotFound_SinMontos()
    {
        var prices = new FakePriceRepository { GetSnapshotPricePerGbFn = (_, _) => null };
        var resources = Res.Rows(Res.Row(
            ("resource_id", 1), ("snapshot_sku", "Standard_ZRS"),
            ("disk_size_gb", 128), ("incremental", false), ("location", "eastus")));

        var r = Assert.Single(NewCalc(prices).Calculate(resources, 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
        Assert.Null(r.PaygMonthly);
    }

    [Fact]
    public void SinTamano_PriceNotFound()
    {
        var prices = new FakePriceRepository { GetSnapshotPricePerGbFn = (_, _) => 0.05 };
        var resources = Res.Rows(Res.Row(
            ("resource_id", 1), ("snapshot_sku", "Standard_LRS"),
            ("disk_size_gb", null), ("incremental", false), ("location", "eastus")));

        var r = Assert.Single(NewCalc(prices).Calculate(resources, 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
    }

    [Fact]
    public void ExcepcionDelRepositorio_PriceNotFound_NoRevienta()
    {
        var prices = new FakePriceRepository
        {
            GetSnapshotPricePerGbFn = (_, _) => throw new InvalidOperationException("boom"),
        };
        var resources = Res.Rows(Res.Row(
            ("resource_id", 1), ("snapshot_sku", "Standard_LRS"),
            ("disk_size_gb", 128), ("incremental", false), ("location", "eastus")));

        var r = Assert.Single(NewCalc(prices).Calculate(resources, 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
        Assert.Contains("InvalidOperationException", r.CalculationNotes);
    }

    // -------------------- Change 3: memoización por invocación --------------------

    [Fact]
    public void MismaRegionYSku_ConsultaElRepositorioUnaSolaVez()
    {
        // Un cliente real ya tiene 184 snapshots; miles son plausibles y el endpoint es
        // sincrónico. GetSnapshotPricePerGb hace un round-trip a SQL por llamada — con muchos
        // snapshots compartiendo (región, sku) esto se vuelve costoso sin memoizar.
        var calls = 0;
        var prices = new FakePriceRepository
        {
            GetSnapshotPricePerGbFn = (_, _) => { calls++; return 0.05; },
        };
        var resources = Res.Rows(
            Res.Row(("resource_id", 1), ("snapshot_sku", "Standard_LRS"),
                ("disk_size_gb", 128), ("incremental", false), ("location", "eastus")),
            Res.Row(("resource_id", 2), ("snapshot_sku", "Standard_LRS"),
                ("disk_size_gb", 64), ("incremental", false), ("location", "eastus")),
            Res.Row(("resource_id", 3), ("snapshot_sku", "Standard_LRS"),
                ("disk_size_gb", 256), ("incremental", true), ("location", "East US")));

        var results = NewCalc(prices).Calculate(resources, 99);

        Assert.Equal(1, calls);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal("calculated", r.CalculationStatus));
    }

    [Fact]
    public void RegionesODiscosDistintos_ConsultaElRepositorioUnaVezPorCombinacion()
    {
        var calls = new List<(string Region, string? Sku)>();
        var prices = new FakePriceRepository
        {
            GetSnapshotPricePerGbFn = (region, sku) => { calls.Add((region, sku)); return 0.05; },
        };
        var resources = Res.Rows(
            Res.Row(("resource_id", 1), ("snapshot_sku", "Standard_LRS"),
                ("disk_size_gb", 128), ("incremental", false), ("location", "eastus")),
            Res.Row(("resource_id", 2), ("snapshot_sku", "Premium_LRS"),
                ("disk_size_gb", 64), ("incremental", false), ("location", "eastus")),
            Res.Row(("resource_id", 3), ("snapshot_sku", "Standard_LRS"),
                ("disk_size_gb", 256), ("incremental", false), ("location", "westus")));

        NewCalc(prices).Calculate(resources, 99);

        Assert.Equal(3, calls.Count);
    }
}
