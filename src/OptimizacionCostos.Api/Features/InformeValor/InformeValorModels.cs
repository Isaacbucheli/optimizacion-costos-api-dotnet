namespace OptimizacionCostos.Api.Features.InformeValor;

public sealed record FacturacionRow(
    string Hash, string? Tenant, string? SubscriptionName, string? SubscriptionId,
    string? ResourceGroup, string? ResourceName, string? CostCenter, string? Category,
    string? Subcategory, string? Service, decimal? Quantity, string? Unit, decimal? Rate,
    decimal Pvp, short Year, byte Month);

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
