using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public class AccessReviewDeltaBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);

    private static AccessAssignmentRow Row(string pid, string roleDef = "/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/8e3af657",
        string scope = "/subscriptions/s1", string? roleClass = "owner", string sub = "s1", string subName = "SAPPRD") =>
        new(sub, subName, null, scope, "subscription", "Owner", roleDef, pid, "User",
            $"N {pid}", $"{pid}@x.com", "Member", null, null, true, Now, "enabled", roleClass, false);

    private static AccessReviewSnapshot Snap(int runId, IEnumerable<AccessAssignmentRow> rows,
        IEnumerable<AccessGlobalAdminRow>? admins = null, IEnumerable<AccessGuestRow>? guests = null) =>
        new(new AccessRunRef(runId, 6, "ok", Now, Now, null, null),
            [new AccessCredStatus(1, "cred", "ok", "ok", null)],
            [.. rows], [.. guests ?? []], [.. admins ?? []]);

    private static AccessGlobalAdminRow Ga(string id) => new(id, $"Admin {id}", $"{id}@x.com", "Member", true, Now, "enabled");
    private static AccessGuestRow Guest(string id) => new(id, $"G {id}", $"{id}@ext.com", "ext.com", true, "Accepted", null, Now, null, "enabled");

    [Fact]
    public void Primera_corrida_no_tiene_delta()
    {
        var d = AccessReviewDeltaBuilder.Build(Snap(2, [Row("u1")]), null);

        Assert.False(d.HasPrevious);
        Assert.Empty(d.NuevosAccesos);
        Assert.Empty(d.AccesosRemovidos);
    }

    [Fact]
    public void Detecta_acceso_nuevo_y_removido()
    {
        var antes = Snap(1, [Row("u1"), Row("u2")]);
        var ahora = Snap(2, [Row("u1"), Row("u3")]);

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.Equal(1, d.PreviousRunId);
        Assert.Equal(["u3"], d.NuevosAccesos.Select(i => i.PrincipalObjectId));
        Assert.Equal(["u2"], d.AccesosRemovidos.Select(i => i.PrincipalObjectId));
    }

    [Fact]
    public void Sin_cambios_no_reporta_nada()
    {
        var d = AccessReviewDeltaBuilder.Build(Snap(2, [Row("u1")]), Snap(1, [Row("u1")]));

        Assert.True(d.HasPrevious);
        Assert.Empty(d.NuevosAccesos);
        Assert.Empty(d.AccesosRemovidos);
    }

    [Fact]
    public void El_mismo_acceso_heredado_con_otro_roleDefinitionId_no_es_nuevo()
    {
        // ARM prefija el roleDefinitionId con la suscripción consultada: un acceso a nivel root vuelve
        // con un id distinto por cada suscripción. Sin usar la clave del bloque 3, ese acceso
        // apareceria como "nuevo" en cada corrida y el delta seria puro ruido.
        var antes = Snap(1, [Row("u1", roleDef: "/subscriptions/aaa/providers/Microsoft.Authorization/roleDefinitions/8e3af657", scope: "/")]);
        var ahora = Snap(2, [Row("u1", roleDef: "/subscriptions/bbb/providers/Microsoft.Authorization/roleDefinitions/8e3af657", scope: "/")]);

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.Empty(d.NuevosAccesos);
        Assert.Empty(d.AccesosRemovidos);
    }

    [Fact]
    public void Un_cambio_de_rol_es_un_acceso_nuevo_y_uno_removido()
    {
        var antes = Snap(1, [Row("u1", roleDef: "/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7")]);
        var ahora = Snap(2, [Row("u1")]);   // ahora Owner

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.Single(d.NuevosAccesos);
        Assert.Single(d.AccesosRemovidos);
    }

    [Fact]
    public void Detecta_global_admins_nuevos_y_removidos()
    {
        var antes = Snap(1, [Row("u1")], admins: [Ga("a1"), Ga("a2")]);
        var ahora = Snap(2, [Row("u1")], admins: [Ga("a1"), Ga("a3")]);

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.Equal(["Admin a3"], d.NuevosGlobalAdmins);
        Assert.Equal(["Admin a2"], d.GlobalAdminsRemovidos);
    }

    [Fact]
    public void Cuenta_guests_nuevos_y_removidos()
    {
        var antes = Snap(1, [Row("u1")], guests: [Guest("g1"), Guest("g2")]);
        var ahora = Snap(2, [Row("u1")], guests: [Guest("g1"), Guest("g3"), Guest("g4")]);

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.Equal(2, d.NuevosGuests);
        Assert.Equal(1, d.GuestsRemovidos);
    }

    [Fact]
    public void Ordena_lo_elevado_primero()
    {
        var antes = Snap(1, []);
        var ahora = Snap(2, [
            Row("lector", roleClass: "lectura", scope: "/subscriptions/s1/rg/a"),
            Row("dueno", roleClass: "owner", scope: "/subscriptions/s1/rg/b"),
        ]);

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.Equal("dueno", d.NuevosAccesos[0].PrincipalObjectId);
    }

    [Fact]
    public void Clasifica_el_ambiente_de_cada_acceso_nuevo()
    {
        var d = AccessReviewDeltaBuilder.Build(
            Snap(2, [Row("u1", subName: "SAPPRD"), Row("u2", subName: "AnaliticaDEV", scope: "/subscriptions/s2")]),
            Snap(1, []));

        Assert.Contains(AccessReviewEnvironment.Produccion, d.NuevosAccesos.Select(i => i.Environment));
        Assert.Contains(AccessReviewEnvironment.Desarrollo, d.NuevosAccesos.Select(i => i.Environment));
    }
}
