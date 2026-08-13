namespace OptimizacionCostos.Api.Features.InformeValor;

public sealed record FacturacionRow(
    string Hash, string? Tenant, string? SubscriptionName, string? SubscriptionId,
    string? ResourceGroup, string? ResourceName, string? CostCenter, string? Category,
    string? Subcategory, string? Service, decimal? Quantity, string? Unit, decimal? Rate,
    decimal Pvp, short Year, byte Month);

/// <summary>Una celda del pivot de evolución de BITCOST, ya expandida a grano
/// (categoría, subcategoría, recurso, año, mes). <see cref="IsReservation"/> marca las líneas
/// "Reserved VM Instance, SKU, región, término": el precio facturado de la reserva, que la
/// tabla de hechos no trae (spec, sección Insumos).</summary>
public sealed record EvolucionRow(
    string NaturalKeyHash,
    string? Category,
    string? Subcategory,
    string ResourceName,
    bool IsReservation,
    decimal Pvp,
    short PeriodYear,
    byte PeriodMonth);

public sealed record CasoRow(
    string Hash, string? Caso, DateOnly? FechaRegistro, string? Estado, decimal? SlaHoras,
    decimal? DuracionCruda, string? Cumple, string? Categoria, string? Subcategoria, string? Horario);

public sealed record ParseResult<T>(
    IReadOnlyList<T> Rows, int RowsTotal, int RowsSkipped,
    // Filas que ni se guardaron ni se contaron como descartadas: se fusionaron con otra de la
    // misma clave natural (hoy solo pasa en BitcostParser; CasosParser siempre manda 0 acá,
    // porque ahí una colisión es un duplicado real, no algo que sumar). Junto con RowsSkipped
    // deja la cuenta explicable: RowsTotal = Rows.Count + RowsSkipped + RowsMerged.
    int RowsMerged,
    int TruncatedValues, IReadOnlyList<string> Warnings);
