using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de SqlDatabaseCalculator. Port 1:1 de los tests de tests/test_paas_pricing.py
/// que ejercitan SqlDatabaseCalculator con precios mockeados (FakePriceRepository):
///   - test_sql_calculator_skips_datawarehouse_and_synapse_skips_others
///   - test_sql_hyperscale_serverless_calculator_multiplies_by_vcores
///   - test_sql_serverless_adds_max_estimate_note
///   - test_sql_db_in_elastic_pool_costs_zero
///
/// Los montos esperados se copian EXACTAMENTE de los asserts Python (no se recalculan).
/// HOURS_PER_MONTH = 730 ya es el default de FakePricingConstants.
/// </summary>
public sealed class SqlDatabaseCalculatorTests
{
    private const double HoursPerMonth = 730.0;

    private static SqlDatabaseCalculator MakeCalculator(FakePriceRepository prices)
        => new(prices, new FakePricingConstants());

    /// <summary>
    /// Port de test_sql_calculator_skips_datawarehouse_and_synapse_skips_others:
    /// la calculadora SQL salta filas DataWarehouse (no agrega resultado) y solo
    /// procesa la fila regular. get_sql_db_price_details devuelve None => la fila
    /// regular sale como resultado (price_not_found), pero igual existe.
    /// El test Python solo asevera los resource_id devueltos: [2].
    /// </summary>
    [Fact]
    public void Skips_DataWarehouse_And_Keeps_Regular()
    {
        var dw = Res.Row(
            ("resource_id", 1),
            ("sku_name", "DataWarehouse"),
            ("sku_tier", "DataWarehouse"),
            ("location", "East US 2"),
            ("properties_json", "{\"status\": \"Paused\", \"currentServiceObjectiveName\": \"DW100c\"}"));
        var regular = Res.Row(
            ("resource_id", 2),
            ("sku_name", "GP_S_Gen5"),
            ("sku_tier", "GeneralPurpose"),
            ("location", "East US 2"));

        var prices = new FakePriceRepository
        {
            // patch get_sql_db_price_details return_value=None
            GetSqlDbPriceDetailsFn = (_, _, _, _) => null,
        };

        var sqlResults = MakeCalculator(prices).Calculate(Res.Rows(dw, regular), 99);

        Assert.Equal(new[] { 2 }, sqlResults.Select(r => r.ResourceId).ToArray());
    }

    /// <summary>
    /// Port de test_sql_hyperscale_serverless_calculator_multiplies_by_vcores:
    /// Hyperscale Serverless con sku_capacity=4 => payg_monthly = price * 730 * 4.
    /// </summary>
    [Fact]
    public void Hyperscale_Serverless_Multiplies_By_Vcores()
    {
        var resource = Res.Row(
            ("resource_id", 5),
            ("sku_name", "HS_S_Gen5"),
            ("sku_tier", "Hyperscale"),
            ("sku_capacity", 4),
            ("compute_tier", "Serverless"),
            ("location", "eastus2"));

        // Captura de argumentos para verificar la llamada (assert_called_once_with).
        string? gotSku = null, gotRegion = null, gotTier = null, gotCompute = null;
        var callCount = 0;

        var prices = new FakePriceRepository
        {
            GetSqlDbPriceDetailsFn = (sku, region, tier, compute) =>
            {
                callCount++;
                gotSku = sku; gotRegion = region; gotTier = tier; gotCompute = compute;
                return new SqlDbPriceDetails(
                    RetailPrice: 0.378,
                    UnitOfMeasure: "1 Hour",
                    MeterName: "vCore",
                    ProductName: "SQL Database Hyperscale - Serverless - Compute Gen5",
                    SkuName: "1 vCore");
            },
        };

        var result = MakeCalculator(prices).Calculate(Res.Rows(resource), 99)[0];

        // get_price.assert_called_once_with("HS_S_Gen5", "eastus2", "Hyperscale", "Serverless")
        Assert.Equal(1, callCount);
        Assert.Equal("HS_S_Gen5", gotSku);
        Assert.Equal("eastus2", gotRegion);
        Assert.Equal("Hyperscale", gotTier);
        Assert.Equal("Serverless", gotCompute);

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.378 * 730.0 * 4, result.PaygMonthly!.Value, 5);
        Assert.False(result.RiApplies);
        Assert.Contains("capacity=4", result.CalculationNotes);
        Assert.Contains("tope máximo", result.CalculationNotes);
    }

    /// <summary>
    /// Port de test_sql_serverless_adds_max_estimate_note:
    /// GP Serverless con sku_capacity=2 => payg_monthly = 0.50 * 730 * 2; añade la
    /// nota de "Serverless se factura por uso real" / "costo real será menor".
    /// </summary>
    [Fact]
    public void Serverless_Adds_Max_Estimate_Note()
    {
        var resource = Res.Row(
            ("resource_id", 7),
            ("sku_name", "GP_S_Gen5"),
            ("sku_tier", "GeneralPurpose"),
            ("sku_capacity", 2),
            ("compute_tier", "Serverless"),
            ("location", "eastus2"));

        var prices = new FakePriceRepository
        {
            GetSqlDbPriceDetailsFn = (_, _, _, _) => new SqlDbPriceDetails(
                RetailPrice: 0.50,
                UnitOfMeasure: "1 Hour",
                MeterName: "vCore",
                ProductName: "SQL Database General Purpose - Serverless - Compute Gen5",
                SkuName: "1 vCore"),
        };

        var result = MakeCalculator(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.50 * 730.0 * 2, result.PaygMonthly!.Value, 5);
        Assert.False(result.RiApplies);
        Assert.Contains("Serverless se factura por uso real", result.CalculationNotes);
        Assert.Contains("costo real será menor", result.CalculationNotes);
    }

    /// <summary>
    /// Port de test_sql_db_in_elastic_pool_costs_zero:
    /// BD con elastic_pool_name => payg 0, calculated, nota con "Elastic Pool" y
    /// NUNCA se consulta el precio (lookup.assert_not_called()).
    /// </summary>
    [Fact]
    public void Db_In_Elastic_Pool_Costs_Zero()
    {
        var resource = Res.Row(
            ("resource_id", 22),
            ("sku_name", "GP_Gen5"),
            ("sku_tier", "GeneralPurpose"),
            ("sku_capacity", 4),
            ("location", "East US 2"),
            ("elastic_pool_name", "pool-prod"));

        var lookupCalled = false;
        var prices = new FakePriceRepository
        {
            GetSqlDbPriceDetailsFn = (_, _, _, _) =>
            {
                lookupCalled = true;
                return null;
            },
        };

        var result = MakeCalculator(prices).Calculate(Res.Rows(resource), 99)[0];

        // lookup.assert_not_called()
        Assert.False(lookupCalled);
        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(0.0, result.PaygMonthly!.Value, 5);
        Assert.Contains("Elastic Pool", result.CalculationNotes);
    }
}
