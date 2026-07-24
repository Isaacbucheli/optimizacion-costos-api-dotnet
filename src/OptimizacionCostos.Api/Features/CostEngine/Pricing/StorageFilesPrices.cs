namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Precios de Azure Files por GiB/mes para un tier+redundancia+región: PAYG (Consumption
/// "Data Stored"/"Provisioned") y capacidad reservada 1y/3y ya normalizada a GiB/mes.
/// Miembros null cuando no hay meter (== None en Python; el calculador decide el estado).
/// <c>MatchStrategy</c>/<c>MatchConfidence</c> (igual que VmPrices/RedisPrices/...): null en la
/// ruta determinista, "assist_match:..." cuando el PAYG lo eligió el asistente IA.
/// </summary>
public sealed record StorageFilesPrices(
    double? PricePerGbMonth,
    double? Ri1yPerGbMonth,
    double? Ri3yPerGbMonth,
    string? PaygMeterId,
    string? MatchStrategy = null,
    double? MatchConfidence = null);
