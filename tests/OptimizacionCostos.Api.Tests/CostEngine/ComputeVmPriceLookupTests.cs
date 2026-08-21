using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Regresión del doble lookup de precios en <see cref="ComputeVmCalculator"/> (2026-08-21).
///
/// La rama sin premium de Windows llamaba dos veces a <c>GetVmPrices</c> con argumentos idénticos.
/// Cuando la selección determinista no encuentra PAYG, ese método cae al asistente de IA, que no
/// memoiza, así que eran dos elecciones independientes de meter: el PAYG y el payg_meter_id salían de
/// la segunda y la base del RI y el <c>match=</c> de la primera. Si la segunda no resolvía, la VM caía
/// a manual_required con $0 aunque la primera ya tenía precio. Y la segunda estaba fuera del try, así
/// que su excepción tumbaba el cálculo entero en vez de degradar esa fila a price_not_found.
///
/// Las pruebas usan un doble que devuelve algo DISTINTO en cada llamada, que es la única forma de
/// distinguir "una llamada" de "dos llamadas que casualmente coinciden".
/// </summary>
public sealed class ComputeVmPriceLookupTests
{
    private const double Hours = 730.0;

    private static IReadOnlyList<ResourceRow> Vm(string osType, string? licenseBenefit = null)
        => Res.Rows(Res.Row(
            ("resource_id", 1),
            ("vm_size", "Standard_D4s_v5"),
            ("location", "eastus2"),
            ("os_type", osType),
            ("power_state", "VM running"),
            ("os_license_benefit", licenseBenefit)));

