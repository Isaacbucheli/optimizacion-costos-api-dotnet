using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de los tests de SELECCIÓN del servicio "synapse_dw" de
/// optimizacion-costos-api-github/tests/test_paas_pricing.py (clase SynapseDwPricingTests):
///   - test_exact_level_match_with_ri_per_100dwu_blocks
///   - test_missing_level_falls_back_to_generic_dwu_meter
///
/// Los montos esperados son EXACTAMENTE los del Python (copiados, no recalculados).
/// Peculiaridades replicadas 1:1:
///   - DW###c → bloques = DWU/100 (mín 1).
///   - PAYG: fila Consumption con sku_name == nivel exacto.
///   - Si no hay nivel exacto y hay DWU, fallback al meter genérico
///     "SQL Provisioned DWU" (por 100 DWU/hora) × bloques.
///   - Reservas (DW100c) se escalan por bloques.
/// Los tests del CALCULADOR (que parchean get_synapse_dw_prices) NO se portan aquí:
/// son tests de la calculadora, fuera del alcance de la selección de precios.
/// </summary>
public sealed class PriceSelection_SynapseTests
{
    // Mismo array CACHED del Python (SynapseDwPricingTests.CACHED).
    private static IReadOnlyList<PriceRow> Cached() => PriceRowFactory.Many(
        new (string, object?)[]
        {
            ("product_name", "Azure Synapse Analytics Dedicated SQL Pool"),
            ("sku_name", "DW100c"),
            ("meter_name", "100 DWUs"),
            ("price_type", "Consumption"),
            ("retail_price", 1.20),
            ("unit_of_measure", "1/Hour"),
        },
        new (string, object?)[]
        {
            ("product_name", "Azure Synapse Analytics Dedicated SQL Pool"),
            ("sku_name", "DW500c"),
            ("meter_name", "100 DWUs"),
            ("price_type", "Consumption"),
            ("retail_price", 6.00),
            ("unit_of_measure", "1/Hour"),
        },
        new (string, object?)[]
        {
            ("product_name", "Azure Synapse Analytics SQL Provisioned DWU"),
            ("sku_name", "DWU"),
            ("meter_name", "100 DWU"),
            ("price_type", "Consumption"),
            ("retail_price", 1.20),
            ("unit_of_measure", "1/Hour"),
        },
        new (string, object?)[]
        {
            ("product_name", "Azure Synapse Analytics Dedicated SQL Pool"),
            ("sku_name", "DW100c"),
            ("meter_name", "100 DWUs"),
            ("price_type", "Reservation"),
            ("reservation_term", "1 Year"),
            ("retail_price", 6623.0),
        },
        new (string, object?)[]
        {
            ("product_name", "Azure Synapse Analytics Dedicated SQL Pool"),
            ("sku_name", "DW100c"),
            ("meter_name", "100 DWUs"),
            ("price_type", "Reservation"),
            ("reservation_term", "3 Years"),
            ("retail_price", 11038.0),
        });

    /// <summary>
    /// Python: test_exact_level_match_with_ri_per_100dwu_blocks. DW500c → payg 6.00,
    /// blocks 5, ri_1y 6623*5, ri_3y 11038*5.
    /// </summary>
    [Fact]
    public void Exact_Level_Match_With_Ri_Per_100dwu_Blocks()
    {
        var selected = SqlPriceRepository.SelectSynapseDwPrices(Cached(), "DW500c");

        Assert.Equal(6.00, selected.PaygHourly);
        Assert.Equal(5, selected.Blocks);
        Assert.Equal(6623.0 * 5, selected.Ri1yTotal);
        Assert.Equal(11038.0 * 5, selected.Ri3yTotal);
    }

    /// <summary>
    /// Python: test_missing_level_falls_back_to_generic_dwu_meter. DW900c → no hay sku
    /// exacto, fallback al meter genérico "SQL Provisioned DWU" 1.20 × 9 bloques, y la
    /// reserva 1y 6623 × 9 bloques.
    /// </summary>
    [Fact]
    public void Missing_Level_Falls_Back_To_Generic_Dwu_Meter()
    {
        var selected = SqlPriceRepository.SelectSynapseDwPrices(Cached(), "DW900c");

        Assert.Equal(1.20 * 9, selected.PaygHourly);
        Assert.Equal(6623.0 * 9, selected.Ri1yTotal);
    }

    /// <summary>
    /// Refresh por fetch_query rancio: GetSynapseDwPrices refresca vía
    /// FetchSynapseDwPrices(region), borra/inserta por "synapse_dw {region}" y consulta la
    /// cache por el servicio "Azure Synapse Analytics". (No tiene equivalente directo en el
    /// Python de SynapseDwPricingTests pero documenta el patrón de get_synapse_dw_prices.)
    /// </summary>
    [Fact]
    public void Synapse_Refresh_Is_Scoped_To_Fetch_Query()
    {
        var rows = Cached();
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => false,
            QueryCachedFn = (_, _, _, _) => rows,
        };
        var client = new FakeRetailPriceClient
        {
            FetchSynapseDwPricesFn = _ => rows,
        };
        var repo = new SqlPriceRepository(cache, client, new FakePricingConstants());

        var selected = repo.GetSynapseDwPrices("DW500c", "eastus2");

        // Selección determinista igual que el test de selección pura.
        Assert.Equal(6.00, selected.PaygHourly);
        Assert.Equal(5, selected.Blocks);

        // is_fresh.assert_called_once_with("synapse_dw eastus2")
        Assert.Equal("synapse_dw eastus2", Assert.Single(cache.IsFetchQueryFreshCalls));

        // fetch_synapse.assert_called_once_with("eastus2")
        Assert.Equal("eastus2", Assert.Single(client.FetchSynapseDwPricesCalls));

        // delete_by_fetch_query.assert_called_once_with("synapse_dw eastus2")
        Assert.Equal("synapse_dw eastus2", Assert.Single(cache.DeleteByFetchQueryCalls));

        // insert_batch.assert_called_once_with(rows, "synapse_dw eastus2")
        var insert = Assert.Single(cache.InsertCalls);
        Assert.Equal("synapse_dw eastus2", insert.FetchQuery);
        Assert.Equal(rows, insert.Rows);

        // query_cached.assert_called_once_with("Azure Synapse Analytics", "eastus2")
        var query = Assert.Single(cache.QueryCachedCalls);
        Assert.Equal("Azure Synapse Analytics", query.ServiceName);
        Assert.Equal("eastus2", query.Region);
        Assert.Null(query.ArmSkuName);
        Assert.Null(query.SkuName);
    }
}
