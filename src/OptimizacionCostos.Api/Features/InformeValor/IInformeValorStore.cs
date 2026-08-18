using OptimizacionCostos.Api.Features.InformeValor.Entrega;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// <see cref="RowsMerged"/> es la cuenta de filas fusionadas de <c>ParseResult.RowsMerged</c> (solo
/// aplica a facturación: BitcostParser fusiona, CasosParser nunca). Viaja junto a
/// <see cref="Filas"/> para que la calculadora pueda publicar "revisado línea por línea sobre N
/// registros" con el N de antes de fusionar, tal como lo hace la plantilla, sin tener que volver a
/// leer la tabla de facturación completa solo para contarlas.
/// </summary>
/// <param name="IngestaId">La corrida que dejó vigente este insumo. La necesita
/// <c>InformeValorController.Generar</c> para archivarla con la entrega (F4): los insumos son vivos
/// —cada carga borra la anterior— así que las filas no se pueden restaurar, pero con este id se
/// puede DETECTAR que el insumo ya no es el mismo y decirlo, en vez de reemitir en silencio contra
/// otro archivo.</param>
public sealed record InsumoEstado(
    string Kind, bool Cargado, string? SourceFileName, DateTime? CargadoEn,
    int Filas, int RowsMerged, string? Status, IReadOnlyList<string> Warnings,
    int IngestaId);

/// <summary>
/// El primer y el último mes que cubre un insumo cargado, como claves de mes calendario
/// ("aaaa-MM"). Es el rango REAL del archivo, no el que alguien quiso subir: sale de un MIN/MAX
/// sobre las filas vigentes.
/// </summary>
public sealed record RangoMeses(string Desde, string Hasta);

/// <summary>
/// Qué período cubre cada insumo con eje de tiempo. <c>null</c> en un insumo quiere decir que no
/// hay filas cargadas (o que ninguna trae fecha), y eso es distinto de un rango vacío: el front
/// propone el período por defecto con el primero que exista y, si no existe ninguno, se queda con
/// su propio criterio en vez de inventar un rango.
/// </summary>
public sealed record CoberturaMeses(RangoMeses? Facturacion, RangoMeses? Evolucion, RangoMeses? Casos);

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

    /// <summary>Reemplaza el insumo de evolución de consumo por recurso (entrega 5 del informe de
    /// valor): mismo criterio de "insumo vivo" que facturación y casos.</summary>
    Task<int> ReplaceEvolucionAsync(
        int clientId, string fileName, string? user, ParseResult<EvolucionRow> parsed, CancellationToken ct);

    Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct);

    Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct);

    /// <summary>
    /// El rango de meses que cubren los insumos cargados del cliente. Lo pide la pantalla de
    /// insumos junto con el estado, así que es un MIN/MAX agregado por tabla y NUNCA lee filas:
    /// proponer el período del informe no puede costar lo que cuesta calcularlo.
    /// </summary>
    Task<CoberturaMeses> GetCoberturaMesesAsync(int clientId, CancellationToken ct);

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

    /// <summary>Las filas de evolución de consumo ya persistidas de un cliente (insumo de la
    /// entrega 6, el ensamblador del informe). Ordenadas por período y recurso, no por
    /// <c>row_id</c>: ver el comentario de la implementación.</summary>
    Task<IReadOnlyList<EvolucionRow>> GetEvolucionAsync(int clientId, CancellationToken ct);

    /// <summary>
    /// Archiva una entrega generada (F4 de la entrega 3) y devuelve su <c>entrega_id</c>.
    /// <b>Acumula, no reemplaza</b>: reemitir el mismo período es legítimo y el historial importa,
    /// así que no hay unicidad por período y esta llamada nunca borra una fila anterior.
    /// </summary>
    Task<int> RegistrarEntregaAsync(EntregaNueva entrega, CancellationToken ct);

    /// <summary>Las entregas de un cliente, de la más reciente a la más vieja (mismo orden que el
    /// índice de la tabla). Sin la foto de reservas ni el resto de la trazabilidad: la tabla
    /// paginada no las necesita y son las dos columnas grandes.</summary>
    Task<IReadOnlyList<EntregaResumen>> GetEntregasAsync(int clientId, CancellationToken ct);

    /// <summary>
    /// Una entrega archivada completa, con la foto de reservas y el contexto de cálculo.
    /// <c>null</c> si no existe <b>o si es de otro cliente</b>: el filtro por <paramref name="clientId"/>
    /// va en el <c>WHERE</c> y no en una comparación posterior, para que un id de entrega adivinado
    /// no devuelva el artefacto de otro cliente ni siquiera por un instante.
    /// </summary>
    Task<EntregaArchivada?> GetEntregaAsync(int clientId, int entregaId, CancellationToken ct);

    // ── El registro manual de acciones ejecutadas (entrega 8, pieza B) ──

    /// <summary>Las acciones manuales ACTIVAS del cliente, ordenadas por mes de ejecución y id:
    /// alimentan la quinta fuente del registro de lo ejecutado y la pantalla de gestión.</summary>
    Task<IReadOnlyList<AccionManualRow>> GetAccionesManualesAsync(int clientId, CancellationToken ct);

    Task<int> InsertAccionManualAsync(int clientId, AccionManualNueva accion, string? user, CancellationToken ct);

    /// <summary><c>false</c> si la acción no existe, es de otro cliente o ya está inactiva — el
    /// filtro por cliente va en el WHERE, mismo criterio que <see cref="GetEntregaAsync"/>.</summary>
    Task<bool> UpdateAccionManualAsync(int clientId, int accionId, AccionManualNueva accion, CancellationToken ct);

    /// <summary>Borrado lógico (<c>activo = 0</c>): una acción registrada respalda informes ya
    /// emitidos, así que la fila nunca se elimina de verdad.</summary>
    Task<bool> DeleteAccionManualAsync(int clientId, int accionId, CancellationToken ct);
}
