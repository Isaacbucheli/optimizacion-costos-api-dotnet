using System.Text.Json;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Entrega;

/// <summary>
/// Una entrega recién generada, tal como se archiva en <c>informe_valor_entrega</c> (F4).
///
/// <para><b>El criterio de qué va acá:</b> todo lo que haga falta para que el mismo informe,
/// reemitido, dé el mismo resultado. Si un dato entra al cálculo y viene de una fuente que cambia
/// sola, o se guarda o el archivo miente. Por eso el record lleva más campos que los que nombra el
/// spec: la foto de reservas (heredada de la entrega 2d), la corrida de Revisión de accesos, la
/// bandera de seguridad gestionada por fuera y las tres corridas de ingesta.</para>
///
/// <para><b>Lo que este record NO puede garantizar, y hay que saberlo.</b> Los insumos son vivos:
/// cada carga borra la anterior. Las filas de facturación, casos y RBAC de una entrega no se pueden
/// restaurar desde acá. Lo que sí queda es la identidad de la carga que las produjo
/// (<see cref="FacturacionIngestaId"/> y sus dos hermanas), suficiente para DETECTAR que el insumo
/// ya no es el mismo y decirlo, en vez de reemitir en silencio contra otro archivo. El artefacto
/// archivado en el blob, en cambio, sí es el informe exacto que se entregó: lleva el modelo
/// completo adentro.</para>
/// </summary>
public sealed record EntregaNueva(
    int ClientId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly Corte,
    /// <summary>Tri-estado del spec §12.3.3, tal cual llegó en
    /// <see cref="ContextoInformeValor.MesesParcialesForzados"/>: <c>null</c> = heurística
    /// automática, lista vacía = el consultor declaró "ninguno", lista con elementos = ésos. Los
    /// tres se guardan distinto y los tres reemiten distinto.</summary>
    IReadOnlyList<string>? MesesParcialesForzados,
    VarianteInforme Variante,
    IReadOnlyList<BloqueEconomico> BloquesPublicados,
    string? RbacOrigen,
    DateTime? RbacCorridaFecha,
    bool SeguridadGestionadaExternamente,
    int? FacturacionIngestaId,
    int? CasosIngestaId,
    int? RbacIngestaId,
    /// <summary>La corrida de evolución (entrega 5) que alimentó la conciliación de la Tarea 8 y el
    /// balde de reservas facturadas de la Tarea 3 (entrega 6). Mismo criterio que sus tres hermanas:
    /// el insumo es vivo, así que esto es lo único que después permite detectar que ya no es el
    /// mismo. <c>null</c> cuando el insumo no estaba cargado al generar.</summary>
    int? EvolucionIngestaId,
    /// <summary>La foto de reservas con la que se generó. <c>null</c> significa "esta entrega se
    /// generó sin capturarla", que NO es lo mismo que una foto con <c>Medido=false</c> ("se intentó
    /// y no se pudo", con su motivo adentro).</summary>
    FotoReservas? FotoReservas,
    string? PlantillaVersion,
    /// <summary>El contenedor de Blob Storage donde quedó el artefacto. Se guarda junto al nombre
    /// —igual que hace el informe de gestión mensual con <c>client_monthly_report.storage_container</c>
    /// y el Excel de costos con <c>analysis_files</c>— y no se deduce de la configuración al
    /// descargar: el contenedor sale de un valor de entorno y puede cambiar, y con eso todas las
    /// entregas archivadas dejarían de encontrarse sin que nada lo explique.</summary>
    string BlobContainer,
    string BlobName,
    int BlobSizeBytes,
    string FileName,
    string? SummaryJson,
    string? GeneratedBy);

/// <summary>Una fila de la tabla de entregas (pestaña "Entregas"). Sin la foto ni el resto de la
/// trazabilidad: eso es <see cref="EntregaArchivada"/>, y solo se paga cuando alguien la pide.</summary>
public sealed record EntregaResumen(
    int EntregaId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly Corte,
    string Variante,
    IReadOnlyList<string> BloquesPublicados,
    string? RbacOrigen,
    string FileName,
    int BlobSizeBytes,
    string? GeneratedBy,
    DateTime GeneratedAt);

