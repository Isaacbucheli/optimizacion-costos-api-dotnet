namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>
/// Agrega las asignaciones de una corrida por cuenta (principal): la unidad de lectura primaria del
/// módulo — miles de asignaciones son cientos de cuentas. Puro (sin BD): lo consumen el response,
/// los KPIs y el Excel, para que los tres muestren el mismo número.
/// </summary>
public static class AccessReviewAccountBuilder
{
    /// <summary>De más amplio a más puntual: un Owner heredado desde root pesa distinto que uno
    /// sobre un recurso suelto.</summary>
    private static readonly string[] ScopeBreadth =
        ["root", "management_group", "subscription", "resource_group", "resource"];

    /// <summary>Tipos que viven en el directorio del cliente: los únicos que pueden estar "eliminados
    /// de Entra ID" y los únicos donde la ausencia de nombre es un hallazgo.</summary>
    private static bool LivesInTenant(string principalType) =>
        principalType is "User" or "Group" or "ServicePrincipal";

    /// <summary>La fase Graph se leyó completa para todas las credenciales. `sin_licencia_p1` sí
    /// cuenta como completa: falta el último login, no el directorio.</summary>
    public static bool GraphComplete(AccessReviewSnapshot s) =>
        s.Run.Status != "error"
        && s.Credentials.All(c => c.GraphStatus is "ok" or "sin_licencia_p1");

    public static IReadOnlyList<AccessAccountRow> Build(AccessReviewSnapshot s)
    {
        var graphComplete = GraphComplete(s);

        return [.. s.Assignments
            .GroupBy(a => a.PrincipalObjectId)
            .Select(g => Account(g.Key, [.. g], graphComplete))
            .OrderByDescending(a => a.Owner)
            .ThenByDescending(a => a.OtorgaAccesos)
            .ThenByDescending(a => a.TotalAssignments)
            .ThenBy(a => a.DisplayName ?? a.PrincipalObjectId, StringComparer.OrdinalIgnoreCase)];
    }

    private static AccessAccountRow Account(string principalId, List<AccessAssignmentRow> rows, bool graphComplete)
    {
        // Asignación efectiva = (rol, scope). Dos fuentes de duplicación que hay que colapsar:
        //  1. La misma potestad obtenida de forma directa Y heredada por grupo es UNA sola.
        //  2. ARM devuelve las asignaciones heredadas (root / management group) una vez por
        //     suscripción consultada, y prefija el roleDefinitionId con esa suscripción → el mismo
        //     rol llega con N ids distintos. De ahí `RoleKey`, que compara solo el GUID del rol.
        var efectivas = rows
            .GroupBy(a => (Role: AccessReviewRoleClassifier.RoleKey(a.RoleDefinitionId), a.Scope))
            .Select(g => new
            {
                RoleClass = g.First().RoleClass,
                ScopeLevel = g.First().ScopeLevel,
                Directo = g.Any(x => x.ViaGroupId is null),
                PorGrupo = g.Any(x => x.ViaGroupId is not null),
            })
            .ToList();

        var type = rows[0].PrincipalType;
        // Los datos de directorio pueden haber resuelto en una fila y no en otra (directa vs. derivada).
        var displayName = rows.Select(a => a.DisplayName).FirstOrDefault(v => v is not null);
        var login = rows.Select(a => a.Login).FirstOrDefault(v => v is not null);
        var userType = rows.Select(a => a.UserType).FirstOrDefault(v => v is not null);

        var via = (efectivas.Any(e => e.Directo), efectivas.Any(e => e.PorGrupo)) switch
        {
            (true, true) => "ambos",
            (false, true) => "grupo",
            _ => "directo",
        };

        var broadest = ScopeBreadth.FirstOrDefault(level => efectivas.Any(e => e.ScopeLevel == level))
            ?? rows[0].ScopeLevel;

        int PorClase(string cls) => efectivas.Count(e => e.RoleClass == cls);

        return new AccessAccountRow(
            PrincipalObjectId: principalId,
            PrincipalType: type,
            DisplayName: displayName,
            Login: login,
            UserType: userType,
            IsExternal: External(type, userType, login, graphComplete),
            TotalAssignments: efectivas.Count,
            Owner: PorClase(AccessReviewRoleClassifier.Owner),
            OtorgaAccesos: PorClase(AccessReviewRoleClassifier.OtorgaAccesos),
            EscrituraTotal: PorClase(AccessReviewRoleClassifier.EscrituraTotal),
            EscrituraServicio: PorClase(AccessReviewRoleClassifier.EscrituraServicio),
            Lectura: PorClase(AccessReviewRoleClassifier.Lectura),
            SinClasificar: efectivas.Count(e => e.RoleClass is null),
            // A propósito se cuenta sobre las filas crudas, no sobre las efectivas: un Owner heredado
            // desde root llega reportado bajo las N suscripciones, y "tiene acceso en N suscripciones"
            // es la lectura correcta (colapsarlo mostraría subs=1 y subestimaría el alcance).
            Subscriptions: rows.Select(a => a.SubscriptionId).Distinct().Count(),
            BroadestScopeLevel: broadest,
            Via: via,
            AccountEnabled: rows.Select(a => a.AccountEnabled).FirstOrDefault(v => v is not null),
            LastSignIn: rows.Select(a => a.LastSignIn).FirstOrDefault(v => v is not null),
            MfaStatus: rows.Select(a => a.MfaStatus).FirstOrDefault(v => v is not null),
            // Principal con asignación RBAC que ya no existe en el directorio ("Identity not found"
            // en el portal). Solo se afirma con Graph completo: nombre vacío sin Graph significa
            // "no resuelto", no "eliminado".
            Orphan: graphComplete && LivesInTenant(type) && displayName is null);
    }

    /// <summary>
    /// Eje interna/externa. Requiere Graph (el `#EXT#` está en el UPN, y ARM solo entrega object IDs),
    /// pero NO requiere licencia P1 — sobrevive a más degradaciones que el eje de inactividad.
    /// null = no medido: preferible a afirmar "interna" sin dato. Público porque el response lo
    /// aplica también por asignación: una sola implementación de la regla.
    /// </summary>
    public static bool? External(string principalType, string? userType, string? login, bool graphComplete)
    {
        if (!graphComplete) return null;
        // Grupo administrado desde otro tenant: externo por definición, sin necesidad de UPN.
        if (principalType == "ForeignGroup") return true;
        // Sin UPN que mirar no hay forma de decidir (SP multi-tenant, dispositivo, tipo desconocido).
        if (principalType is not ("User" or "Group")) return null;
        if (string.Equals(userType, "Guest", StringComparison.OrdinalIgnoreCase)) return true;
        return login?.Contains("#EXT#@", StringComparison.OrdinalIgnoreCase) == true;
    }
}
