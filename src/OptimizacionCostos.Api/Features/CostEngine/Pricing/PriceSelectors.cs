using System.Text.RegularExpressions;

namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Helpers transversales de selección y normalización de precios. Port 1:1 de los
/// helpers de módulo en app/pricing/price_repository.py (los que NO son específicos de
/// un servicio). Los porta para que los archivos parciales por servicio de
/// <see cref="SqlPriceRepository"/> y sus tests reusen exactamente la misma lógica.
///
/// Reglas heredadas de Python:
///   - Las comparaciones de texto son case-insensitive salvo donde el original usa
///     comparación con mayúsculas exactas (ej. <c>_is_windows</c>, <c>_is_spot_or_low_priority</c>,
///     que usan "Windows"/"Spot"/"Low Priority" sobre el texto crudo).
///   - <c>_field(item, snake, camel)</c> ya no hace falta: <see cref="PriceRow"/> es un
///     modelo fuerte. Donde Python leía product_name/productName, aquí se lee
///     <see cref="PriceRow.ProductName"/>, etc.
/// </summary>
public static class PriceSelectors
{
    // -------------------- Predicados base --------------------

    /// <summary>Python: <c>_is_consumption</c>. price_type == "Consumption".</summary>
    public static bool IsConsumption(PriceRow item) => item.PriceType == "Consumption";

    /// <summary>Python: <c>_is_reservation(item, term)</c>. price_type == "Reservation" y reservation_term == term.</summary>
    public static bool IsReservation(PriceRow item, string term)
        => item.PriceType == "Reservation" && item.ReservationTerm == term;

    /// <summary>
    /// Python: <c>_contains(item, field_name, text)</c>. Devuelve True si <paramref name="text"/>
    /// (lower) está contenido en el campo (lower). <paramref name="fieldValue"/> es el valor del
    /// campo a inspeccionar (ej. <c>row.MeterName</c>), replicando el acceso por nombre de Python.
    /// </summary>
    public static bool Contains(string? fieldValue, string text)
        => (fieldValue ?? string.Empty).ToLowerInvariant().Contains((text ?? string.Empty).ToLowerInvariant());

    /// <summary>
    /// Python: <c>_is_windows</c>. "Windows" (case-sensitive, como el original) en product_name.
    /// </summary>
    public static bool IsWindows(PriceRow item) => (item.ProductName ?? string.Empty).Contains("Windows");

    /// <summary>
    /// Python: <c>_is_cloud_services</c>. La Retail API usa "Cloud Services" y "CloudServices"
    /// (sin espacio, ej. "Eadsv5 Series CloudServices"); compara normalizado quitando espacios.
    /// </summary>
    public static bool IsCloudServices(PriceRow item)
    {
        var product = (item.ProductName ?? string.Empty).ToLowerInvariant().Replace(" ", string.Empty);
        return product.Contains("cloudservices");
    }

    /// <summary>
    /// Python: <c>_is_spot_or_low_priority</c>. "Spot" o "Low Priority" (case-sensitive)
    /// en meter_name o sku_name.
    /// </summary>
    public static bool IsSpotOrLowPriority(PriceRow item)
    {
        var meter = item.MeterName ?? string.Empty;
        var sku = item.SkuName ?? string.Empty;
        return meter.Contains("Spot") || meter.Contains("Low Priority")
            || sku.Contains("Spot") || sku.Contains("Low Priority");
    }

    /// <summary>Python: <c>_is_linux_product</c>. "linux" (lower) en product_name.</summary>
    public static bool IsLinuxProduct(PriceRow item)
        => (item.ProductName ?? string.Empty).ToLowerInvariant().Contains("linux");

    // -------------------- Selección de mejor candidato --------------------

    /// <summary>
    /// Python: <c>_prefer_primary_hourly(items)</c>. Ordena por score y devuelve el mejor
    /// (o null si la lista está vacía). Score: +4 si is_primary_meter, +2 si unit contiene
    /// "hour", +1 si meter no contiene "free" ni "trial". Empate: respeta el orden estable
    /// de entrada (igual que <c>sorted(..., reverse=True)</c> de Python, que es estable).
    /// </summary>
    public static PriceRow? PreferPrimaryHourly(IReadOnlyList<PriceRow> items)
    {
        if (items is null || items.Count == 0)
            return null;

        static int Score(PriceRow item)
        {
            var value = 0;
            var unit = (item.UnitOfMeasure ?? string.Empty).ToLowerInvariant();
            var meter = (item.MeterName ?? string.Empty).ToLowerInvariant();
            if (item.IsPrimaryMeter)
                value += 4;
            if (unit.Contains("hour"))
                value += 2;
            if (!meter.Contains("free") && !meter.Contains("trial"))
                value += 1;
            return value;
        }

        // OrderByDescending es estable en .NET (preserva el orden de inserción ante empates),
        // igual que el sorted(reverse=True) estable de Python.
        return items.OrderByDescending(Score).First();
    }

    // -------------------- Normalización --------------------

    /// <summary>
    /// Python: <c>_normalize_price_token(value)</c>. Quita todo lo que no sea [a-z0-9] (sobre lower).
    /// </summary>
    public static string NormalizePriceToken(string? value)
        => Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", string.Empty);

