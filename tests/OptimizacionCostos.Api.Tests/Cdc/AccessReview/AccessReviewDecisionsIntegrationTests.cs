using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

/// <summary>
/// Cómo afectan las decisiones a cuentas, contadores y hallazgos. El criterio de producto es que
/// SOLO `justificado` descuenta: `revocar` es una promesa (mientras el acceso exista sigue siendo
/// riesgo) y `mantener` es una revisión (no vuelve seguro lo que no lo es).
/// </summary>
public class AccessReviewDecisionsIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly AccessRunRef Run = new(9, 6, "ok", Now, Now, null, null);

    private const string OwnerDef = "/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/8e3af657";

    private static AccessAssignmentRow Row(
        string pid, string? roleClass = "owner", string scope = "/", string scopeLevel = "root",
        string roleDef = OwnerDef, string? displayName = "Ana", bool? enabled = true, string? mfa = "enabled") =>
        new("s1", "Sub", null, scope, scopeLevel, "Owner", roleDef, pid, "User",
            displayName, $"{pid}@x.com", "Member", null, null, enabled, Now.AddDays(-1), mfa, roleClass, false);

    private static AccessDecision Decision(
        string pid, string decision, string scope = "/", string roleDef = OwnerDef,
        int runsSince = 0, string? note = null) =>
        new(AccessReviewAccessKey.For(pid, roleDef, scope),
            pid, AccessReviewRoleClassifier.RoleKey(roleDef), scope, null,
            decision, note, "consultor@bit.ec", Now.AddDays(-10), 8, runsSince);

    private static Dictionary<string, AccessDecision> Decisions(params AccessDecision[] ds) =>
        ds.ToDictionary(d => d.AccessKey, d => d, StringComparer.OrdinalIgnoreCase);

    private static (IReadOnlyList<AccessAccountRow> Accounts, AccessReviewKpis Kpis, IReadOnlyList<AccessFinding> Findings)
        Compute(IEnumerable<AccessAssignmentRow> assignments, IReadOnlyDictionary<string, AccessDecision>? decisions = null)
    {
        var snapshot = new AccessReviewSnapshot(Run,
            [new AccessCredStatus(1, "cred", "ok", "ok", null)],
            [.. assignments], [], []);
        decisions ??= new Dictionary<string, AccessDecision>();
        var accounts = AccessReviewAccountBuilder.Build(snapshot, decisions);
        var kpis = AccessReviewKpiCalculator.Compute(snapshot, accounts, 90, Now, decisions);
        var findings = AccessReviewFindingsBuilder.Build(snapshot, accounts, kpis, 90, Now, decisions);
        return (accounts, kpis, findings);
    }

    private static AccessFinding Get(IReadOnlyList<AccessFinding> f, string key) =>
        Assert.Single(f.Where(x => x.Key == key));

    [Fact]
    public void Justificado_baja_el_conteo_del_hallazgo()
    {
        var sin = Compute([Row("u1"), Row("u2")]);
        Assert.Equal(2, Get(sin.Findings, "owner_en_raiz").AffectedAccounts);

        var con = Compute([Row("u1"), Row("u2")],
            Decisions(Decision("u1", AccessDecisionValues.Justificado, note: "Cuenta break-glass documentada")));

        Assert.Equal(1, Get(con.Findings, "owner_en_raiz").AffectedAccounts);
        Assert.Equal(["u2"], Get(con.Findings, "owner_en_raiz").AffectedPrincipals);
    }

    [Fact]
    public void Revocar_y_mantener_no_bajan_el_conteo()
    {
        var f = Compute([Row("u1"), Row("u2")],
            Decisions(Decision("u1", AccessDecisionValues.Revocar),
                      Decision("u2", AccessDecisionValues.Mantener)));

        // Prometer revocar no revoca, y marcar revisado no vuelve seguro lo que no lo es.
        Assert.Equal(2, Get(f.Findings, "owner_en_raiz").AffectedAccounts);
    }

    [Fact]
    public void Una_cuenta_sale_del_hallazgo_solo_si_todos_sus_accesos_estan_justificados()
    {
        var parcial = Compute([
            Row("u1", scope: "/"),
            Row("u1", scope: "/subscriptions/s1", scopeLevel: "management_group"),
        ], Decisions(Decision("u1", AccessDecisionValues.Justificado, scope: "/")));

        // Le queda un acceso sin justificar: la cuenta sigue en el hallazgo.
        Assert.Equal(1, Get(parcial.Findings, "owner_en_raiz").AffectedAccounts);

        var total = Compute([
            Row("u1", scope: "/"),
            Row("u1", scope: "/subscriptions/s1", scopeLevel: "management_group"),
        ], Decisions(
            Decision("u1", AccessDecisionValues.Justificado, scope: "/"),
            Decision("u1", AccessDecisionValues.Justificado, scope: "/subscriptions/s1")));

        Assert.Equal(0, Get(total.Findings, "owner_en_raiz").AffectedAccounts);
    }

    [Fact]
    public void Revocacion_incumplida_dispara_con_decision_de_una_corrida_anterior()
    {
        var f = Compute([Row("u1"), Row("u2")],
            Decisions(Decision("u1", AccessDecisionValues.Revocar, runsSince: 2)));

        var hallazgo = Get(f.Findings, "revocacion_incumplida");
        Assert.Equal(AccessFindingSeverity.Alta, hallazgo.Severity);
        Assert.Equal(["u1"], hallazgo.AffectedPrincipals);
        Assert.Contains("2", hallazgo.Detail);
    }

    [Fact]
    public void Revocacion_incumplida_no_dispara_si_se_decidio_en_esta_corrida()
    {
        // runsSince = 0: todavía no hubo oportunidad de cumplirlo.
        var f = Compute([Row("u1")], Decisions(Decision("u1", AccessDecisionValues.Revocar, runsSince: 0)));

        Assert.Equal(0, Get(f.Findings, "revocacion_incumplida").AffectedAccounts);
    }

    [Fact]
    public void Revocacion_incumplida_no_dispara_si_el_acceso_ya_no_existe()
    {
        // La decisión sigue en BD, pero el acceso desapareció: la revocación se cumplió.
        var f = Compute([Row("u2")],
            Decisions(Decision("u1", AccessDecisionValues.Revocar, runsSince: 3)));

        Assert.Equal(0, Get(f.Findings, "revocacion_incumplida").AffectedAccounts);
    }

    [Fact]
    public void Un_hallazgo_de_umbral_aceptado_deja_de_estar_abierto()
    {
        var aceptado = new AccessDecision(
            AccessReviewAccessKey.ForFinding("granularidad_recurso"), "", "", "", "granularidad_recurso",
            AccessDecisionValues.Justificado, "Arquitectura heredada, plan a 6 meses",
            "consultor@bit.ec", Now.AddDays(-3), 8, 1);

        var f = Compute([
            Row("u1", scopeLevel: "resource", scope: "/subscriptions/s1/rg/x"),
            Row("u2", scopeLevel: "resource", scope: "/subscriptions/s1/rg/y"),
        ], Decisions(aceptado));

        var hallazgo = Get(f.Findings, "granularidad_recurso");
        Assert.True(hallazgo.Accepted);
        Assert.Equal("consultor@bit.ec", hallazgo.AcceptedBy);
        Assert.Contains("Arquitectura heredada", hallazgo.AcceptedNote);
    }

    [Fact]
    public void Pendientes_de_revisar_cuenta_accesos_elevados_sin_decision()
    {
        var f = Compute([
            Row("u1"),                                   // elevado, sin decisión → pendiente
            Row("u2"),                                   // elevado, con decisión → no cuenta
            Row("u3", roleClass: "lectura"),             // sin privilegio → no cuenta
        ], Decisions(Decision("u2", AccessDecisionValues.Mantener)));

        Assert.Equal(1, f.Kpis.PendientesDeRevisar);
    }

    [Fact]
    public void Resumen_de_decisiones_por_cuenta()
    {
        var f = Compute([
            Row("u1", scope: "/"),
            Row("u1", scope: "/subscriptions/s1", scopeLevel: "management_group"),
            Row("u1", scope: "/subscriptions/s2", scopeLevel: "subscription"),
        ], Decisions(
            Decision("u1", AccessDecisionValues.Justificado, scope: "/"),
            Decision("u1", AccessDecisionValues.Revocar, scope: "/subscriptions/s1")));

        var cuenta = Assert.Single(f.Accounts);
        Assert.Equal(1, cuenta.Decisions!.Justificado);
        Assert.Equal(1, cuenta.Decisions.Revocar);
        Assert.Equal(0, cuenta.Decisions.Mantener);
        Assert.Equal(1, cuenta.Decisions.Pendientes);
    }

    [Fact]
    public void Sin_decisiones_todo_queda_pendiente_y_nada_cambia()
    {
        var f = Compute([Row("u1"), Row("u2")]);

        Assert.Equal(2, Get(f.Findings, "owner_en_raiz").AffectedAccounts);
        Assert.Equal(0, Get(f.Findings, "revocacion_incumplida").AffectedAccounts);
        Assert.All(f.Accounts, a => Assert.Equal(1, a.Decisions!.Pendientes));
    }
}
