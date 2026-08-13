using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Implementación de <see cref="IInsumosBdRecolector"/>. Abre UNA sola conexión y la reusa para
/// Advisor, Matriz y Retiros (los tres reciben una <see cref="SqlConnection"/> ya abierta y, a
/// propósito, no aseguran su propio schema: ver el comentario de clase de cada uno). RBAC no la
/// usa: pasa por <see cref="IAccessReviewStore"/>, que administra su propia conexión. Opex
/// (<see cref="OpexRecolector"/>) tampoco: pasa por <see cref="IAdvisorScoreStore"/>, mismo motivo,
/// y se lee antes de abrir la conexión compartida para no mantenerla abierta de más.
///
/// <para>La corrida de accesos se lee UNA sola vez (<c>GetLatestFinishedRunAsync</c> +
/// <c>GetSnapshotAsync</c>) y el mismo snapshot alimenta dos cosas: <see cref="EstadoRbac.Resolver"/>
/// (que necesita el snapshot completo, con las credenciales) y <see cref="RbacRecolector.Mapear"/>
/// (que solo proyecta las asignaciones ya deduplicadas). Llamar en cambio a
/// <see cref="RbacRecolector.LeerAsync"/> habría repetido las mismas dos consultas contra
/// <see cref="IAccessReviewStore"/> sin ganar nada: ese método hace exactamente este mismo
/// fetch-y-mapea, pero por separado.</para>
///
/// <para>Dos insumos se resuelven UNA sola vez acá y se comparten con Advisor y Matriz, en vez de
/// que cada recolector los vuelva a preguntar: la lista de suscripciones administradas
/// (<see cref="SqlSuscripcionesAdministradas"/>) y la bandera + nota de seguridad gestionada
/// externamente (<see cref="SeguridadGestionadaExternamenteAsync"/>). Los dos son universo de
/// datos, no parámetros de una pantalla: sin ellos el informe podía mostrar hallazgos de una
/// suscripción que el cliente dejó de administrar, o del pilar de Seguridad cuando el cliente pidió
/// no verlo sin decir por qué (las tres salidas del producto que sí lo respetan: pantalla WAF,
/// export a Excel e informe mensual).
/// </para>
///
/// <para><b>El cable de la condicional de RBAC (<see cref="ResolverRbac"/>).</b> <c>informeValorStore</c>
/// es el mismo <see cref="IInformeValorStore"/> que ya persiste el Excel de respaldo que sube el
/// consultor: hasta esta tarea nadie llamaba a <see cref="IInformeValorStore.GetRbacAsync"/>, así
/// que un archivo guardado nunca alimentaba <see cref="InsumosBd.Rbac"/> — el informe seguía viendo
/// el insumo vacío aunque la carga hubiera funcionado. <see cref="ResolverRbac"/> solo pide esas
/// filas cuando <see cref="EstadoRbac.Resolver"/> ya no es <see cref="DisponibilidadRbac.Completo"/>:
/// si la base alcanza por sí sola no hace falta ni preguntarle al store.</para>
/// </summary>
public sealed class SqlInsumosBdRecolector(
    ISqlConnectionFactory factory, IAccessReviewStore accessReviewStore, IInformeValorStore informeValorStore,
    IAdvisorScoreStore advisorScoreStore)
    : IInsumosBdRecolector
{
    /// <summary>
    /// Caché por proceso (mismo patrón <c>_schemaEnsured</c> que
    /// <c>SqlAccessReviewStore</c>/<c>AccessReviewDecisionStore</c>/<c>SqlFinOpsDataStore</c>), pero
    /// acotada a este módulo: <c>WafSchema.EnsureWafSchemaAsync</c> son ~19 sentencias DDL
    /// idempotentes y <c>BoletinService.EnsureSchemaAsync</c> varias más, y <c>/insumos-bd</c> es un
    /// endpoint de solo lectura (no escribe nada) que las repetía en cada request contra un App
    /// Service B1 compartido (1 core, 1.75 GB). No se agrega el guard adentro de
    /// <c>WafSchema</c>/<c>BoletinService</c> (eso cambiaría su costo para TODOS sus llamadores, no
    /// solo este) ni se toca su comportamiento: el schema sigue siendo idempotente, solo se deja de
    /// re-verificar de más para esta lectura en particular.
    /// </summary>
    private static bool _schemaEnsured;

    /// <summary>
    /// Predicado canónico de suscripciones administradas de Optimization/WAF/Inventory/Boletín/
    /// Revisión de accesos (ver <c>BoletinService.ManagedSubscriptionsAsync</c>,
    /// <c>AccessReviewSyncService.CredentialUnitsAsync</c>, <c>SqlAdvisorScoreStore</c>): el
    /// <c>INNER JOIN</c> a <c>client_azure_credentials</c> con <c>c.is_active = 1</c> es parte del
    /// predicado, no un detalle opcional — antes esta consulta lo omitía pese a que el comentario
    /// decía "mismo predicado". <c>is_managed</c> lo decide el usuario, nunca el sync
    /// (<c>COALESCE(is_managed,1)=1</c> trata NULL como "sí administrada").
    ///
    /// <para>Sin el JOIN, una credencial desactivada (revocada, rotada) seguía contando sus
    /// suscripciones como administradas: <see cref="EstadoRbac.Resolver"/> pedía "ejecutá la
    /// revisión de accesos" para un cliente que en realidad no tiene nada que sincronizar, y la
    /// corrida que el consultor disparara iba a fallar siempre (mismo predicado con el JOIN en
    /// <c>AccessReviewSyncService.CredentialUnitsAsync</c>, que es quien decide qué se sincroniza).
    /// </para>
    ///
    /// <para>Devuelve los ids, no un conteo: <see cref="AdvisorRecolector.LeerAsync"/> y
    /// <see cref="MatrizRecolector.LeerAsync"/> (Importante 2 de la revisión de rama) necesitan la
    /// lista completa para filtrar sus propias consultas vía <see cref="Waf.WafSubscriptionFilter"/>
    /// — sin ese filtro, Advisor y Matriz seguían trayendo hallazgos de una suscripción que el
    /// cliente dejó de administrar, y el conteo por suscripción del informe no cuadraba con el del
    /// Boletín (que sí filtra) para el mismo cliente. Una sola consulta sirve para eso y para el
    /// booleano que necesita <see cref="EstadoRbac.Resolver"/> (<c>Count > 0</c>), así que no hace
    /// falta correrla dos veces.</para>
    /// </summary>
    internal const string SqlSuscripcionesAdministradas = """
        SELECT s.subscription_id
        FROM dbo.client_azure_subscriptions s
        INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
        WHERE s.client_id = @clientId AND s.is_active = 1
          AND COALESCE(s.is_managed, 1) = 1 AND c.is_active = 1
        """;

    public async Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default)
    {
        // Opex no toca la conexión compartida de abajo: IAdvisorScoreStore administra la suya
        // propia (mismo patrón que accessReviewStore/informeValorStore). Se lee antes de abrir la
        // conexión de Advisor/Matriz/Retiros para no mantenerla abierta de más mientras se hace
        // este IO independiente.
        var opex = await OpexRecolector.LeerAsync(advisorScoreStore, clientId, ct);

        await using var conn = await factory.OpenAsync(ct);

        // Advisor y Matriz dependen del schema WAF; Retiros, del de Boletín. Ninguno de los tres
        // recolectores lo asegura por sí mismo (ver sus comentarios de clase): centralizarlo acá
        // en vez de repetirlo en cada uno evita 3 chequeos de DDL idempotente por request cuando
        // 2 alcanzan (WAF sirve para Advisor y Matriz a la vez). Y correrlos una sola vez por
        // proceso (ver _schemaEnsured) evita repetir esas ~19+ sentencias en cada request de un
        // endpoint que no escribe nada.
        await AsegurarSchemaAsync(conn, ct);

        var administradas = await SuscripcionesAdministradasAsync(conn, clientId, ct);
        var (seguridadGestionadaExternamente, seguridadGestionadaNota) =
            await SeguridadGestionadaExternamenteAsync(conn, clientId, ct);

        var advisor = await AdvisorRecolector.LeerAsync(conn, clientId, administradas, seguridadGestionadaExternamente, ct);
        var matriz = await MatrizRecolector.LeerAsync(conn, clientId, administradas, seguridadGestionadaExternamente, ct);
        var retiros = await RetirosRecolector.LeerAsync(conn, clientId, ct);
        // El estado del insumo de retiros viaja con el insumo: "0 retiros" y "el Boletín nunca corrió
        // para este cliente" son dos hechos distintos y hasta acá salían iguales. Misma conexión y
        // mismo schema de Boletín ya asegurado arriba, así que cuesta una consulta de una fila.
        var corridaBoletin = await RetirosRecolector.LeerUltimaCorridaAsync(conn, clientId, ct);
        // Tarea 2 de la entrega 2d (E3): mismo patron que Advisor/Matriz (misma conexion, mismo
        // schema WAF ya asegurado arriba, mismo filtro de suscripciones administradas).
        var hallazgosResueltos = await HallazgoResueltoRecolector.LeerAsync(
            conn, clientId, administradas, seguridadGestionadaExternamente, ct);
        // Tarea 6: misma conexión y mismo schema WAF ya asegurado arriba. No depende de
        // suscripciones administradas (waf_tracking_history no tiene columna de suscripción).
        var hitos = await CronologiaRecolector.LeerAsync(conn, clientId, seguridadGestionadaExternamente, ct);

        var run = await accessReviewStore.GetLatestFinishedRunAsync(clientId, ct);
        var snapshot = run is null ? null : await accessReviewStore.GetSnapshotAsync(run.RunId, ct);
        var estadoBase = EstadoRbac.Resolver(snapshot, administradas.Count > 0);
        var rbacBase = snapshot is null ? [] : RbacRecolector.Mapear(snapshot);

        // El archivo de respaldo solo hace falta cuando la base no alcanza por sí sola: si ya es
        // Completo, gana la base sin gastar esta consulta (mismo cuidado de costo que
        // LeerEstadoRbacAsync del lado del controller).
        var rbacArchivo = estadoBase.Disponibilidad == DisponibilidadRbac.Completo
            ? []
            : await informeValorStore.GetRbacAsync(clientId, ct);

        var (rbac, ejesRbac, rbacOrigen) = ResolverRbac(estadoBase, rbacBase, rbacArchivo);

        return new InsumosBd(
            advisor, matriz, rbac, retiros, estadoBase with { Ejes = ejesRbac },
            seguridadGestionadaExternamente, seguridadGestionadaNota, DateTime.UtcNow,
            RbacOrigen: rbacOrigen, HallazgosResueltos: hallazgosResueltos,
            CorridaBoletin: corridaBoletin, Opex: opex, Hitos: hitos);
    }

    /// <summary>
    /// <see cref="IInsumosBdRecolector.LeerHallazgosResueltosAsync"/>: el camino angosto del balde 2,
    /// para la segunda llamada de la vista previa (<c>/preview/variacion-consumo</c>). Paga lo que el
    /// <c>WHERE</c> de <see cref="HallazgoResueltoRecolector"/> necesita —suscripciones administradas
    /// y bandera de seguridad— y nada mas.
    ///
    /// <para>El schema-ensure se hace completo (WAF y Boletin), no solo el de WAF que esta consulta
    /// necesita: <see cref="_schemaEnsured"/> es UNA bandera por proceso para los dos. Asegurar solo
    /// WAF y darla por buena dejaria a un <see cref="LeerAsync"/> posterior sin el schema de Boletin
    /// que si necesita <see cref="RetirosRecolector"/>. Es DDL idempotente y corre una sola vez por
    /// proceso, asi que no hay nada que ganar partiendo la bandera en dos.</para>
    /// </summary>
    public async Task<IReadOnlyList<HallazgoResueltoFila>> LeerHallazgosResueltosAsync(
        int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        await AsegurarSchemaAsync(conn, ct);

        var administradas = await SuscripcionesAdministradasAsync(conn, clientId, ct);
        var (seguridadGestionadaExternamente, _) = await SeguridadGestionadaExternamenteAsync(conn, clientId, ct);

        return await HallazgoResueltoRecolector.LeerAsync(
            conn, clientId, administradas, seguridadGestionadaExternamente, ct);
    }

    /// <summary>Ver <see cref="_schemaEnsured"/>: DDL idempotente de WAF y Boletin, una sola vez por
    /// proceso. Compartido por <see cref="LeerAsync"/> y <see cref="LeerHallazgosResueltosAsync"/>,
    /// los dos caminos que consultan tablas de esos dos modulos.</summary>
    private static async Task AsegurarSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        if (_schemaEnsured) return;

        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await BoletinService.EnsureSchemaAsync(conn, ct);
        _schemaEnsured = true;
    }

    /// <summary>
    /// Solo <see cref="EstadoRbacResultado"/> (ver el comentario de <see cref="IInsumosBdRecolector.LeerEstadoRbacAsync"/>
    /// para el motivo): abre su propia conexión liviana, sin el schema-ensure de WAF/Boletín ni las
    /// tres lecturas de Advisor/Matriz/Retiros que <see cref="LeerAsync"/> paga completas. Los dos
    /// ejes que devuelve describen la corrida de base tal cual — a diferencia de <see cref="LeerAsync"/>,
    /// que los reemplaza por los del archivo cuando ese es el origen efectivo: acá no hay ningún
    /// archivo de por medio, solo la pregunta "¿la base alcanza?" que hace <c>InformeValorController.Subir</c>
    /// antes de decidir si guarda el Excel que subió el consultor.
    /// </summary>
    public async Task<EstadoRbacResultado> LeerEstadoRbacAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var administradas = await SuscripcionesAdministradasAsync(conn, clientId, ct);
        var run = await accessReviewStore.GetLatestFinishedRunAsync(clientId, ct);
        var snapshot = run is null ? null : await accessReviewStore.GetSnapshotAsync(run.RunId, ct);
        return EstadoRbac.Resolver(snapshot, administradas.Count > 0);
    }

    /// <summary>
    /// <see cref="IInsumosBdRecolector.LeerEstadoRbacConOrigenAsync"/>: mismo camino liviano de
    /// <see cref="LeerEstadoRbacAsync"/> arriba (conexión propia, sin schema-ensure de WAF/Boletín
    /// ni Advisor/Matriz/Retiros), más el origen. <c>rbacBase</c> sale gratis del snapshot que ya
    /// hay que leer para <see cref="EstadoRbac.Resolver"/> (proyección en memoria vía
    /// <see cref="RbacRecolector.Mapear"/>, sin consulta nueva); <c>rbacArchivo</c> solo se pide
    /// cuando la base no alcanza por sí sola, igual que <see cref="LeerAsync"/> arriba -- así que
    /// el caso más común (Completo) sigue sin pagar ninguna consulta de más sobre lo que ya hacía
    /// <see cref="LeerEstadoRbacAsync"/>. <see cref="ResolverRbac"/> es la misma función pura del
    /// camino pesado: este método no reimplementa el criterio de "gana la base", lo reusa.
    /// </summary>
    public async Task<(EstadoRbacResultado Estado, string? Origen)> LeerEstadoRbacConOrigenAsync(
        int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var administradas = await SuscripcionesAdministradasAsync(conn, clientId, ct);
        var run = await accessReviewStore.GetLatestFinishedRunAsync(clientId, ct);
        var snapshot = run is null ? null : await accessReviewStore.GetSnapshotAsync(run.RunId, ct);
        var estadoBase = EstadoRbac.Resolver(snapshot, administradas.Count > 0);
        var rbacBase = snapshot is null ? [] : RbacRecolector.Mapear(snapshot);

        var rbacArchivo = estadoBase.Disponibilidad == DisponibilidadRbac.Completo
            ? []
            : await informeValorStore.GetRbacAsync(clientId, ct);

        var (_, ejes, origen) = ResolverRbac(estadoBase, rbacBase, rbacArchivo);
        return (estadoBase with { Ejes = ejes }, origen);
    }

    /// <summary>
    /// Decisión 4 del brief ("precedencia: gana la base"), aplicada a las FILAS que va a consumir
    /// la calculadora — no solo al descarte del archivo al subirlo, que ya hacía el controller.
    /// Si <paramref name="estadoBase"/> ya es <see cref="DisponibilidadRbac.Completo"/>, gana la
    /// base sin mirar <paramref name="rbacArchivo"/> (ni siquiera se lo pide al store, ver
    /// <see cref="LeerAsync"/>). Si no, y el archivo trae filas, gana el archivo completo — no se
    /// mezcla con lo que la base sí pudo dar: el spec dice "se usa el archivo", no "se completa la
    /// base con el archivo". Sin archivo, se conserva lo que ya hacía el código antes de esta
    /// tarea: las filas (parciales o vacías) que la base pudo dar, con origen "base" si hay alguna
    /// y sin origen si no hay ninguna de las dos fuentes.
    ///
    /// <para><b>Los ejes viajan con la fuente.</b> Por archivo se recalculan sobre ESAS filas (ver
    /// <see cref="EjesDesdeArchivo"/>), nunca los de <see cref="EstadoRbac.Resolver"/>, que
    /// describen la corrida de base: un export que sí trae "Último login" no puede quedar marcado
    /// como no medido solo porque la corrida de base no lo pudo medir (el espejo de D9 — ahí se
    /// fabricaba un hallazgo falso, acá se suprimiría uno real).</para>
    ///
    /// <para>Internal para que <c>SqlInsumosBdRecolectorTests</c> lo pruebe como función pura, sin
    /// base de datos (mismo mecanismo que <see cref="ResolverNota"/>).</para>
    /// </summary>
    internal static (IReadOnlyList<RbacFila> Rbac, EjesRbac Ejes, string? Origen) ResolverRbac(
        EstadoRbacResultado estadoBase, IReadOnlyList<RbacFila> rbacBase, IReadOnlyList<RbacFila> rbacArchivo)
    {
        if (estadoBase.Disponibilidad == DisponibilidadRbac.Completo)
            return (rbacBase, estadoBase.Ejes, InsumosBd.OrigenBase);

        if (rbacArchivo.Count > 0)
            return (rbacArchivo, EjesDesdeArchivo(rbacArchivo), InsumosBd.OrigenArchivo);

        return (rbacBase, estadoBase.Ejes, rbacBase.Count > 0 ? InsumosBd.OrigenBase : null);
    }

    /// <summary>
    /// Los dos ejes de identidad medidos por ESTE conjunto de filas (ya releídas de
    /// <c>informe_valor_rbac</c>, no <see cref="RbacParseResult.Ejes"/> del parseo original: esos
    /// no se persisten — ver el comentario de clase de <see cref="RbacRow"/> — así que se
    /// recalculan sobre lo que sí sobrevive la vuelta por la base). Mismo criterio que
    /// <see cref="RbacParser"/> aplica columna por columna: el eje está medido si AL MENOS UNA fila
    /// trae texto no vacío. <see cref="RbacFila.UltimoLoginTexto"/> conserva el texto crudo de la
    /// celda (<see cref="RbacFilaConverter"/> ya colapsa vacío a null igual que el parser), así que
    /// la equivalencia es exacta. <see cref="RbacFila.CuentaHabilitada"/> en cambio es <c>bool?</c>
    /// ya interpretado ("Sí"/"No"/vacío): un texto no reconocido en la celda contaría acá como "no
    /// medido" en vez de "medido, pero no se pudo interpretar" — no distinguible del parseo
    /// original sin guardar el texto crudo también para esa columna, y el export solo escribe
    /// "Sí"/"No" en ella, así que el residual es teórico.
    /// </summary>
    internal static EjesRbac EjesDesdeArchivo(IReadOnlyList<RbacFila> filas) => new(
        EstadoCuentaMedido: filas.Any(f => f.CuentaHabilitada is not null),
        UltimoLoginMedido: filas.Any(f => f.UltimoLoginTexto is not null));

    private static async Task<IReadOnlyList<string>> SuscripcionesAdministradasAsync(
        SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SqlSuscripcionesAdministradas;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));

        var ids = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetString(0));
        return ids;
    }

    /// <summary>
    /// Bandera y nota por cliente (Crítico de la revisión de rama; la nota se agregó en la
    /// re-revisión, IMPORTANTE 2): cuando la seguridad se gestiona por fuera (Gestión de
    /// Vulnerabilidades), la pantalla WAF (<c>WafController.ListRecommendations</c>/<c>Sections</c>),
    /// el export a Excel (<c>WafController.BuildExportRowsAsync</c>) y el informe de gestión mensual
    /// (<c>ReportBuilder.WafRecommendationsAsync</c>) ocultan el pilar de Seguridad entero. Este
    /// informe ya ocultaba el pilar (bandera), pero no explicaba por qué: <see cref="InsumosBd"/>
    /// devolvía el pilar en cero igual que un cliente sin ningún hallazgo de seguridad, y la
    /// calculadora de la entrega siguiente no tenía cómo distinguir los dos casos. Ver
    /// <see cref="ResolverNota"/> para el criterio de la nota.
    ///
    /// <para>Lectura calcada de <c>ReportBuilder.WafRecommendationsAsync</c> (no de
    /// <c>IClientStore.GetSecurityManagementAsync</c>, que abriría una cuarta conexión solo para
    /// esto y de paso correría su ALTER TABLE de esquema en un endpoint de solo lectura): el guard
    /// <c>COL_LENGTH</c> hace que una base sin la columna todavía (nunca se guardó la bandera para
    /// ningún cliente) devuelva "no gestionada, sin nota" en vez de reventar.</para>
    /// </summary>
    private static async Task<(bool Managed, string? Nota)> SeguridadGestionadaExternamenteAsync(
        SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SqlSeguridadGestionadaExternamente;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return (false, null);

        var managed = !rd.IsDBNull(0) && rd.GetBoolean(0);
        var notaCruda = rd.IsDBNull(1) ? null : rd.GetString(1);
        return (managed, ResolverNota(managed, notaCruda));
    }

    /// <summary>SQL de <see cref="SeguridadGestionadaExternamenteAsync"/>, expuesto para que el test
    /// de texto confirme que trae las dos columnas (antes de la re-revisión solo traía la bandera,
    /// nunca la nota).</summary>
    internal const string SqlSeguridadGestionadaExternamente = """
        IF COL_LENGTH('dbo.clients', 'security_managed_externally') IS NOT NULL
            SELECT security_managed_externally, security_managed_note FROM dbo.clients WHERE client_id = @clientId;
        """;

    /// <summary>
    /// Nota que ve la calculadora del informe cuando el pilar de Seguridad sale vacío (IMPORTANTE 2
    /// de la re-revisión). Calcada de la tarjeta de Seguridad de <c>WafController.Sections</c>
    /// (<c>isSecMgmt ? resolvedNote : null</c>): <c>null</c> cuando el cliente NO gestiona su
    /// seguridad aparte — ahí no hay nada que explicar, el pilar puede estar simplemente vacío
    /// porque no hay hallazgos — y el texto por defecto (<see cref="WafConstants.SecurityManagedDefaultNote"/>)
    /// cuando SÍ la gestiona aparte pero el cliente no escribió una nota propia en <c>dbo.clients</c>.
    /// </summary>
    internal static string? ResolverNota(bool managed, string? notaCruda) =>
        !managed ? null : string.IsNullOrWhiteSpace(notaCruda) ? WafConstants.SecurityManagedDefaultNote : notaCruda;
}
