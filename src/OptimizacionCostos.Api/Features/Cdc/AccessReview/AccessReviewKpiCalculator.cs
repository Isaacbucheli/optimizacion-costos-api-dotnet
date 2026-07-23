namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public static class AccessReviewKpiCalculator
{
    public static AccessReviewKpis Compute(AccessReviewSnapshot s, int inactivityDays, DateTimeOffset now)
    {
        var users = s.Assignments.Where(a => a.PrincipalType == "User").ToList();
        // Una cuenta se evalúa una sola vez aunque tenga N asignaciones.
        var byUser = users.GroupBy(a => a.PrincipalObjectId).Select(g => g.First()).ToList();

        bool Inactive(DateTimeOffset? lastSignIn) =>
            lastSignIn is not null && (now - lastSignIn.Value).TotalDays > inactivityDays;

        var internosSinMfa = byUser.Count(u => u.UserType != "Guest" && u.MfaStatus == "disabled");
        var deshabilitadas = byUser.Count(u => u.AccountEnabled == false);
        var inactivas = byUser.Count(u => Inactive(u.LastSignIn));

        var guestsInactivos = s.Guests.Where(g => Inactive(g.LastSignIn)).ToList();

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
            TotalAsignaciones: s.Assignments.Count);
    }
}
