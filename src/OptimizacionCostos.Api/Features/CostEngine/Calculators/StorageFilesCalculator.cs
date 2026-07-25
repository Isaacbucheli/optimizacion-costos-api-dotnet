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
/// desglose ya viene con ese criterio). RI 1y/3y HÍBRIDA COMPARABLE POR BLOQUES (spec
/// 2026-07-24, revisión 2026-07-24 post-review): Azure Files Reserved Capacity se compra en
/// BLOQUES ENTEROS de 10 TiB (10.240 GiB) por tier+redundancia+región (fixture
/// StorageFilesRetailFixture.md §6.3/§4.1: los skuName de reserva son literalmente
/// "Hot LRS - 10 TB") — no existe "reserva parcial" de un bloque. Por término, cada tier
/// aporta: si alcanza al menos 1 bloque Y Azure publica una tasa reservada para ese término,
/// bloque(s) completo(s) a la tasa reservada + el remanente (&lt; 1 bloque) a su tasa PAYG;
/// si no alcanza un bloque o Azure no publica esa tasa, el tier completo a su tasa PAYG. El
/// término se emite si ALGÚN tier aportó al menos un bloque reservado real (si ninguno lo
/// aportó, el término queda null, con una razón que distingue "ningún tier tiene tasa
/// reservada en Azure" de "ningún tier alcanza el bloque mínimo"). Esto evita el bug de "todo o
/// nada": antes, un solo tier sin reserva (típicamente transaction_optimized, que además es el
/// tier DEFAULT de los shares sin accessTier explícito) anulaba el RI completo aunque el resto
/// de la cuenta sí fuera reservable; y evita el bug de sobreestimar la reserva aplicando la
/// tasa de bloque a capacidad que Azure no vende en bloques parciales. No incluye
/// transacciones/metadata (nota). SKUs provisioned v2 → manual_required (modelo de facturación
/// distinto, sin inventar números).
/// </summary>
public sealed class StorageFilesCalculator(IPriceRepository prices, IPricingConstants constants) : ICostCalculator
{
    private readonly IPriceRepository _prices = prices;
    private readonly IPricingConstants _constants = constants;

    /// <summary>
    /// Tamaño del bloque mínimo comprable de Azure Files Reserved Capacity (fixture
    /// StorageFilesRetailFixture.md §6.3: "increments of 10 TiB and 100 TiB", skuName literal
    /// "Hot LRS - 10 TB"). 10 TiB binarios = 10.240 GiB. No existe reserva por debajo de este
    /// bloque ni por una fracción de él.
    /// </summary>
    internal const double ReservationBlockGib = 10240.0;

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
            double reservable1Gib = 0;
            double reservable3Gib = 0;
            // Distingue las dos causas de "sin RI" (usado solo si al final ningún término
            // aplica): true si ALGÚN tier con capacidad alcanzó el bloque mínimo (aunque le haya
            // faltado la tasa reservada) — en ese caso la causa es "Azure no publica reserva para
            // estos tiers"; false si NINGÚN tier llegó al bloque — la causa es puramente de
            // tamaño, "ningún tier alcanza el bloque mínimo".
            var anyTierReachesBlock = false;
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

