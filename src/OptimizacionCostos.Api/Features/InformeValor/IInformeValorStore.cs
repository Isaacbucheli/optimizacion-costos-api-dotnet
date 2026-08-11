using OptimizacionCostos.Api.Features.InformeValor.Recolector;

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

    /// <summary>Reemplaza el insumo de RBAC de respaldo (entrega 2 del informe de valor). Recibe
    /// <see cref="RbacParseResult"/> en vez de <see cref="ParseResult{T}"/> porque el parser lleva
    /// metadata propia (qué hoja se leyó, los dos ejes medidos por este archivo) que no aplica a
    /// facturación ni casos; la implementación la proyecta a <see cref="ParseResult{T}"/> antes de
    /// reusar el mismo camino de persistencia.</summary>
    Task<int> ReplaceRbacAsync(
        int clientId, string fileName, string? user, RbacParseResult parsed, CancellationToken ct);

    Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct);

    Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct);

    /// <summary>
    /// Las filas de facturación ya persistidas de un cliente (Tarea 8: insumo del ensamblador del
    /// informe). Devuelve la carga vigente completa, sin filtrar por rango — el filtro D0 es
    /// responsabilidad de la calculadora, no de la lectura. Ordenada por <c>row_id</c> (orden de
    /// inserción de la carga vigente) para que dos lecturas de la misma carga siempre devuelvan las
    /// filas en el mismo orden: el orden de SQL sin <c>ORDER BY</c> no está garantizado, y
    /// <c>ConsumoCalculador</c> desempata por primer orden de aparición.
    /// </summary>
    Task<IReadOnlyList<FacturacionRow>> GetFacturacionAsync(int clientId, CancellationToken ct);

    /// <summary>Las filas de casos ya persistidas de un cliente. Mismo motivo de
    /// <c>ORDER BY row_id</c> que <see cref="GetFacturacionAsync"/>.</summary>
    Task<IReadOnlyList<CasoRow>> GetCasosAsync(int clientId, CancellationToken ct);

    /// <summary>
    /// Las filas de RBAC ya persistidas de un cliente, convertidas a <see cref="RbacFila"/>
    /// (<see cref="RbacFilaConverter"/> hace la conversión de texto a los tipos que
    /// <see cref="RbacFila"/> necesita — decisión 7 del brief). <b>Más débil que
    /// <see cref="RbacRecolector"/>:</b> ver el comentario de clase de <see cref="RbacFilaConverter"/>
    /// para qué campos de <see cref="RbacFila"/> no sobreviven esta vía y por qué.
    /// </summary>
    Task<IReadOnlyList<RbacFila>> GetRbacAsync(int clientId, CancellationToken ct);
}
