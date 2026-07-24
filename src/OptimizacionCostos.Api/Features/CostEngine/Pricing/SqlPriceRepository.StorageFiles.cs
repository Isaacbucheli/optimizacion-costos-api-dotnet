using System.Text.RegularExpressions;

namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Precios de Azure Files por GiB/mes (spec 2026-07-24). Selección ESTRICTA de meters de
/// almacenamiento ("Data Stored"/"Provisioned") con exclusión explícita de transacciones,
/// metadata y snapshots de Files — endurecimiento tipo fix "&lt;tier&gt; Disk" de discos.
///
/// Mapeo verificado contra la Retail API real (tests/.../StorageFilesRetailFixture.md, fixture
/// Task 1, región eastus) — el fixture corrigió el mapeo provisional del plan en varios puntos:
///   - Consumption de "transaction_optimized" usa productName "Files v2" (NO "Files": ese
///     producto es legacy v1 y no tiene filas ZRS/GZRS).
///   - Consumption de "premium" usa meterName con prefijo "Premium " ("Premium {RED}
///     Provisioned") y unidad "1 GB/Month" (no GiB).
///   - Reservation tiene un productName DISTINTO al de Consumption: "Files Reserved Capacity"
///     (hot/cool) o "Premium Files Reserved Capacity" (premium) — el segundo CONTIENE al primero
///     como subcadena, así que la selección usa igualdad EXACTA de producto, nunca "contains".
///   - El retail_price de las filas Reservation es el TOTAL del término (12|36 meses) para el
///     bloque de capacidad codificado en el skuName ("Hot LRS - 10 TB" = 10.240 GiB TiB binario;
///     "- 100 TB" = 102.400 GiB). El unit_of_measure de esas filas ("1 GB/Month" en TODAS,
///     sin importar el bloque) es un remanente heredado y NO se usa para normalizar.
///   - "transaction_optimized" no tiene ninguna fila de Reservation (Azure Files Reserved
///     Capacity solo cubre Hot/Cool/Premium): el RI queda null sin tocar la cache de reservas.
///   - Los únicos tokens de redundancia con meter propio son LRS/ZRS/GRS/GZRS (premium solo
///     LRS/ZRS); RA-GRS/RA-GZRS no existen como meter — el llamador los mapea a GRS/GZRS antes
///     de invocar este método (Task 4).
/// </summary>
public sealed partial class SqlPriceRepository
{
    /// <summary>
    /// Spec de selección por tier: producto+meter de Consumption (PAYG), tokens de tier/redundancia
    /// para matchear la reserva por meter_name/sku_name, y el productName real de Reservation
    /// (null si el tier no tiene capacidad reservada disponible, ej. transaction_optimized).
    /// </summary>
    internal sealed record FilesMeterSpec(
        string Product, string PaygMeter, string TierToken, string Redundancy, string? ReservationProduct);

    private static readonly string[] FilesExcludedMeterTokens =
    [
        "Snapshot", "Metadata", "Operation", "Transaction", "Write", "Read",
        "List", "Protocol", "Early Delete", "Retrieval", "IOPS", "Throughput",
    ];

