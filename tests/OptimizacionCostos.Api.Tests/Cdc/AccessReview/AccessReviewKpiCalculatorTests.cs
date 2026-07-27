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

    /// <summary>En producción los KPIs y las cuentas se calculan juntos; el test hace lo mismo.</summary>
    private static AccessReviewKpis Kpis(AccessReviewSnapshot s, int days = 90) =>
        AccessReviewKpiCalculator.Compute(s, AccessReviewAccountBuilder.Build(s), days, Now);

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

        var k = Kpis(snapshot);

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

    [Fact]
    public void Guests_con_rbac_no_cuentan_como_internos_sin_mfa()
    {
        // Sin el guard "UserType != Guest" en AccessReviewKpiCalculator, u2 también se contaría
        // (mfa disabled) y el resultado sería 2.
        var snapshot = new AccessReviewSnapshot(Run, [],
            [
                Row("u1", mfa: "disabled"),                                  // interno sin MFA: cuenta
                Row("u2", userType: "Guest", mfa: "disabled"),               // guest con RBAC: NO debe contar
            ],
            [], []);

        var k = Kpis(snapshot);

        Assert.Equal(1, k.InternosSinMfaConRbac);
    }

    [Fact]
    public void Solo_cuentas_tipo_User_cuentan_para_deshabilitadas_e_inactivas()
    {
        // Sin el filtro PrincipalType == "User" previo al cálculo, el grupo y el service principal
        // (ambos deshabilitados + inactivos) también se contarían y el resultado sería 3 en cada KPI.
        var snapshot = new AccessReviewSnapshot(Run, [],
            [
                Row("u1", enabled: false, lastSignIn: Now.AddDays(-200)),                                     // user: cuenta
                Row("grp1", ptype: "Group", userType: null, enabled: false, lastSignIn: Now.AddDays(-200)),   // grupo: NO debe contar
                Row("sp1", ptype: "ServicePrincipal", userType: null, enabled: false, lastSignIn: Now.AddDays(-200)), // SP: NO debe contar
            ],
            [], []);

        var k = Kpis(snapshot);

        Assert.Equal(1, k.CuentasDeshabilitadasConRbac);
        Assert.Equal(1, k.CuentasInactivasConRbac);
    }

    [Fact]
    public void Guests_inactivos_sin_permisos_no_cuentan_en_con_permisos()
    {
        // Prueba que RolesInSubs realmente discrimina: dos guests inactivos, solo uno con permisos.
        var snapshot = new AccessReviewSnapshot(Run, [], [],
            [
                Guest("g1", Now.AddDays(-120), "Reader (Sub Uno)"), // inactivo con permisos
                Guest("g2", Now.AddDays(-120), null),               // inactivo SIN permisos
            ],
            []);

        var k = Kpis(snapshot);

        Assert.Equal(2, k.GuestsInactivos);
        Assert.Equal(1, k.GuestsInactivosConPermisos);
    }

    [Fact]
    public void Service_principals_se_cuentan_distintos_no_por_fila()
    {
        // Fila duplicada del mismo principal (p.ej. asignado en dos scopes): debe colapsar a 1 distinto,
        // mientras que TotalAsignaciones sigue contando cada fila.
        var snapshot = new AccessReviewSnapshot(Run, [],
            [
                Row("sp1", ptype: "ServicePrincipal", userType: null, enabled: null, mfa: null),
                Row("sp1", ptype: "ServicePrincipal", userType: null, enabled: null, mfa: null), // duplicado: mismo principal
                Row("sp2", ptype: "ServicePrincipal", userType: null, enabled: null, mfa: null),
            ],
            [], []);

        var k = Kpis(snapshot);

        Assert.Equal(2, k.ServicePrincipalsUnicos);
        Assert.Equal(3, k.TotalAsignaciones);
    }

    private static AccessAssignmentRow Clase(string pid, string? roleClass, string roleDef = "def-1",
        string? userType = "Member", string? login = null, bool custom = false) =>
        new("s1", "Sub", null, "/subscriptions/s1", "subscription", "Rol", roleDef,
            pid, "User", $"N {pid}", login ?? $"{pid}@x.com", userType, null, null, true, null, "enabled",
            roleClass, custom);

    [Fact]
    public void Cuenta_elevadas_owners_y_porcentaje()
    {
        var snapshot = new AccessReviewSnapshot(Run, [new(1, "cred", "ok", "ok", null)],
            [
                Clase("u1", "owner"),
                Clase("u2", "otorga_accesos", "def-2"),
                Clase("u3", "escritura_total", "def-3"),
                Clase("u4", "escritura_servicio", "def-4"),   // NO elevada
                Clase("u5", "lectura", "def-5"),              // NO elevada
                Clase("u6", null, "def-6"),                   // sin clasificar: NO elevada
            ], [], []);

        var k = Kpis(snapshot);

        Assert.Equal(6, k.CuentasUnicas);
        Assert.Equal(3, k.AsignacionesElevadas);
        Assert.Equal(50.0m, k.PctElevadas);
        Assert.Equal(1, k.Owners);
    }

    [Fact]
    public void Porcentaje_de_elevadas_no_divide_por_cero()
    {
        var k = Kpis(new AccessReviewSnapshot(Run, [], [], [], []));

        Assert.Equal(0m, k.PctElevadas);
        Assert.Equal(0, k.CuentasUnicas);
    }

    [Fact]
    public void Cuenta_externas_y_owners_externos()
    {
        var snapshot = new AccessReviewSnapshot(Run, [new(1, "cred", "ok", "ok", null)],
            [
                Clase("ext1", "owner", login: "juan_prov.com#EXT#@contoso.onmicrosoft.com"),
                Clase("ext2", "lectura", "def-2", userType: "Guest"),
                Clase("int1", "owner", "def-3"),
            ], [], []);

        var k = Kpis(snapshot);

        Assert.Equal(2, k.CuentasExternasConRbac);
        Assert.Equal(1, k.OwnersExternos);   // solo ext1: ext2 es externa pero sin Owner
    }

    [Fact]
    public void Sin_graph_las_externas_no_se_cuentan_como_cero_medido()
    {
        // Con Graph incompleto el eje externo es null en todas las cuentas → los contadores quedan
        // en 0, y es la UI la que debe mostrarlos como "n/d" en vez de como un cero verde.
        var snapshot = new AccessReviewSnapshot(Run, [new(1, "cred", "ok", "sin_consent", null)],
            [Clase("ext1", "owner", login: "juan_prov.com#EXT#@contoso.onmicrosoft.com")], [], []);

        var k = Kpis(snapshot);

        Assert.Equal(0, k.CuentasExternasConRbac);
        Assert.Equal(0, k.OwnersExternos);
        Assert.Equal(1, k.Owners);            // el eje de privilegio sí sigue medido
    }

    [Fact]
    public void Cuenta_definiciones_de_rol_personalizadas_distintas()
    {
        var snapshot = new AccessReviewSnapshot(Run, [new(1, "cred", "ok", "ok", null)],
            [
                Clase("u1", "owner", "custom-1", custom: true),
                Clase("u2", "owner", "custom-1", custom: true),   // mismo rol: cuenta una vez
                Clase("u3", "lectura", "custom-2", custom: true),
                Clase("u4", "lectura", "builtin-1"),
            ], [], []);

        Assert.Equal(2, Kpis(snapshot).RolesPersonalizados);
    }

    [Fact]
    public void El_mismo_rol_personalizado_en_varias_suscripciones_cuenta_una_vez()
    {
        // Caso real (E2E BANCO DELTA): 4 roles personalizados se reportaban como 18 porque ARM
        // prefija el roleDefinitionId con la suscripción consultada.
        var snapshot = new AccessReviewSnapshot(Run, [new(1, "cred", "ok", "ok", null)],
            [
                Clase("u1", "escritura_servicio", "/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/darktrace", custom: true),
                Clase("u2", "escritura_servicio", "/subscriptions/s2/providers/Microsoft.Authorization/roleDefinitions/darktrace", custom: true),
                Clase("u3", "escritura_servicio", "/subscriptions/s3/providers/Microsoft.Authorization/roleDefinitions/darktrace", custom: true),
            ], [], []);

        Assert.Equal(1, Kpis(snapshot).RolesPersonalizados);
    }
}
