using System.Text.Json;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de storage accounts con Azure Files (service_key "storage_files",
/// spec 2026-07-24). Solo llegan aquí cuentas cuya capacidad facturable superó 10 TiB
/// (corte aplicado en la importación por StorageFilesEnricher).
///
/// Costo = Σ por tier (tier_breakdown_json, GiB facturables) × precio/GiB-mes del tier
/// con la redundancia del SKU. Estándar factura GiB usados MÁS el diferencial de snapshots
/// del share (ya sumado por StorageFilesEnricher: Azure factura pay-as-you-go sobre
/// "Data Stored", que incluye el diferencial de snapshots); premium GiB de cuota (el
/// desglose ya viene con ese criterio). RI 1y/3y HÍBRIDA COMPARABLE (spec 2026-07-24, revisión):
/// por término, Σ de (gib × tasa reservada si el tier la tiene, sino gib × su tasa PAYG) — el
/// término se emite si ALGÚN tier con capacidad aportó una tasa reservada real (si ninguno la
/// tiene, ej. transaction_optimized —que nunca la tiene en Azure—, el término queda null). Esto
/// evita el bug de "todo o nada": antes, un solo tier sin reserva (típicamente
/// transaction_optimized, que además es el tier DEFAULT de los shares sin accessTier explícito)
/// anulaba el RI completo aunque el resto de la cuenta sí fuera reservable. No incluye
/// transacciones/metadata (nota). SKUs provisioned v2 → manual_required (modelo de facturación
/// distinto, sin inventar números).
/// </summary>
public sealed class StorageFilesCalculator(IPriceRepository prices, IPricingConstants constants) : ICostCalculator
{
    private readonly IPriceRepository _prices = prices;
    private readonly IPricingConstants _constants = constants;

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        var hours = _constants.HoursPerMonth();
        // Memoización por invocación: varias cuentas comparten (región, tier, redundancia) y
        // GetStorageFilesPrices hace un round-trip a SQL por llamada (fresh-check + query de
        // cache "Storage" completa); con miles de storage accounts esto evita repetirlo.
        var priceCache = new Dictionary<(string Region, string Tier, string Redundancy), StorageFilesPrices>();

        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "storage_files");

            var sku = r.GetString("files_sku") ?? r.GetString("sku_name") ?? "";
            var region = PriceSelectors.NormalizeRegion(r.GetString("location"));
            var billableGib = r.GetDouble("billable_gib");
            var shareCount = r.GetInt("share_count") ?? 0;
            var breakdown = ParseTierBreakdown(r.GetString("tier_breakdown_json"));

            if (sku.StartsWith("StandardV2_", StringComparison.OrdinalIgnoreCase)
                || sku.StartsWith("PremiumV2_", StringComparison.OrdinalIgnoreCase))
            {
                result.CalculationStatus = "manual_required";
                result.CalculationNotes =
                    $"Files provisioned v2 (sku={sku}): modelo de facturación distinto, requiere costeo manual";
                results.Add(result);
                continue;
            }

            if (breakdown.Count == 0 || billableGib is null or <= 0 || string.IsNullOrEmpty(region))
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = "Storage account sin desglose de capacidad de Azure Files";
                results.Add(result);
                continue;
            }

            var redundancy = RedundancyToken(sku);
            if (redundancy is null)
            {
                // Guarda contra sub-costeo silencioso: antes un sufijo no reconocido caía por
                // default a "LRS" (la redundancia MÁS BARATA; GZRS es ~2.3x LRS). Mejor fallar
                // explícito que inventar un número bajo.
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Redundancia del SKU no reconocida (sku={sku})";
                results.Add(result);
                continue;
            }

            double payg = 0, ri1 = 0, ri3 = 0;
            var ri1Applies = false;
            var ri3Applies = false;
            double reservableGib = 0;
            string? meterId = null;
            string? matchStrategy = null;
            var missingTiers = new List<string>();
            var failed = false;
            var pricedTiers = 0;

            foreach (var (tier, gib) in breakdown)
            {
                if (gib <= 0)
                {
                    continue;
                }
                StorageFilesPrices p;
                var key = (region, tier, redundancy);
                if (priceCache.TryGetValue(key, out var cachedPrice))
                {
                    p = cachedPrice;
                }
                else
                {
                    try
                    {
                        p = _prices.GetStorageFilesPrices(region, tier, redundancy);
                    }
                    catch (Exception ex)
                    {
                        result.CalculationStatus = "price_not_found";
                        result.CalculationNotes = $"Price lookup error: {ex.GetType().Name}";
                        failed = true;
                        break;
                    }
                    priceCache[key] = p;
                }
                // Nunca aceptar un precio $0 silencioso: null Y <= 0 son "no encontrado" (ver
                // guarda equivalente en SqlPriceRepository.StorageFiles.IsPositivePrice).
                if (p.PricePerGbMonth is null or <= 0)
                {
                    missingTiers.Add(tier);
                    continue;
                }
                payg += gib * p.PricePerGbMonth.Value;
                pricedTiers++;
                meterId ??= p.PaygMeterId;
                matchStrategy ??= string.IsNullOrEmpty(p.MatchStrategy) ? null : p.MatchStrategy;

                // RI híbrida comparable: cada término suma, tier por tier, la tasa reservada si
                // existe o la tasa PAYG de ese mismo tier si no (transaction_optimized nunca
                // tiene reserva en Azure; esto también cubre huecos puntuales de datos en tiers
                // que sí son reservables). El término solo "aplica" si algún tier aportó una tasa
                // reservada real — de lo contrario no hay nada que reservar.
                var tierIsReservable = false;
                if (p.Ri1yPerGbMonth is not null)
                {
                    ri1 += gib * p.Ri1yPerGbMonth.Value;
                    ri1Applies = true;
                    tierIsReservable = true;
                }
                else
                {
                    ri1 += gib * p.PricePerGbMonth.Value;
                }
                if (p.Ri3yPerGbMonth is not null)
                {
                    ri3 += gib * p.Ri3yPerGbMonth.Value;
                    ri3Applies = true;
                    tierIsReservable = true;
                }
                else
                {
                    ri3 += gib * p.PricePerGbMonth.Value;
                }
                if (tierIsReservable)
                {
                    reservableGib += gib;
                }
            }

            if (failed)
            {
                results.Add(result);
                continue;
            }
            if (missingTiers.Count > 0)
            {
                // Exactitud: sin precio de UN tier no se reporta suma parcial.
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes =
                    $"No price for Azure Files tiers: {string.Join(", ", missingTiers)} "
                    + $"(redundancia={redundancy}, region={region})";
                results.Add(result);
                continue;
            }
            if (pricedTiers == 0)
            {
                // Desglose sin capacidad positiva (inconsistencia con billable_gib): jamás emitir $0 "calculated".
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = "Storage account sin desglose de capacidad de Azure Files";
                results.Add(result);
                continue;
            }

            result.PaygMonthly = payg;
            result.PaygHourly = payg / hours;
            result.StorageMonthly = payg;
            result.PaygMeterId = meterId;
            if (ri1Applies) { result.Ri1yMonthly = ri1; }
            if (ri3Applies) { result.Ri3yMonthly = ri3; }
            result.RiApplies = result.Ri1yMonthly is not null || result.Ri3yMonthly is not null;
            if (!result.RiApplies)
            {
                result.RiNotApplicableReason =
                    "Ningún tier de este storage account tiene capacidad reservada en Azure "
                    + "(transaction optimized no la soporta)";
            }
            result.ComputeSavings();
            result.DiscardNonSavingRi();

            var billableNote = billableGib.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var note =
                $"Azure Files: {billableNote} GiB facturables en {shareCount} shares "
                + "(estándar por GiB usados + diferencial de snapshots del share, premium por cuota). "
                + "Incluye el uso diferencial de snapshots (respaldos/versiones). "
                + "No incluye transacciones ni metadata. "
                + "La reserva se adquiere en bloques de 10/100 TiB (se usa como referencia la tasa del bloque de 10 TiB).";
            if (reservableGib > 0)
            {
                var reservableNote = reservableGib.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                note +=
                    $" Reserva aplicable a {reservableNote} GiB de {billableNote} GiB "
                    + "(los tiers sin reserva se cotizan PAYG; transaction optimized no tiene "
                    + "capacidad reservada en Azure).";
            }
            note += $" match={matchStrategy ?? "deterministic"}";
            result.CalculationNotes = string.IsNullOrEmpty(result.CalculationNotes)
                ? note
                : $"{note} {result.CalculationNotes}";
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Sufijo del SKU → token de redundancia de los meters de Azure Files. RA-GRS/RA-GZRS
    /// no tienen meter propio (fixture StorageFilesRetailFixture.md §5): una cuenta con
    /// esa redundancia se factura bajo el meter GRS/GZRS respectivo, así que se mapean
    /// antes de consultar precios (nunca se emite el token "RA-GRS"/"RA-GZRS"). Null si el
    /// sufijo no es ninguno de los conocidos (el llamador NO debe asumir una redundancia por
    /// default: LRS es la MÁS BARATA y asumirla ante un sufijo desconocido sub-costearía).
    /// </summary>
    internal static string? RedundancyToken(string? sku)
    {
        var parts = (sku ?? "").Split('_');
        var suffix = parts.Length >= 2 ? parts[^1].ToUpperInvariant() : "";
        return suffix switch
        {
            "LRS" => "LRS",
            "ZRS" => "ZRS",
            "GRS" => "GRS",
            "RAGRS" => "GRS",
            "GZRS" => "GZRS",
            "RAGZRS" => "GZRS",
            _ => null,
        };
    }

    /// <summary>tier_breakdown_json ({"hot":8000.0,...}) → dict tier→GiB. Vacío si inválido.</summary>
    internal static IReadOnlyDictionary<string, double> ParseTierBreakdown(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return EmptyBreakdown;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return EmptyBreakdown;
            }
            var dict = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    dict[prop.Name] = prop.Value.GetDouble();
                }
            }
            return dict;
        }
        catch (JsonException)
        {
            return EmptyBreakdown;
        }
    }

    private static readonly IReadOnlyDictionary<string, double> EmptyBreakdown =
        new Dictionary<string, double>(StringComparer.Ordinal);
}
