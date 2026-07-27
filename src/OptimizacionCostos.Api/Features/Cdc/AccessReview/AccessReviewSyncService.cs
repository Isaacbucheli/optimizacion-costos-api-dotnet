using System.Collections.Concurrent;
using System.Net;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>Credencial con sus subs administradas. AuthType: app_secret|user_session.</summary>
public sealed record AccessCredentialUnit(
    int CredentialId, string? CredentialName, string AuthType,
    IReadOnlyList<(string SubscriptionId, string? SubscriptionName, string? State)> Subscriptions);

public interface IAccessReviewSyncService
{
    /// <summary>Ejecuta la corrida runId del cliente y persiste resultados + estado final.</summary>
    Task RunAsync(int runId, int clientId, CancellationToken ct = default);
}

public class AccessReviewSyncService(
    IAccessReviewArmClient arm, IAccessReviewGraphClient graph, IAccessReviewStore store,
    ISqlConnectionFactory factory, ILogger<AccessReviewSyncService> logger) : IAccessReviewSyncService
{
    private const int MfaConcurrency = 8;

    protected virtual async Task<IReadOnlyList<AccessCredentialUnit>> CredentialUnitsAsync(int clientId, CancellationToken ct)
    {
        // dbo.client_azure_subscriptions no tiene columna de estado de la suscripción en Azure
        // (solo is_active/is_managed, que ya filtran la query); SubscriptionState queda null en producción.
        var bySub = new Dictionary<int, (string? Name, string AuthType, List<(string, string?, string?)> Subs)>();
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.credential_id, c.credential_name, COALESCE(c.auth_type, 'app_secret'),
                   s.subscription_id, s.subscription_name
            FROM dbo.client_azure_subscriptions s
            INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
            WHERE s.client_id = @cid AND s.is_active = 1 AND COALESCE(s.is_managed, 1) = 1 AND c.is_active = 1
            ORDER BY c.credential_id
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            if (!bySub.TryGetValue(id, out var u))
                u = bySub[id] = (r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), []);
            u.Subs.Add((r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), null));
        }
        return bySub.Select(kv => new AccessCredentialUnit(kv.Key, kv.Value.Name, kv.Value.AuthType,
            kv.Value.Subs.Select(s => (s.Item1, s.Item2, s.Item3)).ToList())).ToList();
    }

    public async Task RunAsync(int runId, int clientId, CancellationToken ct = default)
    {
        var assignments = new List<AccessAssignmentRow>();
        var guests = new List<AccessGuestRow>();
        var globalAdmins = new List<AccessGlobalAdminRow>();
        var credStatuses = new List<AccessCredStatus>();
        var anyProblem = false;

        var units = await CredentialUnitsAsync(clientId, ct);
        if (units.Count == 0)
        {
            await store.MarkFinishedAsync(runId, "error", "El cliente no tiene credenciales con suscripciones administradas.", ct);
            return;
        }

        foreach (var unit in units)
        {
            // ── Fase ARM ──────────────────────────────────────────────
            var armRows = new List<(ArmRoleAssignment A, string SubId, string? SubName, string? SubState)>();
            string armStatus = "ok"; string? armDetail = null;
            foreach (var (subId, subName, subState) in unit.Subscriptions)
            {
                try
                {
                    foreach (var a in await arm.GetRoleAssignmentsAsync(unit.CredentialId, subId, ct))
                        armRows.Add((a, subId, subName, subState));
                }
                catch (Exception ex)
                {
                    armStatus = "error";
                    armDetail = $"{subId}: {ex.GetType().Name}: {Trunc(ex.Message, 300)}";
                    anyProblem = true;
                    logger.LogWarning(ex, "ARM fallo cred {Cred} sub {Sub}", unit.CredentialId, subId);
                }
            }

            // ── Fase Graph ────────────────────────────────────────────
            string graphStatus; string? graphDetail = null;
            GraphUserSweep? sweep = null;
            IReadOnlyList<GraphDirectoryObject> gas = [];
            IReadOnlyDictionary<string, GraphDirectoryObject> dirObjects = new Dictionary<string, GraphDirectoryObject>();
            var groupMembers = new Dictionary<string, IReadOnlyList<GraphDirectoryObject>>();

            if (unit.AuthType == "user_session")
            {
                graphStatus = "no_aplica";
                graphDetail = "Credencial de sesión de usuario (Lighthouse): Graph no cruza tenants.";
                anyProblem = true;
            }
            else
            {
                try
                {
                    sweep = await graph.SweepUsersAsync(unit.CredentialId, ct);
                    graphStatus = sweep.SignInActivityAvailable ? "ok" : "sin_licencia_p1";
                    if (!sweep.SignInActivityAvailable)
                    {
                        graphDetail = "El tenant no expone signInActivity (requiere Entra ID P1/P2).";
                        anyProblem = true;
                    }

                    gas = await graph.GetGlobalAdminsAsync(unit.CredentialId, ct);

                    // Solo se piden al directorio los tipos que viven en el tenant del cliente:
                    // un ForeignGroup (otro tenant), un Device o un principal sin tipo nunca van a
                    // resolver, y pedirlos es ruido garantizado en getByIds.
                    var unresolved = armRows
                        .Where(x => x.A.PrincipalType is "User" or "Group" or "ServicePrincipal")
                        .Select(x => x.A.PrincipalId)
                        .Where(id => !sweep.ById.ContainsKey(id))
                        .Distinct().ToList();
                    dirObjects = await graph.GetByIdsAsync(unit.CredentialId, unresolved, ct);

                    foreach (var gid in armRows.Where(x => x.A.PrincipalType == "Group").Select(x => x.A.PrincipalId).Distinct())
                        groupMembers[gid] = await graph.GetGroupTransitiveMembersAsync(unit.CredentialId, gid, ct);
                }
                catch (Exception ex)
                {
                    // Solo 401/403 son consent/permisos de Graph. Cualquier otra falla (404, 5xx, red,
                    // secreto inválido) se reporta como "error": etiquetarla sin_consent despista el
                    // diagnóstico (caso real: 404 de un grupo borrado leído como falta de consent).
                    if ((ex as HttpRequestException)?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        graphStatus = "sin_consent";
                        graphDetail = $"{ex.GetType().Name}: {Trunc(ex.Message, 300)}. Revisar admin consent de Graph.";
                    }
                    else
                    {
                        graphStatus = "error";
                        graphDetail = $"{ex.GetType().Name}: {Trunc(ex.Message, 300)}";
                    }
                    anyProblem = true;
                    logger.LogWarning(ex, "Graph fallo cred {Cred}", unit.CredentialId);
                }
            }

            // ── Estado de MFA ─────────────────────────────────────────
            // Es el costo dominante de la corrida: un round-trip a Graph por identidad única, y no
            // hay endpoint por lotes. Se precalcula en paralelo con tope MfaConcurrency (Graph
            // aplica throttling 429 si se abusa) antes de componer las filas. MfaAsync sigue siendo
            // el único accesor: un id que no haya quedado en el prefetch se resuelve igual, solo sin
            // la ganancia — el prefetch es una optimización, no la fuente de verdad.
            var mfaCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var mfaApplies = graphStatus is not ("no_aplica" or "sin_consent");

            async Task<string?> MfaAsync(string userId)
            {
                if (!mfaApplies) return null;
                if (mfaCache.TryGetValue(userId, out var m)) return m;
                return mfaCache[userId] = await graph.GetMfaStatusAsync(unit.CredentialId, userId, ct);
            }

            if (mfaApplies)
            {
                // Mismo conjunto que pediría la composición, para no gastar llamadas de más.
                var pending = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (a, _, _, _) in armRows)
                    if (a.PrincipalType == "User" && sweep?.ById.ContainsKey(a.PrincipalId) == true)
                        pending.Add(a.PrincipalId);
                foreach (var members in groupMembers.Values)
                    foreach (var m in members)
                        if (m.OdataType == "#microsoft.graph.user") pending.Add(m.Id);
                if (sweep is not null && graphStatus is "ok" or "sin_licencia_p1")
                {
                    foreach (var u in sweep.ById.Values)
                        if (u.UserType == "Guest") pending.Add(u.Id);
                    foreach (var ga in gas) pending.Add(ga.Id);
                }

                if (pending.Count > 0)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await Parallel.ForEachAsync(pending,
                        new ParallelOptions { MaxDegreeOfParallelism = MfaConcurrency, CancellationToken = ct },
                        async (id, token) =>
                        {
                            mfaCache[id] = await graph.GetMfaStatusAsync(unit.CredentialId, id, token);
                        });
                    logger.LogInformation(
                        "MFA resuelto para {Count} identidades de la credencial {Cred} en {Seconds:0.0}s (concurrencia {Max})",
                        pending.Count, unit.CredentialId, sw.Elapsed.TotalSeconds, MfaConcurrency);
                }
            }

            // ── Composición de filas ──────────────────────────────────

            // 1) Asignaciones directas + fila del grupo + filas derivadas.
            foreach (var (a, subId, subName, subState) in armRows)
            {
                var user = sweep?.ById.GetValueOrDefault(a.PrincipalId);
                var dir = dirObjects.GetValueOrDefault(a.PrincipalId);

                if (a.PrincipalType == "User")
                {
                    assignments.Add(new AccessAssignmentRow(subId, subName, subState, a.Scope, a.ScopeLevel,
                        a.RoleName, a.RoleDefinitionId, a.PrincipalId, "User",
                        user?.DisplayName ?? dir?.DisplayName, user?.Upn ?? dir?.Upn,
                        user?.UserType ?? dir?.UserType, null, null,
                        user?.AccountEnabled, user?.LastSignIn,
                        user is not null ? await MfaAsync(a.PrincipalId) : null,
                        a.RoleClass, a.IsCustomRole));
                }
                else if (a.PrincipalType == "ServicePrincipal")
                {
                    assignments.Add(new AccessAssignmentRow(subId, subName, subState, a.Scope, a.ScopeLevel,
                        a.RoleName, a.RoleDefinitionId, a.PrincipalId, "ServicePrincipal",
                        dir?.DisplayName, dir?.AppId, null, null, null, null, null, null,
                        a.RoleClass, a.IsCustomRole));
                }
                else if (a.PrincipalType == "Group") // fila del grupo + derivadas por miembro transitivo (solo usuarios).
                {
                    var groupName = dir?.DisplayName;
                    assignments.Add(new AccessAssignmentRow(subId, subName, subState, a.Scope, a.ScopeLevel,
                        a.RoleName, a.RoleDefinitionId, a.PrincipalId, "Group",
                        groupName, null, null, null, null, null, null, null,
                        a.RoleClass, a.IsCustomRole));

                    foreach (var m in groupMembers.GetValueOrDefault(a.PrincipalId, []))
                    {
                        if (m.OdataType != "#microsoft.graph.user") continue;
                        var mu = sweep?.ById.GetValueOrDefault(m.Id);
                        // La fila derivada hereda la clase del rol de la asignación del grupo.
                        assignments.Add(new AccessAssignmentRow(subId, subName, subState, a.Scope, a.ScopeLevel,
                            a.RoleName, a.RoleDefinitionId, m.Id, "User",
                            mu?.DisplayName ?? m.DisplayName, mu?.Upn ?? m.Upn, mu?.UserType ?? m.UserType,
                            a.PrincipalId, groupName, mu?.AccountEnabled, mu?.LastSignIn, await MfaAsync(m.Id),
                            a.RoleClass, a.IsCustomRole));
                    }
                }
                else
                {
                    // ForeignGroup (grupo administrado desde otro tenant), Device, Unknown: se
                    // persisten tal cual, sin resolver ni expandir. No viven en el directorio del
                    // cliente, así que la ausencia de nombre es lo esperado y NO una asignación
                    // huérfana. Se conserva el tipo real: etiquetarlos "Group" mentiría sobre su origen.
                    assignments.Add(new AccessAssignmentRow(subId, subName, subState, a.Scope, a.ScopeLevel,
                        a.RoleName, a.RoleDefinitionId, a.PrincipalId, a.PrincipalType,
                        null, null, null, null, null, null, null, null,
                        a.RoleClass, a.IsCustomRole));
                }
            }

            // 2) Guests del tenant (solo credenciales con Graph ok / sin_licencia_p1).
            if (sweep is not null && graphStatus is "ok" or "sin_licencia_p1")
            {
                var rolesByPrincipal = assignments
                    .Where(x => x.UserType == "Guest" || sweep.ById.GetValueOrDefault(x.PrincipalObjectId)?.UserType == "Guest")
                    .GroupBy(x => x.PrincipalObjectId)
                    .ToDictionary(g => g.Key,
                        g => string.Join(" | ", g.Select(x => $"{x.RoleName} ({x.SubscriptionName ?? x.SubscriptionId})").Distinct().Order()));

                foreach (var u in sweep.ById.Values.Where(u => u.UserType == "Guest"))
                {
                    var domain = ExternalDomain(u);
                    guests.Add(new AccessGuestRow(u.Id, u.DisplayName, u.Mail ?? u.Upn, domain,
                        u.AccountEnabled ?? false, u.ExternalState, u.CreatedAt, u.LastSignIn,
                        rolesByPrincipal.GetValueOrDefault(u.Id), await MfaAsync(u.Id)));
                }

                // 3) Global Admins.
                foreach (var ga in gas)
                {
                    var gu = sweep.ById.GetValueOrDefault(ga.Id);
                    globalAdmins.Add(new AccessGlobalAdminRow(ga.Id, gu?.DisplayName ?? ga.DisplayName,
                        gu?.Upn ?? ga.Upn, gu?.UserType ?? ga.UserType ?? "Member",
                        gu?.AccountEnabled, gu?.LastSignIn, await MfaAsync(ga.Id)));
                }
            }

            credStatuses.Add(new AccessCredStatus(unit.CredentialId, unit.CredentialName, armStatus, graphStatus,
                Join(armDetail, graphDetail)));
        }

        // Dos credenciales pueden resolver al mismo tenant de Azure AD: los guests/GAs no deben duplicarse
        // (las asignaciones sí son por-suscripción/por-credencial y no se deduplican).
        guests = guests.GroupBy(g => g.ObjectId).Select(g => g.First()).ToList();
        globalAdmins = globalAdmins.GroupBy(g => g.ObjectId).Select(g => g.First()).ToList();

        await store.SaveResultsAsync(runId, assignments, guests, globalAdmins, credStatuses, ct);
        await store.MarkFinishedAsync(runId, anyProblem ? "partial" : "ok", null, ct);
    }

    private static string Trunc(string s, int max) => s.Length > max ? s[..max] : s;
    private static string? Join(string? a, string? b) =>
        (a, b) switch { (null, null) => null, (null, _) => b, (_, null) => a, _ => $"{a} | {b}" };

    private static string? ExternalDomain(GraphUser u)
    {
        if (u.Upn is not null && u.Upn.Contains("#EXT#@", StringComparison.OrdinalIgnoreCase))
        {
            var local = u.Upn[..u.Upn.IndexOf("#EXT#@", StringComparison.OrdinalIgnoreCase)];
            var idx = local.LastIndexOf('_');
            if (idx > 0) return local[(idx + 1)..];
        }
        return u.Mail?.Split('@') is [_, var dom] ? dom : null;
    }
}
