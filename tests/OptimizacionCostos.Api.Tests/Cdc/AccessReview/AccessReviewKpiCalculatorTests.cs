using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public class AccessReviewKpiCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly AccessRunRef Run = new(1, 10, "ok", Now, Now, null, null);

    private static AccessAssignmentRow Row(string pid, string ptype = "User", string? userType = "Member",
        bool? enabled = true, DateTimeOffset? lastSignIn = null, string? mfa = "enabled") =>
        new("s1", "Sub", "Enabled", "/subscriptions/s1", "subscription", "Reader", "def",
            pid, ptype, $"N {pid}", $"{pid}@x.com", userType, null, null, enabled, lastSignIn, mfa);

    private static AccessGuestRow Guest(string id, DateTimeOffset? lastSignIn, string? roles) =>
        new(id, $"G {id}", $"{id}@ext.com", "ext.com", true, "Accepted", null, lastSignIn, roles, "disabled");

    [Fact]
    public void Cuenta_deshabilitados_inactivos_y_sin_mfa()
    {
        var snapshot = new AccessReviewSnapshot(Run, [],
            [
                Row("u1", lastSignIn: Now.AddDays(-5)),                                  // sano
                Row("u2", enabled: false, lastSignIn: Now.AddDays(-200)),                // deshabilitado + inactivo
                Row("u3", mfa: "disabled", lastSignIn: Now.AddDays(-100)),               // sin MFA + inactivo
                Row("u3", mfa: "disabled", lastSignIn: Now.AddDays(-100)),               // duplicado: cuenta 1 vez
                Row("sp1", ptype: "ServicePrincipal", userType: null, enabled: null, mfa: null),
                Row("g1", ptype: "Group", userType: null, enabled: null, mfa: null),     // grupos no cuentan como cuentas
            ],
            [Guest("gx", Now.AddDays(-120), "Reader (Sub)"), Guest("gy", Now.AddDays(-1), null), Guest("gz", null, null)],
            [
                new("a1", "Admin 1", "a1@x.com", "Member", true, Now.AddDays(-1), "enabled"),
                new("a2", "Admin 2", "a2@x.com", "Member", true, null, "disabled"),
            ]);

        var k = AccessReviewKpiCalculator.Compute(snapshot, 90, Now);

        Assert.Equal(2, k.GlobalAdmins);
        Assert.Equal(1, k.GlobalAdminsSinMfa);
        Assert.Equal(1, k.InternosSinMfaConRbac);           // u3 una sola vez
        Assert.Equal(1, k.CuentasDeshabilitadasConRbac);    // u2
        Assert.Equal(2, k.CuentasInactivasConRbac);         // u2 y u3 (>90 días)
        Assert.Equal(3, k.GuestsTotal);
        Assert.Equal(1, k.GuestsInactivos);                 // gx (gz sin registro no cuenta como inactivo)
        Assert.Equal(1, k.GuestsInactivosConPermisos);      // gx tiene roles
        Assert.Equal(1, k.ServicePrincipalsUnicos);
        Assert.Equal(6, k.TotalAsignaciones);
    }
}
