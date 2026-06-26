using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de paridad de <see cref="ComputeVmCalculator"/> (service_key "vms"),
/// port de la lógica de app/calculators/compute_vm.py con precios MOCKEADOS
/// (FakePriceRepository + FakePricingConstants). El repo Python no tenía tests
/// unitarios para esta calculadora; estos ejercitan cada rama documentada y los
/// montos esperados se escriben con las MISMAS fórmulas literales del .py.
///
/// Convención del .py: HoursPerMonth() == 730 (default del fake, igual que el patch
/// de los tests Python). La región se normaliza (lower + sin espacios) antes de
/// consultar el repositorio.
/// </summary>
public sealed class ComputeVmCalculatorTests
{
    private const double Hours = 730.0;

    private static ComputeVmCalculator Build(FakePriceRepository prices, FakePricingConstants? consts = null)
        => new(prices, consts ?? new FakePricingConstants());

    // ----------------------------------------------------------------------------
    // VM Linux running: PAYG = payg_hourly * 730; RI base prorrateada 12/36.
    // ----------------------------------------------------------------------------
    [Fact]
    public void Linux_running_computes_payg_and_prorated_ri()
    {
        var prices = new FakePriceRepository
        {
            // os_type "Linux" => un solo lookup; RI base 600/1500.
            GetVmPricesFn = (_, _, _) => new VmPrices(0.10, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 1),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "East US"),
            ("os_type", "Linux"),
            ("power_state", "PowerState/running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);          // 73.0
        Assert.Equal(0.10, result.PaygHourly!.Value, 5);
        Assert.Equal(600.0 / 12.0, result.Ri1yMonthly!.Value, 5);          // 50.0
        Assert.Equal(1500.0 / 36.0, result.Ri3yMonthly!.Value, 5);         // 41.6667
        Assert.True(result.RiApplies);
        Assert.Null(result.SqlAddonMonthly);
        Assert.Contains("sku=Standard_D4s_v5 region=eastus os=Linux match=deterministic", result.CalculationNotes);
    }

    // ----------------------------------------------------------------------------
    // Windows SIN AHB: PAYG usa precio Windows; premium = (win-lnx)*730 se SUMA al RI
    // (no se descuenta). RI base = Linux prorrateado.
    // ----------------------------------------------------------------------------
    [Fact]
    public void Windows_no_ahb_adds_premium_to_payg_and_to_ri()
    {
        var prices = new FakePriceRepository
        {
            // side_effect por OS: Windows 0.20 (RI nula en lookup win), Linux 0.10 con RI base.
            GetVmPricesFn = (_, _, os) => os == "Windows"
                ? new VmPrices(0.20, null, null)
                : new VmPrices(0.10, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 2),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("os_type", "Windows"),
            ("power_state", "VM running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        var premium = (0.20 - 0.10) * Hours;                               // 73.0
        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.20 * Hours, result.PaygMonthly!.Value, 5);          // 146.0
        Assert.Equal(600.0 / 12.0 + premium, result.Ri1yMonthly!.Value, 5);  // 50 + 73 = 123
        Assert.Equal(1500.0 / 36.0 + premium, result.Ri3yMonthly!.Value, 5); // 41.6667 + 73
        Assert.True(result.RiApplies);
        Assert.Contains("Windows premium:", result.CalculationNotes);
    }

    // ----------------------------------------------------------------------------
    // Windows CON AHB ('Windows_Server'): un solo lookup os_type; sin premium;
    // RI base puro Linux/OS-agnostic prorrateado.
    // ----------------------------------------------------------------------------
    [Fact]
    public void Windows_ahb_uses_single_lookup_no_premium()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => new VmPrices(0.20, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 3),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("os_type", "Windows"),
            ("os_license_benefit", "Windows_Server"),
            ("power_state", "running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.20 * Hours, result.PaygMonthly!.Value, 5);          // 146.0
        Assert.Equal(600.0 / 12.0, result.Ri1yMonthly!.Value, 5);          // 50.0 (sin premium)
        Assert.Equal(1500.0 / 36.0, result.Ri3yMonthly!.Value, 5);
        Assert.Contains("Windows AHB activo (sin premium)", result.CalculationNotes);
    }

    // ----------------------------------------------------------------------------
    // SQL VM add-on: se suma a PAYG y a RI (NO se descuenta con RI).
    // addon = sql_addon_per_vcore_hour * vcpus * 730. Standard PAYG = 0.10/vcore/hr.
    // ----------------------------------------------------------------------------
    [Fact]
    public void Sql_addon_added_to_payg_and_ri_not_discounted()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => new VmPrices(0.10, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 4),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("os_type", "Linux"),
            ("power_state", "running"),
            ("sql_image_sku", "Standard"),
            ("sql_license_type", "PAYG"),
            ("vcpu_count", 4)));

