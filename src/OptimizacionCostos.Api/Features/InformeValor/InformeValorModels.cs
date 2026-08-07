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
    IReadOnlyList<T> Rows, int RowsTotal, int RowsSkipped, int TruncatedValues,
    IReadOnlyList<string> Warnings);
