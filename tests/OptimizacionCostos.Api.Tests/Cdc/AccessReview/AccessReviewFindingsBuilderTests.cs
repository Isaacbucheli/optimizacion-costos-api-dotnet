using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public class AccessReviewFindingsBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly AccessRunRef Run = new(1, 6, "ok", Now, Now, null, null);

    private static AccessAssignmentRow Row(
        string pid, string type = "User", string? roleClass = "lectura", string roleName = "Rol",
        string scope = "/subscriptions/s1", string scopeLevel = "subscription", string roleDef = "def-1",
        string? displayName = "Ana", string? login = "ana@x.com", string? userType = "Member",
        string? viaGroupId = null, bool? enabled = true, DateTimeOffset? lastSignIn = null,
        string? mfa = "enabled", bool custom = false, string sub = "s1") =>
        new(sub, $"Sub {sub}", null, scope, scopeLevel, roleName, roleDef, pid, type,
            displayName, login, userType, viaGroupId, viaGroupId is null ? null : "Grupo",
            enabled, lastSignIn, mfa, roleClass, custom);

    private static AccessGuestRow Guest(string id, DateTimeOffset? lastSignIn, string? roles) =>
        new(id, $"G {id}", $"{id}@ext.com", "ext.com", true, "Accepted", null, lastSignIn, roles, "disabled");

    private static IReadOnlyList<AccessFinding> Build(
        IEnumerable<AccessAssignmentRow> assignments,
        IEnumerable<AccessGuestRow>? guests = null,
        IEnumerable<AccessGlobalAdminRow>? admins = null,
        string graphStatus = "ok", string runStatus = "ok", int inactivityDays = 90)
    {
        var snapshot = new AccessReviewSnapshot(
            Run with { Status = runStatus },
            [new AccessCredStatus(1, "cred", "ok", graphStatus, null)],
            [.. assignments], [.. guests ?? []], [.. admins ?? []]);
        var accounts = AccessReviewAccountBuilder.Build(snapshot);
        var kpis = AccessReviewKpiCalculator.Compute(snapshot, accounts, inactivityDays, Now);
        return AccessReviewFindingsBuilder.Build(snapshot, accounts, kpis, inactivityDays, Now);
    }

    private static AccessFinding Get(IReadOnlyList<AccessFinding> findings, string key) =>
        Assert.Single(findings.Where(f => f.Key == key));

    // ── Reglas de ARM (se evalúan siempre) ────────────────────────────────

    [Fact]
    public void Owner_en_raiz_dispara_con_scope_root_o_management_group()
    {
        var f = Get(Build([
            Row("u1", roleClass: "owner", scope: "/", scopeLevel: "root"),
            Row("u2", roleClass: "otorga_accesos", scopeLevel: "management_group", roleDef: "def-2"),
            Row("u3", roleClass: "owner", scopeLevel: "subscription", roleDef: "def-3"),   // no dispara
        ]), "owner_en_raiz");

        Assert.Equal(AccessFindingSeverity.Critica, f.Severity);
        Assert.Equal(2, f.AffectedAccounts);
        Assert.Equal(["u1", "u2"], f.AffectedPrincipals.Order());
    }

    [Fact]
    public void Owner_en_raiz_no_dispara_sin_privilegio_de_otorgamiento()
    {
        // Un Reader heredado desde root no es un hallazgo.
        var f = Get(Build([Row("u1", roleClass: "lectura", scope: "/", scopeLevel: "root")]), "owner_en_raiz");

        Assert.Equal(0, f.AffectedAccounts);
        Assert.True(f.Evaluable);
    }

    [Fact]
    public void Grupo_foraneo_elevado_dispara()
    {
        var findings = Build([
            Row("fg1", type: "ForeignGroup", roleClass: "owner", displayName: null, login: null, userType: null),
            Row("fg2", type: "ForeignGroup", roleClass: "lectura", displayName: null, login: null,
                userType: null, roleDef: "def-2"),
        ]);

        var f = Get(findings, "grupo_foraneo_elevado");
        Assert.Equal(AccessFindingSeverity.Alta, f.Severity);
        Assert.Equal(["fg1"], f.AffectedPrincipals);
    }

    [Fact]
    public void Service_principal_con_otorgamiento_dispara()
    {
        var findings = Build([
            Row("sp1", type: "ServicePrincipal", roleClass: "otorga_accesos", userType: null, mfa: null),
            Row("sp2", type: "ServicePrincipal", roleClass: "escritura_total", userType: null, mfa: null, roleDef: "def-2"),
        ]);

        var f = Get(findings, "sp_con_otorgamiento");
        Assert.Equal(["sp1"], f.AffectedPrincipals);   // escritura_total no otorga accesos
    }

    [Fact]
    public void Rol_propio_elevado_dispara()
    {
        var f = Get(Build([
            Row("u1", roleClass: "owner", roleName: "Soporte N3", custom: true),
            Row("u2", roleClass: "lectura", roleName: "Auditoria", custom: true, roleDef: "def-2"),
        ]), "rol_propio_elevado");

        Assert.Equal(AccessFindingSeverity.Media, f.Severity);
        Assert.Equal(1, f.AffectedAssignments);
    }

    [Fact]
    public void Asignacion_directa_dispara_sobre_el_umbral_y_no_trae_afectados()
    {
        // 4 otorgamientos a personas de 4 agrupables = 100% > 70%.
        var f = Get(Build([
            Row("u1"), Row("u2", roleDef: "def-2"), Row("u3", roleDef: "def-3"), Row("u4", roleDef: "def-4"),
        ]), "asignacion_directa");

        Assert.Equal(AccessFindingSeverity.Media, f.Severity);
        Assert.Empty(f.AffectedPrincipals);
        Assert.Contains("100", f.Detail);
    }

    [Fact]
    public void Asignacion_directa_compara_personas_contra_grupos_no_contra_filas_derivadas()
    {
        // Un grupo con rol y 3 miembros produce 4 filas, pero es UN otorgamiento. Contando filas,
        // 1 de 5 daría 20% y la regla parecería sana; el criterio real es 1 persona vs 1 grupo = 50%.
        var f = Get(Build([
            Row("g1", type: "Group", displayName: "Grupo", login: null, userType: null),
            Row("m1", viaGroupId: "g1"), Row("m2", viaGroupId: "g1"), Row("m3", viaGroupId: "g1"),
            Row("u1", roleDef: "def-2"),
        ]), "asignacion_directa");

        Assert.Contains("50", f.Detail);
        Assert.Equal(0, f.AffectedAssignments);   // 50% no supera el umbral de 70%
    }

    [Fact]
    public void Asignacion_directa_ignora_service_principals()
    {
        // Un SP no es gente que se administre con membresías: no debe inflar el numerador.
        var f = Get(Build([
            Row("sp1", type: "ServicePrincipal", userType: null, mfa: null),
            Row("sp2", type: "ServicePrincipal", userType: null, mfa: null, roleDef: "def-2"),
            Row("g1", type: "Group", displayName: "Grupo", login: null, userType: null, roleDef: "def-3"),
        ]), "asignacion_directa");

        Assert.Contains("0%", f.Detail);
    }

    [Fact]
    public void Granularidad_recurso_dispara_sobre_el_umbral()
    {
        var f = Get(Build([
            Row("u1", scopeLevel: "resource"), Row("u2", scopeLevel: "resource", roleDef: "def-2"),
            Row("u3", scopeLevel: "subscription", roleDef: "def-3"),
        ]), "granularidad_recurso");

        Assert.Empty(f.AffectedPrincipals);
        Assert.Equal(2, f.AffectedAssignments);
    }

    [Fact]
    public void Granularidad_recurso_no_cuenta_las_filas_derivadas_de_grupos()
    {
        // Caso real del E2E: con las derivadas incluidas la métrica reportaba 68,3% donde el valor
        // real era 29,2%. Acá: 1 otorgamiento a nivel recurso de 2 = 50%; contando las 3 derivadas
        // del grupo daría 4 de 5 = 80%.
        var f = Get(Build([
            Row("g1", type: "Group", scopeLevel: "resource", displayName: "Grupo", login: null, userType: null),
            Row("m1", scopeLevel: "resource", viaGroupId: "g1"),
            Row("m2", scopeLevel: "resource", viaGroupId: "g1"),
            Row("m3", scopeLevel: "resource", viaGroupId: "g1"),
            Row("u1", scopeLevel: "subscription", roleDef: "def-2"),
        ]), "granularidad_recurso");

        Assert.Contains("50", f.Detail);
        Assert.Equal(1, f.AffectedAssignments);
    }

    // ── Reglas que dependen de Graph ──────────────────────────────────────

    [Fact]
    public void Externa_elevada_dispara_con_guest_y_con_ext()
    {
        var findings = Build([
            Row("g1", roleClass: "owner", userType: "Guest"),
            Row("e1", roleClass: "escritura_total", login: "juan_prov.com#EXT#@contoso.onmicrosoft.com", roleDef: "def-2"),
            Row("i1", roleClass: "owner", roleDef: "def-3"),                     // interna: no dispara
            Row("g2", roleClass: "lectura", userType: "Guest", roleDef: "def-4"),// externa sin privilegio
        ]);

        var f = Get(findings, "externa_elevada");
        Assert.Equal(AccessFindingSeverity.Critica, f.Severity);
        Assert.Equal(["e1", "g1"], f.AffectedPrincipals.Order());
    }

    [Fact]
    public void Principal_eliminado_dispara_solo_para_tipos_del_tenant()
    {
        var findings = Build([
            Row("u1", displayName: null, login: null),
            Row("fg1", type: "ForeignGroup", displayName: null, login: null, userType: null, roleDef: "def-2"),
        ]);

        var f = Get(findings, "principal_eliminado");
        Assert.Equal(["u1"], f.AffectedPrincipals);
    }

    [Fact]
    public void Deshabilitada_con_rbac_coincide_con_el_kpi()
    {
        var assignments = new[]
        {
            Row("u1", enabled: false),
            Row("u2", enabled: false, roleDef: "def-2"),
            Row("u3"),
        };
        var findings = Build(assignments);

        var f = Get(findings, "deshabilitada_con_rbac");
        Assert.Equal(AccessFindingSeverity.Alta, f.Severity);
        Assert.Equal(2, f.AffectedAccounts);
    }

    [Fact]
    public void Elevada_sin_mfa_dispara()
    {
        var findings = Build([
            Row("u1", roleClass: "owner", mfa: "disabled"),
            Row("u2", roleClass: "lectura", mfa: "disabled", roleDef: "def-2"),   // sin privilegio
            Row("u3", roleClass: "owner", mfa: "enabled", roleDef: "def-3"),      // con MFA
        ]);

        Assert.Equal(["u1"], Get(findings, "elevada_sin_mfa").AffectedPrincipals);
    }

    [Fact]
    public void Exceso_global_admins_usa_el_umbral_de_cinco()
    {
        AccessGlobalAdminRow Ga(string id) => new(id, $"A {id}", $"{id}@x.com", "Member", true, Now, "enabled");

        var seis = Build([Row("u1")], admins: [Ga("a1"), Ga("a2"), Ga("a3"), Ga("a4"), Ga("a5"), Ga("a6")]);
        var cinco = Build([Row("u1")], admins: [Ga("a1"), Ga("a2"), Ga("a3"), Ga("a4"), Ga("a5")]);

        Assert.Equal(6, Get(seis, "exceso_global_admins").AffectedAccounts);
        Assert.Equal(0, Get(cinco, "exceso_global_admins").AffectedAccounts);
    }

    [Fact]
    public void Guest_inactivo_con_permisos_dispara()
    {
        var findings = Build([Row("u1")],
            guests: [
                Guest("g1", Now.AddDays(-200), "Reader (Sub)"),   // inactivo con permisos
                Guest("g2", Now.AddDays(-200), null),             // inactivo sin permisos
                Guest("g3", Now.AddDays(-1), "Reader (Sub)"),     // activo
            ]);

        Assert.Equal(1, Get(findings, "guest_inactivo_con_permisos").AffectedAccounts);
    }

    // ── Inactividad y "nunca inició sesión" ───────────────────────────────

    [Fact]
    public void Nunca_inicio_sesion_es_distinto_de_inactiva()
    {
        var findings = Build([
            Row("nunca", lastSignIn: null),                    // jamás entró
            Row("vieja", lastSignIn: Now.AddDays(-200), roleDef: "def-2"),
            Row("activa", lastSignIn: Now.AddDays(-2), roleDef: "def-3"),
        ]);

        Assert.Equal(["nunca"], Get(findings, "nunca_inicio_sesion").AffectedPrincipals);
        Assert.Equal(["vieja"], Get(findings, "inactiva_con_rbac").AffectedPrincipals);
    }

    [Fact]
    public void Sin_licencia_p1_la_inactividad_no_es_evaluable()
    {
        // Sin signInActivity, last_sign_in nulo es ambiguo: no se puede afirmar "nunca entró".
        var findings = Build([Row("u1", lastSignIn: null)], graphStatus: "sin_licencia_p1");

        foreach (var key in new[] { "nunca_inicio_sesion", "inactiva_con_rbac", "guest_inactivo_con_permisos" })
        {
            var f = Get(findings, key);
            Assert.False(f.Evaluable);
            Assert.Equal(0, f.AffectedAccounts);
            Assert.NotNull(f.NotEvaluableReason);
        }
    }

    // ── Evaluabilidad ─────────────────────────────────────────────────────

    [Fact]
    public void Sin_graph_las_reglas_de_directorio_no_son_evaluables_y_las_de_arm_si()
    {
        var findings = Build([
            Row("u1", roleClass: "owner", scope: "/", scopeLevel: "root", displayName: null,
                login: null, userType: null, enabled: null, mfa: null),
        ], graphStatus: "sin_consent");

        var arm = Get(findings, "owner_en_raiz");
        Assert.True(arm.Evaluable);
        Assert.Equal(1, arm.AffectedAccounts);

        foreach (var key in new[] { "externa_elevada", "principal_eliminado", "deshabilitada_con_rbac", "elevada_sin_mfa" })
        {
            var f = Get(findings, key);
            Assert.False(f.Evaluable);
            Assert.Equal(0, f.AffectedAccounts);
        }
    }

    [Fact]
    public void Alcance_incompleto_informa_que_falto_leer()
    {
        var f = Get(Build([Row("u1")], graphStatus: "sin_consent"), "alcance_incompleto");

        Assert.Equal(AccessFindingSeverity.Informativa, f.Severity);
        Assert.Contains("consent", f.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Alcance_completo_no_reporta_faltantes()
    {
        Assert.Equal(0, Get(Build([Row("u1")]), "alcance_incompleto").AffectedAccounts);
    }

    // ── Forma del resultado ───────────────────────────────────────────────

    [Fact]
    public void No_repite_principals_aunque_tenga_varias_asignaciones_que_disparan()
    {
        var f = Get(Build([
            Row("u1", roleClass: "owner", scope: "/", scopeLevel: "root"),
            Row("u1", roleClass: "owner", scopeLevel: "management_group", roleDef: "def-2"),
            Row("u1", roleClass: "otorga_accesos", scopeLevel: "management_group", roleDef: "def-3"),
        ]), "owner_en_raiz");

        Assert.Equal(["u1"], f.AffectedPrincipals);
        Assert.Equal(1, f.AffectedAccounts);
        Assert.Equal(3, f.AffectedAssignments);
    }

    [Fact]
    public void Ordena_por_severidad_y_luego_por_afectados()
    {
        var findings = Build([
            Row("u1", roleClass: "owner", scope: "/", scopeLevel: "root"),
            Row("g1", roleClass: "owner", userType: "Guest", roleDef: "def-2"),
            Row("g2", roleClass: "owner", userType: "Guest", roleDef: "def-3"),
        ]);

        var conHallazgos = findings.Where(f => f.AffectedAccounts > 0).ToList();
        var ranks = conHallazgos.Select(f => AccessFindingSeverity.Rank(f.Severity)).ToList();
        Assert.Equal(ranks.OrderBy(r => r), ranks);
        // Entre las dos críticas, la de 2 cuentas va antes que la de 1.
        var criticas = conHallazgos.Where(f => f.Severity == AccessFindingSeverity.Critica).ToList();
        Assert.Equal("externa_elevada", criticas[0].Key);
    }

    [Fact]
    public void Devuelve_todas_las_reglas_incluso_sin_hallazgos()
    {
        // Un hallazgo limpio es información: la UI los agrupa al final, pero deben existir.
        var findings = Build([Row("u1", viaGroupId: "g1")]);

        Assert.Equal(15, findings.Count);
        Assert.All(findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Recommendation)));
        Assert.All(findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Title)));
    }
}
