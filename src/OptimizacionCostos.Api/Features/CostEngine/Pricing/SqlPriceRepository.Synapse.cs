using System.Text.RegularExpressions;

namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Parcial de precios de Synapse Dedicated SQL Pool. Port 1:1 de
/// <c>get_synapse_dw_prices</c> y del selector <c>_select_synapse_dw_prices</c>
/// de app/pricing/price_repository.py.
/// </summary>
public sealed partial class SqlPriceRepository
{
    // Python: re.search(r"DW(\d+)C?", level) sobre el nivel en MAYÚSCULAS.
    private static readonly Regex DwLevelRegex = new(@"DW(\d+)C?", RegexOptions.Compiled);

    /// <summary>
    /// Precios de Synapse Dedicated SQL Pool por nivel DWc.
    /// Port de <c>get_synapse_dw_prices(dw_level, region)</c>.
    /// </summary>
    public SynapseDwPrices GetSynapseDwPrices(string dwLevel, string region)
    {
        const string serviceName = "Azure Synapse Analytics";
        var fetchQuery = $"synapse_dw {region}";

        // refresh-si-rancio por fetch_query (igual que Python).
        RefreshByFetchQueryIfStale(fetchQuery, () => _client.FetchSynapseDwPrices(region));

        var cached = _cache.QueryCached(serviceName, region);
        return SelectSynapseDwPrices(cached, dwLevel);
    }

    /// <summary>
    /// Port de <c>_select_synapse_dw_prices</c>.
    /// dw_level ej. 'DW100c'. PAYG: fila Consumption con sku_name == nivel
    /// (precio por hora del nivel completo). Si no existe el nivel exacto,
    /// fallback al meter genérico 'SQL Provisioned DWU' (por 100 DWU/hora)
    /// multiplicado por bloques. Reservas: solo existen por bloque de 100 DWUs
    /// (sku DW100c), se escalan por bloques.
    /// </summary>
    internal static SynapseDwPrices SelectSynapseDwPrices(IReadOnlyList<PriceRow> cached, string dwLevel)
    {
        var level = (dwLevel ?? string.Empty).ToUpperInvariant();
        var match = DwLevelRegex.Match(level);
        var dwu = match.Success ? int.Parse(match.Groups[1].Value) : 0;
        var blocks = dwu != 0 ? Math.Max(1, dwu / 100) : 1;

        // pool = filas cuyo product_name contiene "Dedicated SQL Pool".
        var pool = cached
            .Where(c => (c.ProductName ?? string.Empty).Contains("Dedicated SQL Pool"))
            .ToList();

        // PAYG: Consumption + sku_name (MAYÚS) == nivel exacto.
        var payg = PriceSelectors.PreferPrimaryHourly(
            pool.Where(c =>
                PriceSelectors.IsConsumption(c)
                && (c.SkuName ?? string.Empty).ToUpperInvariant() == level)
            .ToList());
        double? paygHourly = payg?.RetailPrice;
        string? paygMeterId = payg?.MeterId;

        // Fallback al meter genérico "SQL Provisioned DWU" × bloques.
        if (paygHourly is null && dwu != 0)
        {
            var generic = PriceSelectors.PreferPrimaryHourly(
                cached.Where(c =>
                    PriceSelectors.IsConsumption(c)
                    && (c.ProductName ?? string.Empty).Contains("SQL Provisioned DWU"))
                .ToList());
            var per100Dwu = generic?.RetailPrice;
            if (per100Dwu is not null)
            {
                paygHourly = (double)per100Dwu * blocks;
                paygMeterId = generic?.MeterId;
            }
        }

        // Reservas: primera fila Reservation por término dentro del pool.
        var ri1y = pool.FirstOrDefault(c => PriceSelectors.IsReservation(c, "1 Year"));
        var ri3y = pool.FirstOrDefault(c => PriceSelectors.IsReservation(c, "3 Years"));
        double? ri1yTotal = ri1y?.RetailPrice;
        double? ri3yTotal = ri3y?.RetailPrice;

        return new SynapseDwPrices(
            PaygHourly: paygHourly,
            Ri1yTotal: ri1yTotal is not null ? (double)ri1yTotal * blocks : null,
            Ri3yTotal: ri3yTotal is not null ? (double)ri3yTotal * blocks : null,
            Dwu: dwu,
            Blocks: blocks,
            PaygMeterId: paygMeterId);
    }
}
