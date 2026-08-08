namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Los cuatro insumos de base de un cliente para el informe de valor, ya leídos, más el estado de
/// RBAC resuelto (<see cref="EstadoRbac"/>), la gestión externa de seguridad y el instante de la
/// lectura. Nada calcula todavía (eso es la entrega 2b): esta es la foto cruda que consumen tanto
/// el endpoint de diagnóstico como, más adelante, la calculadora del informe. Por eso
/// <see cref="Advisor"/>/<see cref="Matriz"/>/<see cref="Rbac"/>/<see cref="Retiros"/> llevan las
/// filas completas (con nombres de recurso, suscripción e identidad): quien decide qué de todo esto
/// se expone hacia afuera es cada consumidor, no este record. El endpoint de diagnóstico, por
/// ejemplo, solo publica conteos.
///
/// <para><b><see cref="SeguridadGestionadaExternamente"/>/<see cref="SeguridadGestionadaNota"/>
/// (IMPORTANTE 2 de la re-revisión):</b> cuando el pilar de Seguridad sale vacío en
/// <see cref="Matriz"/>/<see cref="Advisor"/>, puede ser porque el cliente no tiene hallazgos de
/// seguridad, o porque los gestiona aparte (Gestión de Vulnerabilidades) y las dos consultas ya los
/// excluyeron (ver <see cref="MatrizRecolector"/>/<see cref="AdvisorRecolector"/>). Sin esta
/// bandera el resultado no distinguía los dos casos: un pilar en cero se veía igual en ambos, y la
/// calculadora de la entrega siguiente no tenía cómo explicar por qué está vacío. Calcado de la
/// tarjeta de Seguridad de <c>WafController.Sections</c>: la nota va resuelta al texto por defecto
/// cuando el cliente gestiona aparte pero no escribió una propia, y es <c>null</c> cuando no
/// gestiona aparte (no hay nada que explicar).</para>
/// </summary>
public sealed record InsumosBd(
    IReadOnlyList<AdvisorFila> Advisor,
    IReadOnlyList<MatrizFila> Matriz,
    IReadOnlyList<RbacFila> Rbac,
    IReadOnlyList<RetiroFila> Retiros,
    EstadoRbacResultado EstadoRbac,
    bool SeguridadGestionadaExternamente,
    string? SeguridadGestionadaNota,
    DateTime LeidoEn);

/// <summary>
/// Ensamblador de los cuatro recolectores del informe de valor (Advisor, Matriz, RBAC y Retiros)
/// más la resolución del estado de RBAC. No calcula nada: junta lo que cada recolector ya sabe
/// leer y decide, con un único punto de entrada, si el cliente tiene datos suficientes para
/// generar el informe (entrega 2b) o para diagnosticar de dónde salió cada cifra (el endpoint de
/// esta entrega).
/// </summary>
public interface IInsumosBdRecolector
{
    Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default);
}
