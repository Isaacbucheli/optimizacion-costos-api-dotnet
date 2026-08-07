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
/// </summary>
public sealed class SqlInsumosBdRecolector(
    ISqlConnectionFactory factory, IAccessReviewStore accessReviewStore) : IInsumosBdRecolector
{
    /// <summary>
    /// Mismo predicado canónico de suscripciones administradas que Optimization/WAF/Inventory/
    /// Boletín (ver <c>BoletinService.ManagedSubscriptionsAsync</c>): <c>is_managed</c> lo decide el
    /// usuario, nunca el sync (<c>COALESCE(is_managed,1)=1</c> trata NULL como "sí administrada").
    /// Sin esto, <see cref="EstadoRbac.Resolver"/> no podría distinguir "sin corrida" de "sin nada
    /// que sincronizar": un cliente sin suscripciones administradas cierra su corrida en <c>error</c>
    /// sin filas de estado por credencial, así que esa distinción no sale de la corrida misma.
    /// </summary>
    internal const string SqlTieneSuscripcionesAdministradas = """
        SELECT COUNT(*) FROM dbo.client_azure_subscriptions
        WHERE client_id = @clientId AND is_active = 1 AND COALESCE(is_managed, 1) = 1
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

        var advisor = await AdvisorRecolector.LeerAsync(conn, clientId, ct);
        var matriz = await MatrizRecolector.LeerAsync(conn, clientId, ct);
        var retiros = await RetirosRecolector.LeerAsync(conn, clientId, ct);
        var tieneSuscripcionesAdministradas = await TieneSuscripcionesAdministradasAsync(conn, clientId, ct);

        var run = await accessReviewStore.GetLatestFinishedRunAsync(clientId, ct);
        var snapshot = run is null ? null : await accessReviewStore.GetSnapshotAsync(run.RunId, ct);
        var estadoRbac = EstadoRbac.Resolver(snapshot, tieneSuscripcionesAdministradas);
        var rbac = snapshot is null ? [] : RbacRecolector.Mapear(snapshot);

        return new InsumosBd(advisor, matriz, rbac, retiros, estadoRbac, DateTime.UtcNow);
    }

    private static async Task<bool> TieneSuscripcionesAdministradasAsync(
        SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SqlTieneSuscripcionesAdministradas;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        var count = (int)(await cmd.ExecuteScalarAsync(ct))!;
        return count > 0;
    }
}