/// <summary>
/// Una entrega archivada completa: el resumen que ve la tabla más todo lo que hace falta para
/// descargarla o reemitirla. <see cref="MesesParcialesForzados"/> conserva el tri-estado y
/// <see cref="FotoReservas"/> es la foto exacta con la que se calculó.
/// </summary>
public sealed record EntregaArchivada(
    EntregaResumen Resumen,
    /// <summary>El contenedor con el que se subió el artefacto (ver
    /// <see cref="EntregaNueva.BlobContainer"/>). <c>null</c> solo en una fila escrita por una
    /// versión de este módulo anterior a la columna: la descarga cae ahí al contenedor configurado
    /// hoy, que es la única suposición razonable, y lo registra en el log en vez de hacerlo en
    /// silencio.</summary>
    string? BlobContainer,
    string BlobName,
    IReadOnlyList<string>? MesesParcialesForzados,
    DateTime? RbacCorridaFecha,
    bool SeguridadGestionadaExternamente,
    int? FacturacionIngestaId,
    int? CasosIngestaId,
    int? RbacIngestaId,
    /// <summary>Ver <see cref="EntregaNueva.EvolucionIngestaId"/>.</summary>
    int? EvolucionIngestaId,
    FotoReservas? FotoReservas,
    string? PlantillaVersion,
    string? SummaryJson);

/// <summary>
/// Serialización de la foto de reservas para <c>informe_valor_entrega.foto_reservas_json</c>.
///
/// <para>Usa <see cref="InformeValorJsonOptions.Instance"/> (política de nombres en <c>null</c>) en
/// las DOS direcciones: la ida y la vuelta tienen que mirar los mismos nombres de propiedad, y con
/// la política global del repo (snake_case) la vuelta devolvería un record con todos los campos en
/// su valor por defecto —una foto <c>Medido=false</c> sin motivo, indistinguible de una lectura
/// fallida— sin lanzar ninguna excepción.</para>
///
/// <para><see cref="FotoReservas.Errores"/> es <c>IReadOnlyList&lt;object&gt;</c>: al releer, cada
/// elemento vuelve como <c>JsonElement</c>. No se usa en ningún cálculo (solo se muestra), así que
/// alcanza con que sobreviva textualmente.</para>
/// </summary>
public static class FotoReservasJson
{
    public static string? Serializar(FotoReservas? foto) =>
        foto is null ? null : JsonSerializer.Serialize(foto, InformeValorJsonOptions.Instance);

    /// <summary>
    /// <c>null</c> cuando la columna está vacía (entrega anterior a la foto). Un JSON corrupto o
    /// truncado NO se traduce a <c>null</c> en silencio: se propaga la excepción, porque "no se
    /// guardó" y "se guardó y no se puede leer" llevan a decisiones opuestas y confundirlos es el
    /// mismo cero ambiguo de siempre.
    /// </summary>
    public static FotoReservas? Deserializar(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<FotoReservas>(json, InformeValorJsonOptions.Instance);
}

/// <summary>
/// Serialización del tri-estado de meses parciales para
/// <c>informe_valor_entrega.meses_parciales</c>. Existe como par de funciones —y no como dos
/// llamadas sueltas a <c>JsonSerializer</c> repartidas por el store— porque el error fácil acá es
/// colapsar la lista vacía a <c>NULL</c>: quedaría guardado "sin declaración" donde el consultor
/// declaró "ningún mes parcial", y al reemitir se aplicaría la heurística automática que él
/// justamente había desactivado.
/// </summary>
public static class MesesParcialesJson
{
    public static string? Serializar(IReadOnlyList<string>? meses) =>
        meses is null ? null : JsonSerializer.Serialize(meses, InformeValorJsonOptions.Instance);

    public static IReadOnlyList<string>? Deserializar(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<List<string>>(json, InformeValorJsonOptions.Instance) ?? [];
}
