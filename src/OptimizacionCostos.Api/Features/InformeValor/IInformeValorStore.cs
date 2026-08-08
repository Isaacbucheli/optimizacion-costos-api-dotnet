namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// <see cref="RowsMerged"/> es la cuenta de filas fusionadas de <c>ParseResult.RowsMerged</c> (solo
/// aplica a facturación: BitcostParser fusiona, CasosParser nunca). Viaja junto a
/// <see cref="Filas"/> para que la calculadora pueda publicar "revisado línea por línea sobre N
/// registros" con el N de antes de fusionar, tal como lo hace la plantilla, sin tener que volver a
/// leer la tabla de facturación completa solo para contarlas.
/// </summary>
public sealed record InsumoEstado(
    string Kind, bool Cargado, string? SourceFileName, DateTime? CargadoEn,
    int Filas, int RowsMerged, string? Status, IReadOnlyList<string> Warnings);

public interface IInformeValorStore
{
    Task<int> ReplaceFacturacionAsync(
        int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct);

    Task<int> ReplaceCasosAsync(
        int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct);

    Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct);

    Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct);
}
