namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Servicio "app_service" del CalculatorRegistry. Port 1:1 de las funciones de
/// app/pricing/price_repository.py:
///   - get_app_service_prices / _select_app_service_prices / _app_service_base_candidates /
///     _app_service_has_reservation_candidate  → <see cref="GetAppServicePrices"/>.
///   - get_elastic_premium_base_price / _select_duration_unit_prices → <see cref="GetElasticPremiumBasePrice"/>.
///
/// Peculiaridades portadas:
///   - App Service separa Linux vs Windows por producto ("linux" en product_name) tanto en
///     PAYG como en RI; conserva AMBAS RI (la RI Linux para SKUs Linux, la Windows para Windows).
///   - El SKU se filtra normalizado (P2 v3 ≡ P2v3) vía PriceSelectors.AppServiceSkuMatches.
///   - Refresh de RI: si hay PAYG pero falta alguna RI esperada y SÍ existe alguna RI para el SKU,
///     se re-hace el fetch (Linux+Windows, deduplicado por (meterId,skuId,type,reservationTerm)).
///   - El fallback de IA (_assistant_select) queda FUERA de alcance: si la selección
///     determinista no halla PAYG, se devuelve lo que haya (payg_hourly = null).
///   - Elastic Premium (EP*/WS*): costo base/hora = vCPU*precio_vCPU/h + GiB*precio_RAM_GiB/h,
///     leyendo los meters de duración del servicio "Functions" (EP*) o "Logic Apps" (WS*).
/// </summary>
public sealed partial class SqlPriceRepository
{
    private const string AppServiceServiceName = "Azure App Service";

    public AppServicePrices GetAppServicePrices(string armSkuName, string region, bool isLinux)
    {
        // Refresh por sku_name=arm_sku_name (fetch Windows + Linux deduplicado).
        if (!_cache.IsCacheFreshFor(AppServiceServiceName, region, skuName: armSkuName))
        {
            try
            {
                var all = FetchAppServiceWinLinuxDeduped(armSkuName, region);
                _cache.DeleteStale(AppServiceServiceName, region, skuName: armSkuName);
                _cache.Insert(all, $"appservice {armSkuName} {region}");
            }
            catch
            {
                // Paridad con Python: loguea y sigue con la cache vieja.
            }
        }

        var cached = _cache.QueryCached(AppServiceServiceName, region, skuName: armSkuName);
        var selected = SelectAppServicePrices(cached, isLinux, armSkuName);
        var hasAnyRiForSku = AppServiceHasReservationCandidate(cached, armSkuName);
        var hasExpectedRi = selected.Ri1yTotal is not null && selected.Ri3yTotal is not null;

        if (selected.PaygHourly is not null && (!hasAnyRiForSku || hasExpectedRi))
            return selected with { MatchStrategy = "deterministic" };

        // Hay PAYG y alguna RI para el SKU, pero falta la RI esperada → re-fetch dirigido.
        if (selected.PaygHourly is not null && hasAnyRiForSku && !hasExpectedRi)
        {
            try
            {
                var all = FetchAppServiceWinLinuxDeduped(armSkuName, region);
                _cache.DeleteStale(AppServiceServiceName, region, skuName: armSkuName);
                _cache.Insert(all, $"appservice {armSkuName} {region}");
                cached = _cache.QueryCached(AppServiceServiceName, region, skuName: armSkuName);
                selected = SelectAppServicePrices(cached, isLinux, armSkuName);
                if (selected.PaygHourly is not null)
                    return selected with { MatchStrategy = "deterministic" };
            }
            catch
            {
                // Paridad con Python: loguea y sigue.
            }
        }

        // Fallback amplio por región (sin IA): re-hidrata la cache pero el _assistant_select
        // de Python no se porta, así que se devuelve la selección determinista tal cual.
        var broadQuery = $"appservice_region {region}";
        RefreshByFetchQueryIfStale(broadQuery, () => _client.FetchAppServiceRegionPrices(region));

        return selected;
    }

    public ElasticPremiumBase? GetElasticPremiumBasePrice(string skuName, string region)
    {
        var sku = (skuName ?? string.Empty).ToUpperInvariant();
        if (!ElasticPremiumSpecs.TryGetValue(sku, out var spec))
            return null;
        var (vcpuCount, gb) = spec;

        var isFunctions = sku.StartsWith("EP", StringComparison.Ordinal);
        string serviceName, vcpuMeter, memMeter;
        Func<IReadOnlyList<PriceRow>> fetch;
        if (isFunctions)
        {
            serviceName = "Functions";
            vcpuMeter = "Premium vCPU Duration";
            memMeter = "Premium Memory Duration";
            fetch = () => _client.FetchFunctionsPremiumPrices(region);
        }
        else
        {
            serviceName = "Logic Apps";
            vcpuMeter = "Standard vCPU Duration";
            memMeter = "Standard Memory Duration";
            fetch = () => _client.FetchLogicAppsStandardPrices(region);
        }

        var fetchQuery = $"premium_plan {serviceName} {region}";
        RefreshByFetchQueryIfStale(fetchQuery, fetch);

        var cached = _cache.QueryCached(serviceName, region);
        var (vcpuHourly, memGibHourly) = SelectDurationUnitPrices(cached, vcpuMeter, memMeter);
        if (vcpuHourly is null || memGibHourly is null)
            return null;

        var baseHourly = vcpuCount * vcpuHourly.Value + gb * memGibHourly.Value;
        return new ElasticPremiumBase(
            BaseHourly: baseHourly,
            VcpuHourly: vcpuHourly.Value,
            MemGibHourly: memGibHourly.Value,
            Vcpu: vcpuCount,
            Gb: gb);
    }

