using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de los tests de ManagedDiskCalculator desde
/// tests/test_paas_pricing.py (test_managed_disk_calculator_only_returns_orphans_and_stopped_vm_disks).
///
/// El precio de disco se mockea con FakePriceRepository (equivale a
/// patch("app.calculators.managed_disk.prices.get_disk_price", return_value=9.99)).
/// Los montos esperados son EXACTAMENTE los del test Python.
/// </summary>
public sealed class ManagedDiskCalculatorTests
{
    private static ManagedDiskCalculator NewCalc(FakePriceRepository prices)
        => new(prices, new FakePricingConstants());

    /// <summary>
    /// Port directo de test_managed_disk_calculator_only_returns_orphans_and_stopped_vm_disks:
    /// con get_disk_price → 9.99 fijo, solo se reportan el huérfano (id 1) y el disco de VM
    /// apagada (id 3); el disco de VM encendida (id 2) y el disco ASR (id 4) se descartan.
    /// </summary>
    [Fact]
    public void OnlyReturnsOrphansAndStoppedVmDisks()
    {
        var prices = new FakePriceRepository
        {
            // patch get_disk_price return_value=9.99
            GetDiskPriceFn = (_, _) => 9.99,
        };

        var resources = Res.Rows(
            // id 1: huérfano (sin attached_vm) → se reporta como orphan.
            Res.Row(
                ("resource_id", 1),
                ("resource_name", "orphan-disk"),
                ("disk_sku", "StandardSSD_LRS"),
                ("disk_size_gb", 128),
                ("location", "eastus2"),
                ("attached_vm_name", null),
                ("_stopped_vm_names_lower", new HashSet<string> { "vm-off" })),
            // id 2: adjunto a VM encendida (vm-on no está en el set de apagadas) → se descarta.
            Res.Row(
                ("resource_id", 2),
                ("resource_name", "running-disk"),
                ("disk_sku", "StandardSSD_LRS"),
                ("disk_size_gb", 128),
                ("location", "eastus2"),
                ("attached_vm_name", "vm-on"),
                ("_stopped_vm_names_lower", new HashSet<string> { "vm-off" })),
            // id 3: adjunto a VM apagada (vm-off en el set) → se reporta como attached_stopped.
            Res.Row(
                ("resource_id", 3),
                ("resource_name", "stopped-disk"),
                ("disk_sku", "StandardSSD_LRS"),
                ("disk_size_gb", 128),
                ("location", "eastus2"),
                ("attached_vm_name", "vm-off"),
                ("_stopped_vm_names_lower", new HashSet<string> { "vm-off" })),
            // id 4: disco ASR (resource_name contiene asrseeddisk) → se descarta siempre.
            Res.Row(
                ("resource_id", 4),
                ("resource_name", "asrseeddisk-replica"),
                ("disk_sku", "StandardSSD_LRS"),
                ("disk_size_gb", 128),
                ("location", "eastus2"),
                ("attached_vm_name", null),
                ("_stopped_vm_names_lower", new HashSet<string> { "vm-off" })));

        var results = NewCalc(prices).Calculate(resources, 99);

        // self.assertEqual([r.resource_id for r in results], [1, 3])
        Assert.Equal(new[] { 1, 3 }, results.Select(x => x.ResourceId).ToArray());
        // self.assertIn("category=orphan", results[0].calculation_notes)
        Assert.Contains("category=orphan", results[0].CalculationNotes);
        // self.assertIn("category=attached_stopped", results[1].calculation_notes)
        Assert.Contains("category=attached_stopped", results[1].CalculationNotes);
    }

    /// <summary>
    /// Verifica que con get_disk_price → 9.99 el disco huérfano queda con payg_monthly=9.99,
    /// sin RI (los discos no soportan Reserved Instances) y status "calculated".
    /// (Refuerza las garantías de la calculadora ejercitadas por el fixture Python.)
    /// </summary>
    [Fact]
    public void OrphanDiskUsesMockedMonthlyPriceAndNoRi()
    {
        var prices = new FakePriceRepository
        {
            GetDiskPriceFn = (_, _) => 9.99,
        };

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 1),
                ("resource_name", "orphan-disk"),
                ("disk_sku", "StandardSSD_LRS"),
                ("disk_size_gb", 128),
                ("location", "eastus2"),
                ("attached_vm_name", null),
                ("_stopped_vm_names_lower", new HashSet<string> { "vm-off" })));

        var result = NewCalc(prices).Calculate(resources, 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(9.99, result.PaygMonthly!.Value, 5);
        Assert.False(result.RiApplies);
        Assert.Null(result.Ri1yMonthly);
        Assert.Null(result.Ri3yMonthly);
        // tier derivado: StandardSSD_LRS + 128 GiB → E10 LRS (letra E, tier 10, sufijo LRS).
        Assert.Contains("tier=E10 LRS", result.CalculationNotes);
    }

    /// <summary>
    /// price_not_found cuando get_disk_price devuelve None/null (no hay match exacto):
    /// payg_monthly null, status "price_not_found", nota "No price for ...".
    /// El default de FakePriceRepository.GetDiskPriceFn ya devuelve null.
    /// </summary>
    [Fact]
    public void NoPriceMatchMarksPriceNotFound()
    {
        var prices = new FakePriceRepository(); // GetDiskPriceFn default → null

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 1),
                ("resource_name", "orphan-disk"),
                ("disk_sku", "StandardSSD_LRS"),
                ("disk_size_gb", 128),
                ("location", "eastus2"),
                ("attached_vm_name", null),
                ("_stopped_vm_names_lower", new HashSet<string> { "vm-off" })));

        var result = NewCalc(prices).Calculate(resources, 99)[0];

        Assert.Equal("price_not_found", result.CalculationStatus);
        Assert.Null(result.PaygMonthly);
        Assert.Contains("No price for tier=E10 LRS region=eastus2", result.CalculationNotes);
    }

    /// <summary>
    /// Tier no derivable (Ultra disk → DeriveDiskTier devuelve null) ⇒ price_not_found
    /// con nota "Cannot derive tier ...", sin siquiera consultar precios.
    /// </summary>
    [Fact]
    public void UndeterminableTierMarksPriceNotFound()
    {
        var prices = new FakePriceRepository
        {
            // Si por error se llamara, devolvería precio; el test prueba que NO se usa.
            GetDiskPriceFn = (_, _) => 9.99,
        };

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 1),
                ("resource_name", "orphan-ultra"),
                ("disk_sku", "UltraSSD_LRS"),
                ("disk_size_gb", 1024),
                ("location", "eastus2"),
                ("attached_vm_name", null)));

        var result = NewCalc(prices).Calculate(resources, 99)[0];

        Assert.Equal("price_not_found", result.CalculationStatus);
        Assert.Null(result.PaygMonthly);
        Assert.Contains("Cannot derive tier for sku=UltraSSD_LRS size=1024", result.CalculationNotes);
    }
}
