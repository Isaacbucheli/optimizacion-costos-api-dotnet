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
/// usa: pasa por <see cref="IAccessReviewStore"/>, que administra su propia conexión.
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
/// (<see cref="SqlSuscripcionesAdministradas"/>) y la bandera de seguridad gestionada externamente
/// (<see cref="SeguridadGestionadaExternamenteAsync"/>). Los dos son universo de datos, no
/// parámetros de una pantalla: sin ellos el informe podía mostrar hallazgos de una suscripción que
/// el cliente dejó de administrar, o del pilar de Seguridad cuando el cliente pidió no verlo (las
/// tres salidas del producto que sí lo respetan: pantalla WAF, export a Excel e informe mensual).
/// </para>
/// </summary>
public sealed class SqlInsumosBdRecolector(
    ISqlConnectionFactory factory, IAccessReviewStore accessReviewStore) : IInsumosBdRecolector
{
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
        await using var conn = await factory.OpenAsync(ct);

        // Advisor y Matriz dependen del schema WAF; Retiros, del de Boletín. Ninguno de los tres
        // recolectores lo asegura por sí mismo (ver sus comentarios de clase): centralizarlo acá
        // en vez de repetirlo en cada uno evita 3 chequeos de DDL idempotente por request cuando
        // 2 alcanzan (WAF sirve para Advisor y Matriz a la vez).
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await BoletinService.EnsureSchemaAsync(conn, ct);

        var administradas = await SuscripcionesAdministradasAsync(conn, clientId, ct);
        var seguridadGestionadaExternamente = await SeguridadGestionadaExternamenteAsync(conn, clientId, ct);

        var advisor = await AdvisorRecolector.LeerAsync(conn, clientId, administradas, seguridadGestionadaExternamente, ct);
        var matriz = await MatrizRecolector.LeerAsync(conn, clientId, administradas, seguridadGestionadaExternamente, ct);
        var retiros = await RetirosRecolector.LeerAsync(conn, clientId, ct);

        var run = await accessReviewStore.GetLatestFinishedRunAsync(clientId, ct);
        var snapshot = run is null ? null : await accessReviewStore.GetSnapshotAsync(run.RunId, ct);
        var estadoRbac = EstadoRbac.Resolver(snapshot, administradas.Count > 0);
        var rbac = snapshot is null ? [] : RbacRecolector.Mapear(snapshot);

        return new InsumosBd(advisor, matriz, rbac, retiros, estadoRbac, DateTime.UtcNow);
    }

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
    /// Bandera por cliente (Crítico de la revisión de rama): cuando la seguridad se gestiona por
    /// fuera (Gestión de Vulnerabilidades), la pantalla WAF (<c>WafController.ListRecommendations</c>),
    /// el export a Excel (<c>WafController.BuildExportRowsAsync</c>) y el informe de gestión mensual
    /// (<c>ReportBuilder.WafRecommendationsAsync</c>) ocultan el pilar de Seguridad entero. Este
    /// informe no lo hacía: publicaba justo lo que el cliente pidió no ver.
    ///
    /// <para>Lectura calcada de <c>ReportBuilder.WafRecommendationsAsync</c> (no de
    /// <c>IClientStore.GetSecurityManagementAsync</c>, que abriría una cuarta conexión solo para
    /// esto y de paso correría su ALTER TABLE de esquema en un endpoint de solo lectura): el guard
    /// <c>COL_LENGTH</c> hace que una base sin la columna todavía (nunca se guardó la bandera para
    /// ningún cliente) devuelva "no gestionada" en vez de reventar.</para>
    /// </summary>
    private static async Task<bool> SeguridadGestionadaExternamenteAsync(
        SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF COL_LENGTH('dbo.clients', 'security_managed_externally') IS NOT NULL
                SELECT security_managed_externally FROM dbo.clients WHERE client_id = @clientId;
            """;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is bool b && b;
    }
}
