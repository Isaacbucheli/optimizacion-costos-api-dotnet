namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Port de <c>get_vm_prices</c> / <c>_select_vm_prices</c> de
/// app/pricing/price_repository.py (servicio "vm" del CalculatorRegistry).
///
/// Peculiaridades replicadas 1:1:
///   - PAYG filtra por OS: Windows usa producto con "Windows"; Linux usa el que NO lo tiene.
///   - Fallback Windows→Linux: si no hay PAYG Windows, usa Linux y marca os_used = "Linux (fallback)".
///   - RI es OS-agnostic pero se busca SOBRE EL PRECIO BASE LINUX (excluye Windows): cualquier
///     Reservation 1Y/3Y de la SKU+región que NO sea Windows.
///   - Excluye Cloud Services (incluso "CloudServices" sin espacio) y Spot/Low Priority.
///
/// El fallback de IA (_assistant_select sobre el lookup amplio "vm_region") NO se porta en
/// Fase 1: si la selección determinista no halla PAYG, se devuelve lo determinista
/// (payg_hourly = null), igual que cuando el asistente está deshabilitado.
/// </summary>
public sealed partial class SqlPriceRepository
{
    public VmPrices GetVmPrices(string armSkuName, string region, string osType)
    {
        const string serviceName = "Virtual Machines";

        // refresh-si-rancio por arm_sku_name (igual que _is_cache_fresh_for(..., arm_sku_name=...)).
        // Python: api.fetch_vm_prices([arm_sku_name], region, os_type=None).
        RefreshIfStale(
            serviceName,
            region,
            () => _client.FetchVmPrices(new[] { armSkuName }, region, null),
            $"vm {armSkuName} {region}",
            armSkuName: armSkuName);

        var cached = _cache.QueryCached(serviceName, region, armSkuName: armSkuName);
        var selected = SelectVmPrices(cached, osType);

        if (selected.PaygHourly is not null)
            return selected with { MatchStrategy = "deterministic" };

        // Fallback IA (paridad con Python get_vm_prices): el determinista no halló PAYG para esta
        // SKU exacta → traemos precios de TODA la región y dejamos que la IA elija un meter REAL de
        // cómputo (Consumption, no Cloud Services, no Spot/Low Priority, OS correcto).
        RefreshByFetchQueryIfStale($"vm_region {region}", () => _client.FetchVmRegionPrices(region));
        var broad = _cache.QueryCached(serviceName, region);
        var wantsWindows = (osType ?? string.Empty).ToLowerInvariant() == "windows";

        bool OsMatches(PriceRow c, bool windows) =>
            (c.ProductName ?? string.Empty).ToLowerInvariant().Contains("windows") == windows;

        var candidates = broad
            .Where(c => PriceSelectors.IsConsumption(c)
                && !PriceSelectors.IsCloudServices(c)
                && !PriceSelectors.IsSpotOrLowPriority(c)
                && OsMatches(c, wantsWindows))
            .ToList();

        var ctx = new Dictionary<string, object?>
        {
            ["arm_sku_name"] = armSkuName,
            ["region"] = region,
            ["os_type"] = osType,
            ["expected_product"] = "Virtual Machines",
            ["expected_unit"] = "hourly compute",
        };
        var payg = AssistSelect("vms", "payg_compute_hourly", ctx, candidates);

        // Para Windows sin PAYG Windows, Python reintenta con candidatos Linux (fallback OS).
        if (payg is null && wantsWindows)
        {
            var linuxCandidates = broad
                .Where(c => PriceSelectors.IsConsumption(c)
                    && !PriceSelectors.IsCloudServices(c)
                    && !PriceSelectors.IsSpotOrLowPriority(c)
                    && OsMatches(c, false))
                .ToList();
            var ctxLinux = new Dictionary<string, object?>
            {
                ["arm_sku_name"] = armSkuName,
                ["region"] = region,
                ["os_type"] = "Linux fallback for Windows",
                ["expected_product"] = "Virtual Machines",
                ["expected_unit"] = "hourly compute",
            };
            payg = AssistSelect("vms", "payg_compute_hourly_linux_fallback", ctxLinux, linuxCandidates);
        }

        if (payg is not null)
            return selected with { PaygHourly = payg.RetailPrice, MatchStrategy = payg.AiMatchStrategy };

        return selected;
    }

    /// <summary>
    /// Port de <c>_select_vm_prices(cached, os_type)</c>. RI OS-agnostic sobre base Linux,
    /// fallback Windows→Linux.
    /// </summary>
    private static VmPrices SelectVmPrices(IReadOnlyList<PriceRow> cached, string osType)
    {
        var osLower = (osType ?? string.Empty).ToLowerInvariant();

        var baseRows = cached
            .Where(c => !PriceSelectors.IsCloudServices(c) && !PriceSelectors.IsSpotOrLowPriority(c))
            .ToList();

        PriceRow? payg;
        string osUsed;

        if (osLower == "windows")
        {
            var windowsPayg = baseRows
                .Where(c => PriceSelectors.IsConsumption(c) && PriceSelectors.IsWindows(c))
                .ToList();
            payg = PriceSelectors.PreferPrimaryHourly(windowsPayg);
            if (payg is not null)
            {
                osUsed = "Windows";
            }
            else
            {
                var linuxPayg = baseRows
                    .Where(c => PriceSelectors.IsConsumption(c) && !PriceSelectors.IsWindows(c))
                    .ToList();
                payg = PriceSelectors.PreferPrimaryHourly(linuxPayg);
                osUsed = "Linux (fallback)";
            }
        }
        else
        {
            var linuxPayg = baseRows
                .Where(c => PriceSelectors.IsConsumption(c) && !PriceSelectors.IsWindows(c))
                .ToList();
            payg = PriceSelectors.PreferPrimaryHourly(linuxPayg);
            osUsed = "Linux";
        }

        var ri1y = baseRows.FirstOrDefault(
            c => PriceSelectors.IsReservation(c, "1 Year") && !PriceSelectors.IsWindows(c));
        var ri3y = baseRows.FirstOrDefault(
            c => PriceSelectors.IsReservation(c, "3 Years") && !PriceSelectors.IsWindows(c));

        return new VmPrices(
            PaygHourly: payg?.RetailPrice,
            Ri1yTotal: ri1y?.RetailPrice,
            Ri3yTotal: ri3y?.RetailPrice,
            OsUsed: osUsed);
    }
}