    /// <summary>
    /// Normaliza una región "tal cual viene" a la forma de la Retail API: lower sin espacios.
    /// Python lo hace inline en <c>get_public_ip_monthly_price</c> (region.lower().replace(" ", "")).
    /// Las calculadoras normalizan la región ANTES de llamar al repositorio; este helper
    /// centraliza esa convención para los archivos parciales por servicio.
    /// </summary>
    public static string NormalizeRegion(string? region)
        => (region ?? string.Empty).ToLowerInvariant().Replace(" ", string.Empty);

    // -------------------- Tokens / matching por servicio compartido --------------------

    /// <summary>
    /// Python: <c>_vcore_count_from_sku(sku_name)</c>. Extrae el número de vCores del sku.
    /// Primero busca "_<letras opcionales><dígitos>", luego "<dígitos> vCore" (case-insensitive).
    /// Default 1.
    /// </summary>
    public static int VcoreCountFromSku(string? skuName)
    {
        var sku = skuName ?? string.Empty;
        var m = Regex.Match(sku, @"_(?:[A-Za-z]+)?(\d+)");
        if (m.Success)
            return int.Parse(m.Groups[1].Value);
        m = Regex.Match(sku, @"(\d+)\s*vCore", RegexOptions.IgnoreCase);
        if (m.Success)
            return int.Parse(m.Groups[1].Value);
        return 1;
    }

    /// <summary>
    /// Python: <c>_app_service_sku_matches(item, sku_name)</c>. True si el token normalizado del
    /// sku esperado aparece en el token normalizado de sku_name / arm_sku_name / meter_name.
    /// Si el sku esperado es vacío, devuelve True (sin filtro).
    /// </summary>
    public static bool AppServiceSkuMatches(PriceRow item, string? skuName)
    {
        var expected = NormalizePriceToken(skuName);
        if (string.IsNullOrEmpty(expected))
            return true;
        var tokens = new[] { item.SkuName, item.ArmSkuName, item.MeterName };
        return tokens.Any(t => NormalizePriceToken(t).Contains(expected));
    }

    /// <summary>
    /// Python: <c>_redis_capacity_tokens(sku_capacity)</c>. Tokens "C{n}", "P{n}", "E{n}",
    /// "Enterprise E{n}".
    /// </summary>
    public static IReadOnlyList<string> RedisCapacityTokens(int skuCapacity)
        => new[] { $"C{skuCapacity}", $"P{skuCapacity}", $"E{skuCapacity}", $"Enterprise E{skuCapacity}" };

    /// <summary>
    /// Python: <c>_redis_matches_capacity(item, sku_capacity)</c>. True si algún token de
    /// capacidad (lower) aparece en sku_name + meter_name + product_name (lower). En Python
    /// sku_capacity podía ser None → True; aquí <see cref="int"/> siempre tiene valor, así que
    /// el caller decide si saltarse la verificación.
    /// </summary>
    public static bool RedisMatchesCapacity(PriceRow item, int skuCapacity)
    {
        var haystack = string.Join(" ", item.SkuName ?? string.Empty,
            item.MeterName ?? string.Empty, item.ProductName ?? string.Empty).ToLowerInvariant();
        var tokens = RedisCapacityTokens(skuCapacity).Select(t => t.ToLowerInvariant());
        return tokens.Any(haystack.Contains);
    }

    /// <summary>
    /// Python: <c>_mysql_series_tokens(sku_name, sku_tier)</c>. Tokens de serie para filtrar
    /// productos MySQL Flexible Server. Replica los quirks exactos (burstable, ddsv4, etc.).
    /// </summary>
    public static IReadOnlyList<string> MySqlSeriesTokens(string? skuName, string skuTier = "")
    {
        var sku = (skuName ?? string.Empty).ToLowerInvariant();
        var tier = (skuTier ?? string.Empty).ToLowerInvariant();
        var tokens = new List<string>();
        if (sku.Contains("standard_b") || tier.Contains("burstable"))
        {
            tokens.Add("burstable");
            tokens.Add(sku.Replace("standard_", string.Empty).ToUpperInvariant());
        }
        if (sku.Contains("d2ds_v4") || sku.Contains("ddsv4"))
        {
            tokens.Add("general purpose");
            tokens.Add("ddsv4");
        }
        else if (sku.Contains("d") && tier.Contains("general"))
        {
            tokens.Add("general purpose");
        }
        if (sku.Contains("e") || tier.Contains("memory"))
        {
            tokens.Add("memory optimized");
        }
        return tokens;
    }

    /// <summary>
    /// Python: <c>_sql_mi_product_tokens(sku_tier, sku_family)</c>. Tokens de producto para
    /// SQL Managed Instance (tier + familia). Replica el árbol de decisión exacto.
    /// </summary>
    public static IReadOnlyList<string> SqlMiProductTokens(string skuTier = "", string skuFamily = "")
    {
        var tier = (skuTier ?? string.Empty).ToLowerInvariant();
        var family = (skuFamily ?? string.Empty).ToLowerInvariant();
        var tokens = new List<string>();

        if (tier.Contains("business") || tier == "bc")
            tokens.Add("business critical");
        else
            tokens.Add("general purpose");

        if (family.Contains("premium") && family.Contains("memory"))
        {
            tokens.Add("premium series");
            tokens.Add("memory optimized");
        }
        else if (family.Contains("premium"))
        {
            tokens.Add("premium series");
        }
        else if (family.Contains("gen4"))
        {
            tokens.Add("gen4");
        }
        else
        {
            tokens.Add("gen5");
        }
        return tokens;
    }
}