    // -------------------- Helpers de selección (port de los helpers de módulo Python) --------------------

    /// <summary>
    /// Python: fetch Windows + Linux y deduplica por (meterId, skuId, type, reservationTerm).
    /// PriceRow no lleva skuId; replicamos la clave con (MeterId, SkuName, PriceType, ReservationTerm),
    /// que es la información disponible tras el mapeo del cliente (skuId de la Retail API ≈ sku_name).
    /// </summary>
    private IReadOnlyList<PriceRow> FetchAppServiceWinLinuxDeduped(string armSkuName, string region)
    {
        var win = _client.FetchAppServicePrices(armSkuName, region, isLinux: false);
        var linux = _client.FetchAppServicePrices(armSkuName, region, isLinux: true);
        var seen = new HashSet<(string?, string?, string?, string?)>();
        var all = new List<PriceRow>();
        foreach (var it in win.Concat(linux))
        {
            var key = (it.MeterId, it.SkuName, it.PriceType, it.ReservationTerm);
            if (seen.Add(key))
                all.Add(it);
        }
        return all;
    }

    /// <summary>Python: <c>_app_service_base_candidates(cached, sku_name)</c>.</summary>
    private static List<PriceRow> AppServiceBaseCandidates(IReadOnlyList<PriceRow> cached, string skuName = "")
    {
        var baseList = cached.Where(c =>
            (c.ProductName ?? string.Empty).Contains("App Service")
            && !PriceSelectors.Contains(c.MeterName, "Certificate")
            && !PriceSelectors.Contains(c.MeterName, "Domain")).ToList();

        if (!string.IsNullOrEmpty(skuName))
            baseList = baseList.Where(c => PriceSelectors.AppServiceSkuMatches(c, skuName)).ToList();

        return baseList;
    }

    /// <summary>Python: <c>_app_service_has_reservation_candidate(cached, sku_name)</c>.</summary>
    private static bool AppServiceHasReservationCandidate(IReadOnlyList<PriceRow> cached, string skuName)
        => AppServiceBaseCandidates(cached, skuName).Any(c =>
            PriceSelectors.IsReservation(c, "1 Year") || PriceSelectors.IsReservation(c, "3 Years"));

    /// <summary>Python: <c>_select_app_service_prices(cached, is_linux, sku_name)</c>.</summary>
    private static AppServicePrices SelectAppServicePrices(
        IReadOnlyList<PriceRow> cached, bool isLinux, string skuName = "")
    {
        var baseList = AppServiceBaseCandidates(cached, skuName);

        List<PriceRow> paygItems = isLinux
            ? baseList.Where(c => PriceSelectors.IsConsumption(c) && PriceSelectors.IsLinuxProduct(c)).ToList()
            : baseList.Where(c => PriceSelectors.IsConsumption(c) && !PriceSelectors.IsLinuxProduct(c)).ToList();

        var payg = PriceSelectors.PreferPrimaryHourly(paygItems);

        // RI del MISMO OS que el solicitado (Linux para Linux, Windows para Windows).
        var reservationBase = baseList.Where(c => PriceSelectors.IsLinuxProduct(c) == isLinux).ToList();
        var ri1y = reservationBase.FirstOrDefault(c => PriceSelectors.IsReservation(c, "1 Year"));
        var ri3y = reservationBase.FirstOrDefault(c => PriceSelectors.IsReservation(c, "3 Years"));

        return new AppServicePrices(
            PaygHourly: payg?.RetailPrice,
            Ri1yTotal: ri1y?.RetailPrice,
            Ri3yTotal: ri3y?.RetailPrice);
    }

    /// <summary>
    /// Python: <c>_select_duration_unit_prices(cached, vcpu_meter, mem_meter)</c>. Extrae el
    /// precio por vCPU/hora y por GiB-RAM/hora de los meters de duración (Consumption, retail &gt; 0).
    /// </summary>
    private static (double? VcpuHourly, double? MemGibHourly) SelectDurationUnitPrices(
        IReadOnlyList<PriceRow> cached, string vcpuMeter, string memMeter)
    {
        double? Pick(string meterText)
        {
            var candidates = cached.Where(c =>
                PriceSelectors.IsConsumption(c)
                && PriceSelectors.Contains(c.MeterName, meterText)
                && (c.RetailPrice ?? 0) > 0).ToList();
            var item = candidates.Count > 0 ? PriceSelectors.PreferPrimaryHourly(candidates) : null;
            return item?.RetailPrice;
        }

        return (Pick(vcpuMeter), Pick(memMeter));
    }
}