    // ------------------------------------------------------------------------------------
    // Linux nativo y Windows con AHB: un solo lookup, con el OS de tarificación.
    // ------------------------------------------------------------------------------------
    [Theory]
    [InlineData("Linux", null, "Linux")]
    [InlineData("Windows", "Windows_Server", "Linux")]   // AHB: se cotiza a tarifa Linux
    [InlineData("Windows", "Windows_Client", "Linux")]   // Multitenant Hosting Rights: idem
    public void Rama_sin_premium_consulta_una_sola_vez(string osType, string? benefit, string osEsperado)
    {
        var calls = new List<string>();
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, os) =>
            {
                calls.Add(os);
                return new VmPrices(0.10, 600.0, 1500.0, PaygMeterId: "meter-1");
            },
        };

        var result = new ComputeVmCalculator(prices, new FakePricingConstants())
            .Calculate(Vm(osType, benefit), 99)[0];

        Assert.Equal(new[] { osEsperado }, calls);
        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);
    }

    // ------------------------------------------------------------------------------------
    // El defecto que costaba plata: la segunda consulta sin precio mandaba la VM a
    // manual_required con $0 aunque la primera ya lo había resuelto.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Un_segundo_lookup_sin_precio_ya_no_puede_anular_a_la_VM()
    {
        var n = 0;
        var prices = new FakePriceRepository
        {
            // Primera llamada resuelve; cualquier llamada posterior devuelve vacío (lo que hacía el
            // asistente de IA cuando declinaba en el segundo intento).
            GetVmPricesFn = (_, _, _) => ++n == 1
                ? new VmPrices(0.10, 600.0, 1500.0, PaygMeterId: "meter-bueno")
                : new VmPrices(null, null, null),
        };

        var result = new ComputeVmCalculator(prices, new FakePricingConstants())
            .Calculate(Vm("Linux"), 99)[0];

        Assert.Equal(1, n);
        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);
        Assert.Equal("meter-bueno", result.PaygMeterId);
    }

    // ------------------------------------------------------------------------------------
    // La otra mitad del defecto: PAYG y base del RI salían de lookups distintos.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void El_payg_y_la_base_del_ri_salen_del_mismo_lookup()
    {
        var n = 0;
        var prices = new FakePriceRepository
        {
            // Dos meters con precios muy distintos: si el cálculo mezclara llamadas, el PAYG saldría
            // del segundo (0.99) y el RI del primero (600/1500), y las aserciones de abajo fallarían.
            GetVmPricesFn = (_, _, _) => ++n == 1
                ? new VmPrices(0.10, 600.0, 1500.0, PaygMeterId: "meter-1")
                : new VmPrices(0.99, 9999.0, 99999.0, PaygMeterId: "meter-2"),
        };

        var result = new ComputeVmCalculator(prices, new FakePricingConstants())
            .Calculate(Vm("Linux"), 99)[0];

        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);
        Assert.Equal(600.0 / 12.0, result.Ri1yMonthly!.Value, 5);
        Assert.Equal(1500.0 / 36.0, result.Ri3yMonthly!.Value, 5);
        Assert.Equal("meter-1", result.PaygMeterId);
    }

    // ------------------------------------------------------------------------------------
    // Una excepción en el lookup degrada SOLO esa fila, y no aborta el lote.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Una_excepcion_en_el_lookup_degrada_la_fila_y_no_tumba_el_lote()
    {
        var n = 0;
        var prices = new FakePriceRepository
        {
            // Antes: la primera llamada (dentro del try) pasaba y la segunda (fuera) explotaba,
            // así que la excepción escapaba de Calculate y se perdía el lote completo.
            GetVmPricesFn = (_, _, _) => ++n == 1
                ? new VmPrices(0.10, 600.0, 1500.0)
                : throw new InvalidOperationException("segundo lookup"),
        };

        var rows = Res.Rows(
            Res.Row(("resource_id", 1), ("vm_size", "Standard_D4s_v5"), ("location", "eastus2"),
                    ("os_type", "Linux"), ("power_state", "VM running")),
            Res.Row(("resource_id", 2), ("vm_size", "Standard_D8s_v5"), ("location", "eastus2"),
                    ("os_type", "Linux"), ("power_state", "VM running")));

        var results = new ComputeVmCalculator(prices, new FakePricingConstants()).Calculate(rows, 99);

        Assert.Equal(2, results.Count);                       // el lote sobrevive
        Assert.Equal("calculated", results[0].CalculationStatus);
        Assert.Equal("price_not_found", results[1].CalculationStatus);
        Assert.Contains("InvalidOperationException", results[1].CalculationNotes);
    }

    [Fact]
    public void Si_el_primer_lookup_explota_la_fila_cae_a_price_not_found()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => throw new TimeoutException(),
        };

        var result = new ComputeVmCalculator(prices, new FakePricingConstants())
            .Calculate(Vm("Linux"), 99)[0];

        Assert.Equal("price_not_found", result.CalculationStatus);
        Assert.Contains("TimeoutException", result.CalculationNotes);
    }

    // ------------------------------------------------------------------------------------
    // La rama con premium sigue necesitando DOS lookups (Windows y Linux) y no se tocó.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Rama_con_premium_sigue_pidiendo_windows_y_linux()
    {
        var calls = new List<string>();
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, os) =>
            {
                calls.Add(os);
                return os == "Windows"
                    ? new VmPrices(0.20, null, null, PaygMeterId: "meter-win")
                    : new VmPrices(0.10, 600.0, 1500.0, PaygMeterId: "meter-lnx");
            },
        };

        var result = new ComputeVmCalculator(prices, new FakePricingConstants())
            .Calculate(Vm("Windows"), 99)[0];

        Assert.Equal(new[] { "Windows", "Linux" }, calls);
        Assert.Equal(0.20 * Hours, result.PaygMonthly!.Value, 5);          // PAYG del meter Windows
        Assert.Equal("meter-win", result.PaygMeterId);
        var premium = (0.20 - 0.10) * Hours;
        Assert.Equal(600.0 / 12.0 + premium, result.Ri1yMonthly!.Value, 5); // RI base del meter Linux
    }

    [Fact]
    public void Rama_con_premium_sin_precio_windows_cae_a_linux()
    {
        var calls = new List<string>();
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, os) =>
            {
                calls.Add(os);
                return os == "Windows"
                    ? new VmPrices(null, null, null)
                    : new VmPrices(0.10, 600.0, 1500.0, PaygMeterId: "meter-lnx");
            },
        };

        var result = new ComputeVmCalculator(prices, new FakePricingConstants())
            .Calculate(Vm("Windows"), 99)[0];

        Assert.Equal(new[] { "Windows", "Linux" }, calls);
        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);
        Assert.Equal("meter-lnx", result.PaygMeterId);
        Assert.Contains("Sin precio Windows", result.CalculationNotes);
    }
}