                // RI híbrida comparable POR BLOQUES: Azure Files Reserved Capacity solo se
                // compra en bloques ENTEROS de 10 TiB (ReservationBlockGib) por tier — no existe
                // "reserva parcial" de un bloque (fixture §6.3/§4.1). Un tier con menos de un
                // bloque no puede comprar nada de reserva, sin importar si Azure publica una
                // tasa para ese tipo de tier: se cotiza 100% PAYG. Un tier con varios bloques
                // reserva solo los bloques COMPLETOS; el remanente (&lt; 1 bloque) se cotiza a su
                // propia tasa PAYG. Esto reemplaza el bug donde se aplicaba la tasa de bloque a
                // CUALQUIER cantidad de GiB, inflando el ahorro reportado (ver StorageFiles
                // RetailFixture.md §6.3 y el caso real documentado en el plan de este fix).
                //
                // TODO(bloques de 100 TiB): con gib >= 102.400 (100 TiB) el bloque de 100 TiB
                // tiene mejor tasa/GiB que el de 10 TiB (fixture §4.1: ~22% de descuento vs
                // ~18% a 1 año); SqlPriceRepository.StorageFiles.SelectFilesReservationPerGbMonth
                // siempre prefiere el bloque de 10 TiB (ordena por bloque ascendente). No se
                // corrige aquí — cuentas de 100+ TiB seguirán usando la tasa (más cara) del
                // bloque de 10 TiB, lo que SUBESTIMA el ahorro posible pero nunca lo sobreestima.
                var blocks = Math.Floor(gib / ReservationBlockGib);
                var reservedBlockGib = blocks * ReservationBlockGib;
                var paygRemainderGib = gib - reservedBlockGib;
                if (blocks >= 1)
                {
                    anyTierReachesBlock = true;
                }

                var tier1Reservable = blocks >= 1 && p.Ri1yPerGbMonth is > 0;
                ri1 += tier1Reservable
                    ? reservedBlockGib * p.Ri1yPerGbMonth!.Value + paygRemainderGib * p.PricePerGbMonth.Value
                    : gib * p.PricePerGbMonth.Value;
                if (tier1Reservable)
                {
                    ri1Applies = true;
                    reservable1Gib += reservedBlockGib;
                }

                var tier3Reservable = blocks >= 1 && p.Ri3yPerGbMonth is > 0;
                ri3 += tier3Reservable
                    ? reservedBlockGib * p.Ri3yPerGbMonth!.Value + paygRemainderGib * p.PricePerGbMonth.Value
                    : gib * p.PricePerGbMonth.Value;
                if (tier3Reservable)
                {
                    ri3Applies = true;
                    reservable3Gib += reservedBlockGib;
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
                // Dos causas distintas de "sin RI" (ver anyTierReachesBlock arriba): tamaño
                // (ningún tier junta un bloque de 10 TiB) vs disponibilidad (algún tier sí junta
                // un bloque, pero Azure no publica tasa reservada para ese tipo de tier, ej.
                // transaction_optimized). No se fusionan en una sola frase vaga.
                result.RiNotApplicableReason = anyTierReachesBlock
                    ? "Ningún tier de este storage account tiene capacidad reservada en Azure "
                      + "(transaction optimized no la soporta)"
                    : "Ningún tier de este storage account alcanza el bloque mínimo de 10 TiB que "
                      + "exige la reserva de Azure Files (sin un bloque completo no hay nada que comprar)";
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
            if (ri1Applies || ri3Applies)
            {
                // reservable1Gib/reservable3Gib son la capacidad YA ALINEADA A BLOQUES que cada
                // término puede cubrir (nunca la capacidad total del tier): si un tier publica
                // tasa a 1 año pero no a 3 (o viceversa), los dos términos pueden diferir y se
                // muestran por separado; si coinciden, se muestra un solo número.
                string reservableDesc;
                if (ri1Applies && ri3Applies && reservable1Gib == reservable3Gib)
                {
                    reservableDesc = $"{FormatGib(reservable1Gib)} GiB";
                }
                else
                {
                    var parts = new List<string>();
                    if (ri1Applies) { parts.Add($"{FormatGib(reservable1Gib)} GiB (1 año)"); }
                    if (ri3Applies) { parts.Add($"{FormatGib(reservable3Gib)} GiB (3 años)"); }
                    reservableDesc = string.Join(" / ", parts);
                }
                note +=
                    $" Reserva SOLO puede cubrir hasta {reservableDesc} de {billableNote} GiB facturables "
                    + "(bloques completos de 10 TiB ya comprables; el resto de cada tier y los "
                    + "tiers sin bloque completo o sin capacidad reservada en Azure se cotizan PAYG).";
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

    /// <summary>Formato uniforme de GiB en las notas (sin decimales de más).</summary>
    private static string FormatGib(double gib)
        => gib.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

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