        var result = Build(prices).Calculate(rows, 99)[0];

        var addon = 0.10 * 4 * Hours;                                      // 292.0
        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.10 * Hours + addon, result.PaygMonthly!.Value, 5);  // 73 + 292 = 365
        Assert.Equal(0.10 + addon / Hours, result.PaygHourly!.Value, 5);   // 0.5
        Assert.Equal(addon, result.SqlAddonMonthly!.Value, 5);             // 292.0
        Assert.Equal(600.0 / 12.0 + addon, result.Ri1yMonthly!.Value, 5);  // 50 + 292 = 342
        Assert.Equal(1500.0 / 36.0 + addon, result.Ri3yMonthly!.Value, 5);
        Assert.Contains("SQL Standard PAYG:", result.CalculationNotes);
    }

    // SQL add-on con vCPU derivado del vm_size por regex _([A-Z]+)?(\d+) (grupo 2).
    // Standard_D4s_v5 => primer número = 4.
    [Fact]
    public void Sql_addon_uses_vcpu_derived_from_vm_size_when_vcpu_count_missing()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => new VmPrices(0.10, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 5),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("os_type", "Linux"),
            ("power_state", "running"),
            ("sql_image_sku", "Enterprise"),
            ("sql_license_type", "PAYG")));

        var result = Build(prices).Calculate(rows, 99)[0];

        var addon = 0.3753 * 4 * Hours;                                    // Enterprise 0.3753, vcpus=4
        Assert.Equal(addon, result.SqlAddonMonthly!.Value, 5);
        Assert.Equal(0.10 * Hours + addon, result.PaygMonthly!.Value, 5);
    }

    // ----------------------------------------------------------------------------
    // VM apagada: status not_running, compute = 0 (payg/ri = 0).
    // ----------------------------------------------------------------------------
    [Fact]
    public void Not_running_vm_has_zero_compute()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => new VmPrices(0.10, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 6),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("os_type", "Linux"),
            ("power_state", "PowerState/deallocated")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("not_running", result.CalculationStatus);
        Assert.Equal(0.0, result.PaygMonthly!.Value, 5);
        Assert.Equal(0.0, result.Ri1yMonthly!.Value, 5);
        Assert.Equal(0.0, result.Ri3yMonthly!.Value, 5);
        Assert.Equal("Power state: PowerState/deallocated", result.CalculationNotes);
    }

    // power_state ausente y status ausente => "" => no running => not_running.
    [Fact]
    public void Missing_power_state_defaults_to_not_running()
    {
        var prices = new FakePriceRepository();
        var rows = Res.Rows(Res.Row(
            ("resource_id", 7),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("os_type", "Linux")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("not_running", result.CalculationStatus);
        Assert.Equal(0.0, result.PaygMonthly!.Value, 5);
    }

    // status se usa como fallback de power_state.
    [Fact]
    public void Status_used_as_power_state_fallback()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => new VmPrices(0.10, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 8),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("os_type", "Linux"),
            ("status", "running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);
    }

    // ----------------------------------------------------------------------------
    // Falta vm_size o location (running) => price_not_found.
    // ----------------------------------------------------------------------------
    [Fact]
    public void Missing_vm_size_is_price_not_found()
    {
        var prices = new FakePriceRepository();
        var rows = Res.Rows(Res.Row(
            ("resource_id", 9),
            ("location", "eastus"),
            ("os_type", "Linux"),
            ("power_state", "running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("price_not_found", result.CalculationStatus);
        Assert.Equal("Missing vm_size or location", result.CalculationNotes);
        Assert.Null(result.PaygMonthly);
    }

    [Fact]
    public void Missing_location_is_price_not_found()
    {
        var prices = new FakePriceRepository();
        var rows = Res.Rows(Res.Row(
            ("resource_id", 10),
            ("vm_size", "Standard_D4s_v5"),
            ("os_type", "Linux"),
            ("power_state", "running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("price_not_found", result.CalculationStatus);
        Assert.Equal("Missing vm_size or location", result.CalculationNotes);
    }

    // ----------------------------------------------------------------------------
    // No hay precio PAYG y no hay fallback Linux => manual_required.
    // (Linux: lookup os_type devuelve PaygHourly null y no es rama de premium.)
    // ----------------------------------------------------------------------------
    [Fact]
    public void No_price_no_fallback_is_manual_required()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => new VmPrices(null, null, null),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 11),
            ("vm_size", "Standard_XYZ_v9"),
            ("location", "eastus"),
            ("os_type", "Linux"),
            ("power_state", "running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("manual_required", result.CalculationStatus);
        Assert.Null(result.PaygMonthly);
        Assert.Contains("Requiere validacion manual", result.CalculationNotes);
    }

    // ----------------------------------------------------------------------------
    // Windows sin AHB, sin precio Windows pero CON precio Linux => fallback Linux.
    // payg = lnx_hr * 730; premium = 0 (win_hr null). RI base Linux se mantiene.
    // ----------------------------------------------------------------------------
    [Fact]
    public void Windows_no_windows_price_falls_back_to_linux()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, os) => os == "Windows"
                ? new VmPrices(null, null, null)
                : new VmPrices(0.10, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 12),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("os_type", "Windows"),
            ("power_state", "running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);          // 73.0 (fallback Linux)
        // premium = 0 porque win_hr es null; RI = base / N + 0 + 0
        Assert.Equal(600.0 / 12.0, result.Ri1yMonthly!.Value, 5);          // 50.0
        Assert.Equal(1500.0 / 36.0, result.Ri3yMonthly!.Value, 5);
        Assert.Contains("Sin precio Windows; usando Linux como fallback", result.CalculationNotes);
    }

    // ----------------------------------------------------------------------------
    // RI no beneficiosa (RI >= PAYG) se descarta (DiscardNonSavingRi).
    // ----------------------------------------------------------------------------
    [Fact]
    public void Non_saving_ri_is_discarded()
    {
        var prices = new FakePriceRepository
        {
            // RI 1Y total enorme => ri_1y mensual >= payg => descartada.
            GetVmPricesFn = (_, _, _) => new VmPrices(0.10, 100000.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 13),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("os_type", "Linux"),
            ("power_state", "running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);          // 73.0
        Assert.Null(result.Ri1yMonthly);                                   // 100000/12 = 8333 >= 73 => descartada
        Assert.Equal(1500.0 / 36.0, result.Ri3yMonthly!.Value, 5);         // 41.6667 < 73 => se mantiene
        Assert.Contains("RI descartada por precio no beneficioso", result.CalculationNotes);
    }

    // ----------------------------------------------------------------------------
    // os_type ausente => default "Windows" (rama needs_windows_premium).
    // ----------------------------------------------------------------------------
    [Fact]
    public void Missing_os_type_defaults_to_windows_premium_path()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, os) => os == "Windows"
                ? new VmPrices(0.20, null, null)
                : new VmPrices(0.10, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 14),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus"),
            ("power_state", "running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.20 * Hours, result.PaygMonthly!.Value, 5);          // 146.0 (Windows por default)
        Assert.Contains("os=Windows", result.CalculationNotes);
    }

    // ----------------------------------------------------------------------------
    // size_name se usa como fallback de vm_size.
    // ----------------------------------------------------------------------------
    [Fact]
    public void Size_name_used_as_vm_size_fallback()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => new VmPrices(0.10, 600.0, 1500.0),
        };
        var rows = Res.Rows(Res.Row(
            ("resource_id", 15),
            ("size_name", "Standard_D8s_v5"),
            ("location", "eastus"),
            ("os_type", "Linux"),
            ("power_state", "running")));

        var result = Build(prices).Calculate(rows, 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);
        Assert.Contains("sku=Standard_D8s_v5", result.CalculationNotes);
    }
}
