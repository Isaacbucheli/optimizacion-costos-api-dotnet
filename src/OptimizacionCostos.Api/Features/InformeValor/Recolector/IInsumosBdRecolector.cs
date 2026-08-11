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
///
/// <para><b><see cref="RbacOrigen"/></b> (el cable de la condicional de RBAC): de qué fuente
/// salieron las filas de <see cref="Rbac"/> en esta lectura puntual, <see cref="OrigenBase"/> o
/// <see cref="OrigenArchivo"/> — <c>null</c> cuando ninguna de las dos fuentes tiene nada que
/// ofrecer (sin corrida de Revisión de accesos y sin archivo cargado). Lo resuelve
/// <see cref="Recolector.SqlInsumosBdRecolector.ResolverRbac"/> con la precedencia "gana la base"
/// del spec (sección "La condicional de RBAC"): si <see cref="EstadoRbac"/> ya es
/// <see cref="DisponibilidadRbac.Completo"/> el origen siempre es la base; si no, gana el archivo
/// cuando trae filas. El spec de la entrega 3 persiste el mismo valor en la bitácora de cada
/// informe generado (<c>informe_valor_entrega.rbac_origen</c>); acá solo se declara — un consultor
/// que no puede saber qué fuente se usó no puede explicar una cifra del informe. La fecha de la
/// corrida, cuando el origen es la base, ya viaja en <see cref="EstadoRbacResultado.FechaCorrida"/>
/// (no hace falta un segundo campo: esa fecha se resuelve en cuanto hay una corrida, incluso
/// cuando <see cref="EstadoRbac"/> no es <see cref="DisponibilidadRbac.Completo"/>).</para>
/// </summary>
public sealed record InsumosBd(
    IReadOnlyList<AdvisorFila> Advisor,
    IReadOnlyList<MatrizFila> Matriz,
    IReadOnlyList<RbacFila> Rbac,
    IReadOnlyList<RetiroFila> Retiros,
    EstadoRbacResultado EstadoRbac,
    bool SeguridadGestionadaExternamente,
    string? SeguridadGestionadaNota,
    DateTime LeidoEn,
    string? RbacOrigen = null)
{
    /// <summary><see cref="RbacOrigen"/> cuando <see cref="Rbac"/> salió de Revisión de accesos.</summary>
    public const string OrigenBase = "base";

    /// <summary><see cref="RbacOrigen"/> cuando <see cref="Rbac"/> salió del Excel de respaldo.</summary>
    public const string OrigenArchivo = "archivo";
}

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

    /// <summary>
    /// Solo <see cref="EstadoRbacResultado"/> (la síntesis de <see cref="EstadoRbac.Resolver"/>
    /// sobre la corrida de Revisión de accesos): sin Advisor, sin Matriz, sin Retiros, y sin el
    /// schema-ensure de WAF/Boletín que esos tres necesitan. <c>InformeValorController.Subir</c>
    /// (kind <c>rbac</c>) solo necesita esto para decidir la precedencia "gana la base" antes de
    /// guardar el archivo que subió el consultor: <see cref="LeerAsync"/> le hacía pagar el costo
    /// completo de los cuatro insumos por una comprobación que usa apenas uno de ellos, en un App
    /// Service B1 de 1 core y 1,75 GB compartido con el resto de la API.
    /// </summary>
    Task<EstadoRbacResultado> LeerEstadoRbacAsync(int clientId, CancellationToken ct = default);
}
