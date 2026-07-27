using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public class AccessReviewAccountBuilderTests
{
    private static AccessReviewSnapshot Snap(IEnumerable<AccessAssignmentRow> assignments,
        string status = "ok", string graphStatus = "ok") =>
        new(new AccessRunRef(1, 7, status, null, null, null, null),
            [new AccessCredStatus(1, "cred", "ok", graphStatus, null)],
            [.. assignments], [], []);

    private static AccessAssignmentRow Row(
        string principal, string type = "User", string? roleClass = "lectura",
        string scope = "/subscriptions/s1", string scopeLevel = "subscription", string roleDef = "def-1",
        string? displayName = "Ana Perez", string? login = "ana@x.com", string? userType = "Member",
        string? viaGroupId = null, string sub = "s1") =>
        new(sub, $"Sub {sub}", null, scope, scopeLevel, "Rol", roleDef, principal, type,
            displayName, login, userType, viaGroupId, viaGroupId is null ? null : "Grupo",
            true, null, "enabled", roleClass, false);

    [Fact]
    public void Agrupa_por_principal_y_cuenta_asignaciones_efectivas()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", scope: "/subscriptions/s1"),
            Row("u1", scope: "/subscriptions/s1/resourceGroups/rg-a", scopeLevel: "resource_group"),
            Row("u1", roleDef: "def-2"),
            Row("u2", displayName: "Beto Diaz", login: "beto@x.com"),
            Row("u2", roleDef: "def-2", displayName: "Beto Diaz", login: "beto@x.com"),
        ]));

        Assert.Equal(2, accounts.Count);
        Assert.Equal(3, accounts.Single(a => a.PrincipalObjectId == "u1").TotalAssignments);
        Assert.Equal(2, accounts.Single(a => a.PrincipalObjectId == "u2").TotalAssignments);
    }

    [Fact]
    public void Deduplica_directo_y_via_grupo_en_el_mismo_scope()
    {
        // Sin dedup, la expansión de grupos infla el conteo: la misma potestad se contaría dos veces.
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1"),
            Row("u1", viaGroupId: "g1"),
        ]));

        var cuenta = Assert.Single(accounts);
        Assert.Equal(1, cuenta.TotalAssignments);
        Assert.Equal("ambos", cuenta.Via);
    }

    [Fact]
    public void Deduplica_la_misma_asignacion_heredada_reportada_por_varias_suscripciones()
    {
        // Caso real (E2E BANCO DELTA): ARM devuelve las asignaciones de root y de management group
        // una vez por suscripción consultada, prefijando el roleDefinitionId con esa suscripción.
        // Es el MISMO Owner en el MISMO scope: una sola potestad efectiva, no tres.
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", roleClass: "owner", scope: "/", scopeLevel: "root", sub: "s1",
                roleDef: "/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/8e3af657"),
            Row("u1", roleClass: "owner", scope: "/", scopeLevel: "root", sub: "s2",
                roleDef: "/subscriptions/s2/providers/Microsoft.Authorization/roleDefinitions/8e3af657"),
            Row("u1", roleClass: "owner", scope: "/", scopeLevel: "root", sub: "s3",
                roleDef: "/subscriptions/s3/providers/Microsoft.Authorization/roleDefinitions/8e3af657"),
        ]));

        var c = Assert.Single(accounts);
        Assert.Equal(1, c.TotalAssignments);
        Assert.Equal(1, c.Owner);
        // El alcance sí es de 3 suscripciones: un Owner en root las toca todas.
        Assert.Equal(3, c.Subscriptions);
        Assert.Equal("root", c.BroadestScopeLevel);
    }

    [Fact]
    public void Roles_distintos_con_el_mismo_scope_no_se_colapsan()
    {
        // Guarda del cambio anterior: normalizar el id no debe fusionar roles realmente distintos.
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", roleClass: "owner", roleDef: "/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/aaa"),
            Row("u1", roleClass: "lectura", roleDef: "/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/bbb"),
        ]));

        Assert.Equal(2, Assert.Single(accounts).TotalAssignments);
    }

    [Fact]
    public void Via_es_grupo_cuando_solo_hereda()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([Row("u1", viaGroupId: "g1")]));

        Assert.Equal("grupo", Assert.Single(accounts).Via);
    }

    [Fact]
    public void Via_es_directo_cuando_no_hereda()
    {
        Assert.Equal("directo", Assert.Single(AccessReviewAccountBuilder.Build(Snap([Row("u1")]))).Via);
    }

    [Fact]
    public void Cuenta_por_clase_de_privilegio()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", roleClass: "owner", roleDef: "d1"),
            Row("u1", roleClass: "otorga_accesos", roleDef: "d2"),
            Row("u1", roleClass: "escritura_total", roleDef: "d3"),
            Row("u1", roleClass: "escritura_servicio", roleDef: "d4"),
            Row("u1", roleClass: "lectura", roleDef: "d5"),
            Row("u1", roleClass: null, roleDef: "d6"),
        ]));

        var c = Assert.Single(accounts);
        Assert.Equal(6, c.TotalAssignments);
        Assert.Equal(1, c.Owner);
        Assert.Equal(1, c.OtorgaAccesos);
        Assert.Equal(1, c.EscrituraTotal);
        Assert.Equal(1, c.EscrituraServicio);
        Assert.Equal(1, c.Lectura);
        Assert.Equal(1, c.SinClasificar);
    }

    [Fact]
    public void Cuenta_suscripciones_distintas()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", sub: "s1", scope: "/subscriptions/s1"),
            Row("u1", sub: "s1", scope: "/subscriptions/s1", roleDef: "d2"),
            Row("u1", sub: "s2", scope: "/subscriptions/s2"),
        ]));

        Assert.Equal(2, Assert.Single(accounts).Subscriptions);
    }

    [Fact]
    public void Scope_mas_amplio_gana()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", scope: "/subscriptions/s1/resourceGroups/rg/providers/x/y", scopeLevel: "resource"),
            Row("u1", scope: "/", scopeLevel: "root", roleDef: "d2"),
            Row("u1", scope: "/subscriptions/s1", roleDef: "d3"),
        ]));

        Assert.Equal("root", Assert.Single(accounts).BroadestScopeLevel);
    }

    [Fact]
    public void Scope_mas_amplio_respeta_el_orden_completo()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", scopeLevel: "resource", roleDef: "d1"),
            Row("u1", scopeLevel: "resource_group", roleDef: "d2"),
            Row("u2", scopeLevel: "management_group", roleDef: "d1", displayName: "Beto"),
            Row("u2", scopeLevel: "subscription", roleDef: "d2", displayName: "Beto"),
        ]));

        Assert.Equal("resource_group", accounts.Single(a => a.PrincipalObjectId == "u1").BroadestScopeLevel);
        Assert.Equal("management_group", accounts.Single(a => a.PrincipalObjectId == "u2").BroadestScopeLevel);
    }

    [Fact]
    public void El_grupo_es_una_cuenta_propia_y_sus_miembros_tambien()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("g1", type: "Group", displayName: "Grupo Lectores", login: null, userType: null),
            Row("u1", viaGroupId: "g1"),
        ]));

        Assert.Equal(2, accounts.Count);
        Assert.Equal("Group", accounts.Single(a => a.PrincipalObjectId == "g1").PrincipalType);
        Assert.Equal("grupo", accounts.Single(a => a.PrincipalObjectId == "u1").Via);
    }

    [Fact]
    public void Externa_por_ext_en_el_login()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", login: "juan_proveedor.com#EXT#@contoso.onmicrosoft.com"),
        ]));

        Assert.True(Assert.Single(accounts).IsExternal);
    }

    [Fact]
    public void Externa_por_user_type_guest()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([Row("u1", userType: "Guest")]));

        Assert.True(Assert.Single(accounts).IsExternal);
    }

    [Fact]
    public void Interna_cuando_hay_directorio_y_no_es_invitada()
    {
        Assert.False(Assert.Single(AccessReviewAccountBuilder.Build(Snap([Row("u1")]))).IsExternal);
    }

    [Fact]
    public void Foreign_group_es_externo()
    {
        // Su membresía se administra en otro tenant: por definición es acceso externo.
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("fg1", type: "ForeignGroup", displayName: null, login: null, userType: null),
        ]));

        Assert.True(Assert.Single(accounts).IsExternal);
    }

    [Fact]
    public void Tipos_sin_upn_quedan_fuera_del_eje_externo()
    {
        // Un SP multi-tenant no se distingue por UPN, y un Device o un principal sin tipo tampoco:
        // "n/d" es más honesto que afirmar "interna".
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("sp1", type: "ServicePrincipal", displayName: "App", login: "appid", userType: null),
            Row("d1", type: "Device", displayName: null, login: null, userType: null),
            Row("x1", type: "Unknown", displayName: null, login: null, userType: null),
        ]));

        Assert.All(accounts, a => Assert.Null(a.IsExternal));
    }

    [Fact]
    public void Sin_graph_el_eje_externo_es_nulo()
    {
        // Sin Graph solo hay object IDs: no hay UPN que mirar, así que afirmar "interna" sería falso.
        var accounts = AccessReviewAccountBuilder.Build(
            Snap([Row("u1", login: null, userType: null)], graphStatus: "sin_consent"));

        Assert.Null(Assert.Single(accounts).IsExternal);
    }

    [Fact]
    public void Huerfana_solo_para_tipos_del_tenant_y_con_graph_completo()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", displayName: null, login: null),
            Row("fg1", type: "ForeignGroup", displayName: null, login: null, userType: null),
            Row("d1", type: "Device", displayName: null, login: null, userType: null),
        ]));

        Assert.True(accounts.Single(a => a.PrincipalObjectId == "u1").Orphan);
        Assert.False(accounts.Single(a => a.PrincipalObjectId == "fg1").Orphan);
        Assert.False(accounts.Single(a => a.PrincipalObjectId == "d1").Orphan);
    }

    [Fact]
    public void Sin_graph_nada_se_marca_como_huerfano()
    {
        // Nombre vacío con Graph incompleto significa "no resuelto", no "eliminado de Entra ID".
        var accounts = AccessReviewAccountBuilder.Build(
            Snap([Row("u1", displayName: null, login: null)], graphStatus: "sin_consent"));

        Assert.False(Assert.Single(accounts).Orphan);
    }

    [Fact]
    public void Orden_por_riesgo()
    {
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", roleClass: "lectura", displayName: "Solo lectura"),
            Row("u2", roleClass: "otorga_accesos", displayName: "Otorga", roleDef: "d2"),
            Row("u3", roleClass: "owner", displayName: "Dueño", roleDef: "d3"),
        ]));

        Assert.Equal(["u3", "u2", "u1"], accounts.Select(a => a.PrincipalObjectId));
    }

    [Fact]
    public void Toma_el_primer_dato_de_directorio_disponible_entre_sus_filas()
    {
        // La fila directa puede no haber resuelto y la derivada sí (o al revés): la cuenta debe
        // quedarse con el dato que exista, no con el de la primera fila.
        var accounts = AccessReviewAccountBuilder.Build(Snap([
            Row("u1", displayName: null, login: null, userType: null),
            Row("u1", roleDef: "d2", displayName: "Ana Perez", login: "ana@x.com", userType: "Member"),
        ]));

        var c = Assert.Single(accounts);
        Assert.Equal("Ana Perez", c.DisplayName);
        Assert.Equal("ana@x.com", c.Login);
        Assert.False(c.Orphan);
    }

    [Fact]
    public void GraphComplete_es_falso_si_la_corrida_fallo_o_una_credencial_no_leyo_graph()
    {
        Assert.True(AccessReviewAccountBuilder.GraphComplete(Snap([])));
        Assert.True(AccessReviewAccountBuilder.GraphComplete(Snap([], graphStatus: "sin_licencia_p1")));
        Assert.False(AccessReviewAccountBuilder.GraphComplete(Snap([], status: "error")));
        Assert.False(AccessReviewAccountBuilder.GraphComplete(Snap([], graphStatus: "sin_consent")));
        Assert.False(AccessReviewAccountBuilder.GraphComplete(Snap([], graphStatus: "no_aplica")));
        Assert.False(AccessReviewAccountBuilder.GraphComplete(Snap([], graphStatus: "error")));
    }
}
