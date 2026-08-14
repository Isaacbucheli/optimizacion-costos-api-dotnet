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
    string? RbacOrigen = null,
    /// <summary>Hallazgos RESUELTOS de la matriz WAF (<see cref="HallazgoResueltoRecolector"/>,
    /// tarea 2 de la entrega 2d, E3): universo disjunto de <see cref="Advisor"/>/<see cref="Matriz"/>,
    /// que solo miran hallazgos ACTIVOS. Es el insumo del balde 2 de la atribución
    /// (<c>AtribucionCalculador</c>), un bloque nuevo que ningún consumidor de la entrega 2b leía
    /// todavía. Default <c>null</c> (tratado como vacío por quien lo lee) por el mismo motivo que
    /// <see cref="RbacOrigen"/>: para no romper, con un parámetro nuevo requerido, a cada test de
    /// este módulo que ya construye <see cref="InsumosBd"/> a mano.</summary>
    IReadOnlyList<HallazgoResueltoFila>? HallazgosResueltos = null,
    /// <summary>Última corrida del sync del Boletín (<see cref="CorridaBoletin"/>), o <c>null</c>
    /// cuando el módulo nunca sincronizó a este cliente. Es el estado del INSUMO de
    /// <see cref="Retiros"/>, no un retiro más: sin él, <see cref="Retiros"/> vacío no distingue
    /// "Azure no anunció nada sobre este parque" de "nadie fue a buscarlo", y el informe publicaba
    /// "0 retiros" como un hecho del negocio. Ver <see cref="RetirosRecolector.SqlUltimaCorrida"/>.
    ///
    /// <para>Default <c>null</c> por el mismo motivo que los dos parámetros de arriba (no romper los
    /// tests que construyen este record a mano). Ojo con la ambigüedad de ese default: <c>null</c>
    /// significa a la vez "el Boletín nunca corrió" y "nadie preguntó", así que
    /// <c>PosturaCalculador</c> resuelve el empate por el otro lado — con retiros presentes, alguien
    /// los buscó, y eso ya alcanza para declararlo medido.</para></summary>
    CorridaBoletin? CorridaBoletin = null,
    /// <summary>Score del pilar de costos de Advisor hoy más su serie mensual
    /// (<see cref="OpexRecolector"/>), la fuente de la tarjeta "Opex" del resumen (entrega 6/7).
    /// Default <c>null</c> por el mismo motivo que los parámetros de arriba: no romper los tests
    /// que construyen este record a mano.</summary>
    OpexScore? Opex = null,
    /// <summary>Hitos de la bitácora del tracking de la matriz WAF (<see cref="CronologiaRecolector"/>),
    /// la fuente cruda de la cronología del informe (entrega 6/7): la entrega que dibuja la línea de
    /// tiempo decide qué campos de <see cref="HitoFila.Campo"/> se traducen a un hito legible y con
    /// qué redacción, este recolector no filtra por campo. Default <c>null</c> por el mismo motivo
    /// que los parámetros de arriba: no romper los tests que construyen este record a mano.</summary>
    IReadOnlyList<HitoFila>? Hitos = null)
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

    /// <summary>
    /// <see cref="EstadoRbacResultado"/> más de dónde saldrían las filas de RBAC que alimentan el
    /// informe (<see cref="InsumosBd.RbacOrigen"/>: <see cref="InsumosBd.OrigenBase"/>,
    /// <see cref="InsumosBd.OrigenArchivo"/> o <c>null</c>), por el mismo camino liviano de
    /// <see cref="LeerEstadoRbacAsync"/> -- sin Advisor, sin Matriz, sin Retiros y sin el
    /// schema-ensure de WAF/Boletín. La pantalla de insumos (<c>InformeValorController.Estado</c>)
    /// necesita el origen para explicar de dónde sale el insumo de RBAC, no solo si la base
    /// alcanza; antes de este método la única forma de conseguirlo era <see cref="LeerAsync"/>
    /// completo, que de paso paga Advisor/Matriz/Retiros por una pantalla que no los usa.
    ///
    /// <para>El resultado tiene que ser IDÉNTICO al bloque <c>estado_rbac</c> de
    /// <c>/insumos-bd</c> para el mismo cliente en el mismo instante: la implementación reusa
    /// <see cref="SqlInsumosBdRecolector.ResolverRbac"/>, la misma función pura del camino pesado,
    /// sobre las mismas tres piezas (estado base, filas de la base, filas del archivo) -- nunca
    /// recalcula el criterio por su cuenta. La única consulta que este método paga y
    /// <see cref="LeerEstadoRbacAsync"/> no es la del archivo de respaldo
    /// (<see cref="IInformeValorStore.GetRbacAsync"/>), y solo cuando la base no alcanza por sí
    /// sola -- igual que <see cref="LeerAsync"/> -- así que el caso más común (base Completa) no
    /// paga ninguna consulta de más.</para>
    /// </summary>
    Task<(EstadoRbacResultado Estado, string? Origen)> LeerEstadoRbacConOrigenAsync(
        int clientId, CancellationToken ct = default);

    /// <summary>
    /// Solo <see cref="InsumosBd.HallazgosResueltos"/>, por el mismo motivo que
    /// <see cref="LeerEstadoRbacAsync"/> existe: <c>InformeValorController.VariacionConsumo</c> es el
    /// unico consumidor del bloque de variacion del consumo y de todo <see cref="InsumosBd"/> usa este
    /// campo y nada mas (<c>AtribucionCalculador</c> es el unico que lo lee). Con
    /// <see cref="LeerAsync"/> esa segunda llamada de la vista previa volvia a pagar Advisor, Matriz,
    /// Retiros, la ultima corrida de Revision de accesos con su snapshot completo y, si la base no
    /// alcanza, el Excel de RBAC del store — todo eso por segunda vez en la misma vista previa, en un
    /// App Service B1 de 1 core.
    ///
    /// <para>Lo que igual se paga, porque el <c>WHERE</c> de <see cref="HallazgoResueltoRecolector"/>
    /// lo necesita: la lista de suscripciones administradas, la bandera de seguridad gestionada
    /// externamente y el schema-ensure de WAF. Fuera quedan las tres lecturas grandes y la corrida de
    /// accesos.</para>
    /// </summary>
    Task<IReadOnlyList<HallazgoResueltoFila>> LeerHallazgosResueltosAsync(
        int clientId, CancellationToken ct = default);

    /// <summary>
    /// El registro del barrido de optimización resuelto (<see cref="BarridoResueltoRecolector"/>,
    /// entrega 5), leído sin decidir la doble puerta del spec: el llamador (entrega 6, el controller,
    /// donde vive el contexto de usuario) verifica el permiso del módulo Optimization Y
    /// <c>OptimizationService.AccessAllowed(email)</c> ANTES de llamar a esto, y usa
    /// <see cref="RegistroBarrido.NoAutorizado"/> cuando no pasa — este método nunca se llama en ese
    /// caso.
    ///
    /// <para>El llamador también debe correr <c>OptimizationService.EnsureSchemaAsync</c> antes de
    /// invocar este método: las tablas del barrido no las asegura <see cref="SqlInsumosBdRecolector"/>
    /// (no forma parte del schema-ensure de <see cref="LeerAsync"/> a propósito, el barrido no es un
    /// insumo de <see cref="InsumosBd"/>).</para>
    /// </summary>
    Task<RegistroBarrido> LeerBarridoResueltoAsync(int clientId, CancellationToken ct = default);
}
