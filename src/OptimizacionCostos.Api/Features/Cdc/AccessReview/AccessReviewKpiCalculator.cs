namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public static class AccessReviewKpiCalculator
{
    /// <summary>`accounts` viene de AccessReviewAccountBuilder: se pasa ya construido para no
    /// agregar dos veces lo mismo y para que UI, KPIs y Excel muestren idéntico número.</summary>
    public static AccessReviewKpis Compute(AccessReviewSnapshot s, IReadOnlyList<AccessAccountRow> accounts,
        int inactivityDays, DateTimeOffset now,
        IReadOnlyDictionary<string, AccessDecision>? decisions = null)
    {
        decisions ??= new Dictionary<string, AccessDecision>();
        var users = s.Assignments.Where(a => a.PrincipalType == "User").ToList();
        // Una cuenta se evalúa una sola vez aunque tenga N asignaciones.
        var byUser = users.GroupBy(a => a.PrincipalObjectId).Select(g => g.First()).ToList();

        bool Inactive(DateTimeOffset? lastSignIn) =>
            lastSignIn is not null && (now - lastSignIn.Value).TotalDays > inactivityDays;

        var internosSinMfa = byUser.Count(u => u.UserType != "Guest" && u.MfaStatus == "disabled");
        var deshabilitadas = byUser.Count(u => u.AccountEnabled == false);
        var inactivas = byUser.Count(u => Inactive(u.LastSignIn));

        var guestsInactivos = s.Guests.Where(g => Inactive(g.LastSignIn)).ToList();

        // Denominador del % de elevadas: el mismo total que ya muestra la UI (filas de asignación,
        // incluidas las derivadas por grupo), para que las dos cifras sean comparables.
        var elevadas = s.Assignments.Count(a => AccessReviewRoleClassifier.IsElevated(a.RoleClass));

        return new AccessReviewKpis(
            GlobalAdmins: s.GlobalAdmins.Count,
            GlobalAdminsSinMfa: s.GlobalAdmins.Count(g => g.MfaStatus == "disabled"),
            InternosSinMfaConRbac: internosSinMfa,
            CuentasDeshabilitadasConRbac: deshabilitadas,
            CuentasInactivasConRbac: inactivas,
            GuestsTotal: s.Guests.Count,
            GuestsInactivos: guestsInactivos.Count,
            GuestsInactivosConPermisos: guestsInactivos.Count(g => !string.IsNullOrEmpty(g.RolesInSubs)),
            ServicePrincipalsUnicos: s.Assignments.Where(a => a.PrincipalType == "ServicePrincipal")
                .Select(a => a.PrincipalObjectId).Distinct().Count(),
            TotalAsignaciones: s.Assignments.Count,
            CuentasUnicas: accounts.Count,
            AsignacionesElevadas: elevadas,
            PctElevadas: s.Assignments.Count == 0 ? 0m : Math.Round(elevadas * 100m / s.Assignments.Count, 1),
            Owners: s.Assignments.Count(a => a.RoleClass == AccessReviewRoleClassifier.Owner),
            CuentasExternasConRbac: accounts.Count(a => a.IsExternal == true),
            OwnersExternos: accounts.Count(a => a.IsExternal == true && a.Owner > 0),
            // Por GUID, no por id completo: ARM prefija el roleDefinitionId con la suscripción
            // consultada, así que el mismo rol personalizado usado en N suscripciones llega con N ids
            // y se contaría N veces (en el E2E: 4 roles reales reportados como 18).
            RolesPersonalizados: s.Assignments.Where(a => a.IsCustomRole)
                .Select(a => AccessReviewRoleClassifier.RoleKey(a.RoleDefinitionId)).Distinct().Count(),
            // Accesos elevados que nadie decidió todavía: el número que el consultor tiene que bajar.
            PendientesDeRevisar: s.Assignments
                .Where(a => AccessReviewRoleClassifier.IsElevated(a.RoleClass))
                .Select(a => AccessReviewAccessKey.For(a.PrincipalObjectId, a.RoleDefinitionId, a.Scope))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(k => !decisions.ContainsKey(k)));
    }
}
