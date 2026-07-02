using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de los tests de SELECCIÓN de precios VM de
/// tests/test_paas_pricing.py (los que ejercitan prices._select_vm_prices con precios
/// cacheados mockeados).
///
/// Tests Python portados:
///   - test_vm_windows_uses_windows_payg_and_base_reservations
///   - test_vm_selection_excludes_cloudservices_product_without_space
///
/// Montos esperados copiados EXACTAMENTE del Python:
///   windows_payg = 0.45, linux_payg = 0.20, base_ri_1y = 1200.0, base_ri_3y = 2700.0
///   (segundo test) windows_payg = 0.892, linux_payg = 0.524, cloudservices_payg = 0.892
///
/// El Python prueba el selector privado _select_vm_prices directamente. En C# ese selector es
/// privado; se ejercita a través de GetVmPrices con la cache ya "fresca" (IsCacheFreshFor=true,
/// IsFetchQueryFresh=true) y QueryCached devolviendo el array canned → no se dispara ningún fetch,
/// igual que llamar al selector con la lista dada. GetVmPrices añade match_strategy="deterministic"
/// cuando hay PAYG; eso no altera los precios seleccionados que verifican los tests.
/// </summary>
public sealed class PriceSelection_VmTests
{
    private static SqlPriceRepository BuildRepo(FakePriceCache cache, FakeRetailPriceClient client)
        => new SqlPriceRepository(cache, client, new FakePricingConstants());

    [Fact]
    public void Vm_Windows_UsesWindowsPaygAndBaseReservations()
    {
        const double windowsPayg = 0.45;
        const double linuxPayg = 0.20;
        const double baseRi1y = 1200.0;
        const double baseRi3y = 2700.0;
        const double ignoredSpot = 0.01;

        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Virtual Machines D Series Windows"),
                ("meter_name", "D2as v5"),
                ("meter_id", "meter-win-payg"),
                ("price_type", "Consumption"),
                ("retail_price", windowsPayg),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Virtual Machines D Series"),
                ("meter_name", "D2as v5"),
                ("price_type", "Consumption"),
                ("retail_price", linuxPayg),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Virtual Machines D Series"),
                ("meter_name", "D2as v5"),
                ("price_type", "Reservation"),
                ("reservation_term", "1 Year"),
                ("retail_price", baseRi1y),
            },
            new (string, object?)[]
            {
                ("product_name", "Virtual Machines D Series"),
                ("meter_name", "D2as v5"),
                ("price_type", "Reservation"),
                ("reservation_term", "3 Years"),
                ("retail_price", baseRi3y),
            },
            new (string, object?)[]
            {
                ("product_name", "Virtual Machines D Series Windows"),
                ("meter_name", "D2as v5 Spot"),
                ("sku_name", "Spot"),
                ("price_type", "Consumption"),
                ("retail_price", ignoredSpot),
                ("unit_of_measure", "1 Hour"),
            },
            new (string, object?)[]
            {
                ("product_name", "Cloud Services"),
                ("meter_name", "D2as v5"),
                ("price_type", "Consumption"),
                ("retail_price", ignoredSpot),
                ("unit_of_measure", "1 Hour"),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };
        var client = new FakeRetailPriceClient();
        var repo = BuildRepo(cache, client);

        var selected = repo.GetVmPrices("Standard_D2as_v5", "eastus", "Windows");

        Assert.Equal(windowsPayg, selected.PaygHourly);
        Assert.Equal(baseRi1y, selected.Ri1yTotal);
        Assert.Equal(baseRi3y, selected.Ri3yTotal);
        Assert.Equal("Windows", selected.OsUsed);
        Assert.Equal("meter-win-payg", selected.PaygMeterId);
    }

    [Fact]
    public void Vm_Selection_ExcludesCloudServicesProductWithoutSpace()
    {
        // Regresión MINSUR: "Eadsv5 Series CloudServices" (sin espacio) contaminaba la
        // selección Linux → premium Windows en 0 y PAYG Linux inflado.
        const double windowsPayg = 0.892;
        const double linuxPayg = 0.524;
        const double cloudservicesPayg = 0.892;

        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Eadsv5 Series CloudServices"),
                ("meter_name", "E8ads v5"),
                ("price_type", "Consumption"),
                ("retail_price", cloudservicesPayg),
                ("unit_of_measure", "1 Hour"),
            },
            new (string, object?)[]
            {
                ("product_name", "Virtual Machines Eadsv5 Series"),
                ("meter_name", "E8ads v5"),
                ("price_type", "Consumption"),
                ("retail_price", linuxPayg),
                ("unit_of_measure", "1 Hour"),
            },
            new (string, object?)[]
            {
                ("product_name", "Virtual Machines Eadsv5 Series Windows"),
                ("meter_name", "E8ads v5"),
                ("price_type", "Consumption"),
                ("retail_price", windowsPayg),
                ("unit_of_measure", "1 Hour"),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };
        var client = new FakeRetailPriceClient();
        var repo = BuildRepo(cache, client);

        var linuxSelected = repo.GetVmPrices("Standard_E8ads_v5", "eastus", "Linux");
        var windowsSelected = repo.GetVmPrices("Standard_E8ads_v5", "eastus", "Windows");

        Assert.Equal(linuxPayg, linuxSelected.PaygHourly);
        Assert.Equal(windowsPayg, windowsSelected.PaygHourly);
    }
}
