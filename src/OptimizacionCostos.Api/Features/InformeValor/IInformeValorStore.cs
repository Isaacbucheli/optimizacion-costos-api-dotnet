namespace OptimizacionCostos.Api.Features.InformeValor;

public sealed record InsumoEstado(
    string Kind, bool Cargado, string? SourceFileName, DateTime? CargadoEn,
    int Filas, string? Status, IReadOnlyList<string> Warnings);

public interface IInformeValorStore
{
    Task<int> ReplaceFacturacionAsync(
        int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct);

    Task<int> ReplaceCasosAsync(
        int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct);

    Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct);

    Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct);
}