    public StorageFilesPrices GetStorageFilesPrices(string region, string tier, string redundancy)
    {
        const string serviceName = "Storage";
        RefreshByFetchQueryIfStale($"storage_files {region}", () => _client.FetchStorageFilesPrices(region));
        var cached = _cache.QueryCached(serviceName, region);

        var spec = FilesMeterFor(tier, redundancy);
        if (spec is null)
        {
            return new StorageFilesPrices(null, null, null, null);
        }

        var payg = SelectFilesConsumption(cached, spec);
        if (payg is null)
        {
            // Fallback IA auditado (como el resto de get_*): solo candidatos de almacenamiento.
            var pool = cached.Where(c => c.IsConsumption
                && c.ProductNameLower.Contains("files")
                && IsStoredCapacityUnit(c)
                && !IsExcludedFilesMeter(c)).ToList();
            payg = AssistSelect("storage_files", "data_stored",
                new Dictionary<string, object?>
                {
                    ["tier"] = tier, ["redundancy"] = redundancy, ["region"] = region,
                },
                pool);
        }

        // transaction_optimized: spec.ReservationProduct es null → sin RI, sin tocar la cache
        // de reservas (evita un null "accidental" que se confunda con "no encontrado").
        double? ri1 = null;
        double? ri3 = null;
        if (spec.ReservationProduct is not null)
        {
            ri1 = SelectFilesReservationPerGbMonth(cached, spec, "1 Year");
            ri3 = SelectFilesReservationPerGbMonth(cached, spec, "3 Years");
        }
        return new StorageFilesPrices(payg?.RetailPrice, ri1, ri3, payg?.MeterId);
    }

    /// <summary>tier interno → producto/meter del Retail API (fixture Task 1, §5 "Mapeo corregido").</summary>
    internal static FilesMeterSpec? FilesMeterFor(string tier, string redundancy) => tier switch
    {
        "hot" => new FilesMeterSpec("Files v2", $"Hot {redundancy} Data Stored", "Hot", redundancy, "Files Reserved Capacity"),
        "cool" => new FilesMeterSpec("Files v2", $"Cool {redundancy} Data Stored", "Cool", redundancy, "Files Reserved Capacity"),
        "transaction_optimized" => new FilesMeterSpec("Files v2", $"{redundancy} Data Stored", redundancy, redundancy, null),
        "premium" => new FilesMeterSpec("Premium Files", $"Premium {redundancy} Provisioned", redundancy, redundancy, "Premium Files Reserved Capacity"),
        _ => null,
    };

    private static PriceRow? SelectFilesConsumption(IReadOnlyList<PriceRow> cached, FilesMeterSpec spec)
    {
        // 1) match exacto de producto Y meter (el caso normal, determinista).
        // 2) fallback: producto por "contains" con el MISMO meter exacto (defensivo ante
        //    variaciones menores de nombre de producto; los meters de "Files v2" llevan prefijo
        //    de tier, así que "{RED} Data Stored" no colisiona).
        var exact = FilesConsumptionMatches(cached, spec, exactProduct: true);
        var chosen = exact.Count > 0 ? exact : FilesConsumptionMatches(cached, spec, exactProduct: false);
        // Con escalones de precio (tier_minimum_units) se toma el primer escalón (0). El fixture
        // confirma tierMinimumUnits = 0 en TODAS las filas de Files (§4.2), así que esto no
        // afecta el resultado hoy, pero se conserva por si Azure introduce escalones a futuro.
        return chosen.OrderBy(c => c.TierMinimumUnits ?? 0).FirstOrDefault();
    }

    private static List<PriceRow> FilesConsumptionMatches(
        IReadOnlyList<PriceRow> cached, FilesMeterSpec spec, bool exactProduct)
        => cached.Where(c => c.IsConsumption
            && (exactProduct
                ? string.Equals(c.ProductName, spec.Product, StringComparison.Ordinal)
                : (c.ProductName ?? "").Contains(spec.Product, StringComparison.Ordinal))
            && string.Equals(c.MeterName, spec.PaygMeter, StringComparison.Ordinal)
            && IsStoredCapacityUnit(c)
            && !IsExcludedFilesMeter(c)).ToList();

