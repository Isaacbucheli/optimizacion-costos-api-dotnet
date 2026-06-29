namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Registro de precio cacheado en dbo.azure_retail_prices. Port del dict que devuelve
/// <c>_query_cached(...)</c> en app/pricing/price_repository.py (más los campos que se
/// insertan en <c>_insert_prices_batch</c>).
///
/// Convenciones:
///   - Nombres C# PascalCase; cada propiedad documenta su columna SQL / clave Python.
///   - Precios anulables (<c>double?</c>) == None en Python.
///   - <see cref="IsPrimaryMeter"/> es bool (en SQL se persiste 1/0).
///   - Los textos son <c>string?</c> porque la Retail API puede omitirlos.
///
/// Los selectores de <see cref="PriceSelectors"/> consumen estas filas. En Python el
/// dict mezclaba claves snake_case (cache) y camelCase (items crudos de la API) y el
/// helper <c>_field</c> resolvía ambas; aquí el modelo es único y fuerte, así que los
/// selectores leen directamente las propiedades.
/// </summary>
public sealed record PriceRow
{
    /// <summary>price_id (PK identidad). Null en filas recién construidas desde la API.</summary>
    public long? PriceId { get; init; }

    /// <summary>service_name / serviceName. Ej. "Virtual Machines", "Storage".</summary>
    public string? ServiceName { get; init; }

    /// <summary>product_name / productName. Ej. "Virtual Machines D Series Windows".</summary>
    public string? ProductName { get; init; }

    /// <summary>sku_name / skuName. Ej. "P2v3", "1 vCore", "DW100c".</summary>
    public string? SkuName { get; init; }

    /// <summary>arm_sku_name / armSkuName. Ej. "Standard_D2as_v5".</summary>
    public string? ArmSkuName { get; init; }

    /// <summary>meter_name / meterName. Ej. "D2as v5", "P6 LRS Disk".</summary>
    public string? MeterName { get; init; }

    /// <summary>meter_id / meterId.</summary>
    public string? MeterId { get; init; }

    /// <summary>arm_region_name / armRegionName. Ej. "eastus2".</summary>
    public string? ArmRegionName { get; init; }

    /// <summary>price_type / type. "Consumption" o "Reservation".</summary>
    public string? PriceType { get; init; }

    /// <summary>reservation_term / reservationTerm. "1 Year" / "3 Years" o null.</summary>
    public string? ReservationTerm { get; init; }

    /// <summary>currency_code / currencyCode. Default "USD".</summary>
    public string? CurrencyCode { get; init; }

    /// <summary>unit_price / unitPrice.</summary>
    public double? UnitPrice { get; init; }

    /// <summary>retail_price / retailPrice. El precio que usan los selectores.</summary>
    public double? RetailPrice { get; init; }

    /// <summary>unit_of_measure / unitOfMeasure. Ej. "1 Hour", "1 GB/Month", "1/Day".</summary>
    public string? UnitOfMeasure { get; init; }

    /// <summary>tier_minimum_units / tierMinimumUnits.</summary>
    public double? TierMinimumUnits { get; init; }

    /// <summary>is_primary_meter / isPrimaryMeter. En SQL 1/0.</summary>
    public bool IsPrimaryMeter { get; init; }

    /// <summary>fetch_query. Etiqueta de la consulta que cacheó la fila (ej. "vm Standard_D2as_v5 eastus2").</summary>
    public string? FetchQuery { get; init; }

    /// <summary>cached_at (UTC). Usado por la lógica de freshness (TTL 24h).</summary>
    public DateTime? CachedAt { get; init; }

    // -------------------- Marca de selección por IA (asistente de precios) --------------------
    // Se setean cuando el precio lo eligió Azure OpenAI (fallback). Null = selección determinista.
    // El calculador propaga "assist_match" a calculation_notes → el front muestra "IA asistida".

    /// <summary>Python: <c>ai_match_strategy</c>. Ej. "assist_match:compute". Null si determinista.</summary>
    public string? AiMatchStrategy { get; init; }

    /// <summary>Python: <c>ai_match_confidence</c>. Confianza 0..1 reportada por la IA.</summary>
    public double? AiMatchConfidence { get; init; }

    /// <summary>Python: <c>ai_match_reason</c>. Explicación breve (es) de por qué eligió ese meter.</summary>
    public string? AiMatchReason { get; init; }

    // -------------------- Acceso conveniente (paridad con los selectores Python) --------------------

    /// <summary>True si <see cref="PriceType"/> == "Consumption". Python: <c>_is_consumption</c>.</summary>
    public bool IsConsumption => PriceSelectors.IsConsumption(this);

    /// <summary>True si es Reservation con el término dado. Python: <c>_is_reservation</c>.</summary>
    public bool IsReservation(string term) => PriceSelectors.IsReservation(this, term);

    /// <summary>Lowercase de <see cref="ProductName"/> (nunca null).</summary>
    public string ProductNameLower => (ProductName ?? string.Empty).ToLowerInvariant();

    /// <summary>Lowercase de <see cref="MeterName"/> (nunca null).</summary>
    public string MeterNameLower => (MeterName ?? string.Empty).ToLowerInvariant();

    /// <summary>Lowercase de <see cref="SkuName"/> (nunca null).</summary>
    public string SkuNameLower => (SkuName ?? string.Empty).ToLowerInvariant();

    /// <summary>Lowercase de <see cref="UnitOfMeasure"/> (nunca null).</summary>
    public string UnitOfMeasureLower => (UnitOfMeasure ?? string.Empty).ToLowerInvariant();
}