    /// <summary>
    /// Reserva normalizada a GiB/mes para el término dado. El productName de Reservation es
    /// DISTINTO al de Consumption (spec.ReservationProduct, ej. "Files Reserved Capacity") y se
    /// exige IGUALDAD EXACTA — "Premium Files Reserved Capacity" contiene "Files Reserved
    /// Capacity" como subcadena, así que un match por "contains" confundiría hot/cool con
    /// premium. Se prefiere el bloque de capacidad más chico (10 TiB antes que 100 TiB) para dar
    /// una tasa de referencia conservadora e independiente del orden de la cache.
    /// </summary>
    private static double? SelectFilesReservationPerGbMonth(
        IReadOnlyList<PriceRow> cached, FilesMeterSpec spec, string term)
    {
        if (spec.ReservationProduct is null)
        {
            return null;
        }
        var rows = cached.Where(c => c.IsReservation(term)
                && string.Equals(c.ProductName, spec.ReservationProduct, StringComparison.Ordinal)
                && !IsExcludedFilesMeter(c)
                && ContainsTierAndRedundancy(c, spec))
            .OrderBy(c => ParseReservedBlockGib(c.SkuName) ?? double.MaxValue);
        foreach (var row in rows)
        {
            var normalized = NormalizeReservedPerGbMonth(row, term);
            if (normalized is not null)
            {
                return normalized;
            }
        }
        return null;
    }

    /// <summary>
    /// El tier y la redundancia deben aparecer como TOKEN completo (con límite de palabra) en
    /// meter_name o sku_name de la reserva. Usa límite de palabra (no un Contains ingenuo)
    /// porque "ZRS" es subcadena de "GZRS": sin límite de palabra, buscar la reserva "ZRS"
    /// también matchearía (incorrectamente) una fila "GZRS".
    /// </summary>
    private static bool ContainsTierAndRedundancy(PriceRow c, FilesMeterSpec spec)
    {
        var haystack = $"{c.MeterName} {c.SkuName}";
        return HasWordToken(haystack, spec.TierToken) && HasWordToken(haystack, spec.Redundancy);
    }

    private static bool HasWordToken(string haystack, string token)
        => Regex.IsMatch(haystack, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// retail_price (TOTAL del término por el bloque de capacidad del skuName) → precio/GiB-mes.
    /// NO usa unit_of_measure (miente: dice "1 GB/Month" en toda fila de Reservation sin importar
    /// el bloque real) — usa <see cref="ParseReservedBlockGib"/> sobre el skuName.
    /// </summary>
    internal static double? NormalizeReservedPerGbMonth(PriceRow row, string term)
    {
        if (row.RetailPrice is null)
        {
            return null;
        }
        var months = term == "1 Year" ? 12.0 : term == "3 Years" ? 36.0 : 0.0;
        var gib = ParseReservedBlockGib(row.SkuName);
        if (months <= 0 || gib is null or <= 0)
        {
            return null;
        }
        return row.RetailPrice.Value / months / gib.Value;
    }

    /// <summary>
    /// Sufijo del skuName de Reservation → GiB del bloque. "- 10 TB" significa 10 TiB
    /// (10.240 GiB); "- 100 TB" significa 100 TiB (102.400 GiB) — el "TB" del skuName de la
    /// Retail API es TiB binario, NO decimal (fixture §4.1/§6.3). Null si no matchea (ej. skuName
    /// de Consumption, que no lleva sufijo de bloque).
    /// </summary>
    internal static double? ParseReservedBlockGib(string? skuName)
    {
        if (string.IsNullOrEmpty(skuName))
        {
            return null;
        }
        var m = Regex.Match(skuName, @"-\s*(10|100)\s*TB\s*$", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }
        return m.Groups[1].Value == "10" ? 10240.0 : 102400.0;
    }

    /// <summary>Unidad de Consumption de almacenamiento por GB/mes o GiB/mes (PAYG únicamente).</summary>
    private static bool IsStoredCapacityUnit(PriceRow c)
        => c.UnitOfMeasureLower.StartsWith("1 gb/month", StringComparison.Ordinal)
        || c.UnitOfMeasureLower.StartsWith("1 gib/month", StringComparison.Ordinal);

    private static bool IsExcludedFilesMeter(PriceRow c)
        => FilesExcludedMeterTokens.Any(t => (c.MeterName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
}
