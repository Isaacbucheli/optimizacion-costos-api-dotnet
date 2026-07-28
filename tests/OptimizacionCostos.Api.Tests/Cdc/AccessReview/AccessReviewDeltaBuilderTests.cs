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
        IEnumerable<AccessGlobalAdminRow>? admins = null, IEnumerable<AccessGuestRow>? guests = null,
        string arm = "ok", string graph = "ok") =>
        new(new AccessRunRef(runId, 6, "ok", Now, Now, null, null),
            [new AccessCredStatus(1, "cred", arm, graph, null)],
            [.. rows], [.. guests ?? []], [.. admins ?? []]);

    private static AccessGlobalAdminRow Ga(string id) => new(id, $"Admin {id}", $"{id}@x.com", "Member", true, Now, "enabled");
    private static AccessGuestRow Guest(string id) => new(id, $"G {id}", $"{id}@ext.com", "ext.com", true, "Accepted", null, Now, null, "enabled");

    [Fact]
    public void Primera_corrida_no_tiene_delta()
    {
        var d = AccessReviewDeltaBuilder.Build(Snap(2, [Row("u1")]), null);

        Assert.False(d.HasPrevious);
        // Null, no vacío: sin corrida anterior no hay nada que comparar, y una lista vacía se leería
        // como "verificamos y no cambió nada".
        Assert.Null(d.NuevosAccesos);
        Assert.Null(d.AccesosRemovidos);
        Assert.False(d.AccesosComparables);
        Assert.False(d.DirectorioComparable);
    }

    [Fact]
    public void Detecta_acceso_nuevo_y_removido()
    {
        var antes = Snap(1, [Row("u1"), Row("u2")]);
        var ahora = Snap(2, [Row("u1"), Row("u3")]);

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.Equal(1, d.PreviousRunId);
        Assert.Equal(["u3"], d.NuevosAccesos!.Select(i => i.PrincipalObjectId));
        Assert.Equal(["u2"], d.AccesosRemovidos!.Select(i => i.PrincipalObjectId));
    }

    [Fact]
    public void Sin_cambios_no_reporta_nada()
    {
        var d = AccessReviewDeltaBuilder.Build(Snap(2, [Row("u1")]), Snap(1, [Row("u1")]));

        Assert.True(d.HasPrevious);
        Assert.Empty(d.NuevosAccesos!);
        Assert.Empty(d.AccesosRemovidos!);
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

        Assert.Empty(d.NuevosAccesos!);
        Assert.Empty(d.AccesosRemovidos!);
    }

    [Fact]
    public void Un_cambio_de_rol_es_un_acceso_nuevo_y_uno_removido()
    {
        var antes = Snap(1, [Row("u1", roleDef: "/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7")]);
        var ahora = Snap(2, [Row("u1")]);   // ahora Owner

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.Single(d.NuevosAccesos!);
        Assert.Single(d.AccesosRemovidos!);
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

        Assert.Equal("dueno", d.NuevosAccesos![0].PrincipalObjectId);
    }

    [Fact]
    public void Clasifica_el_ambiente_de_cada_acceso_nuevo()
    {
        var d = AccessReviewDeltaBuilder.Build(
            Snap(2, [Row("u1", subName: "SAPPRD"), Row("u2", subName: "AnaliticaDEV", scope: "/subscriptions/s2")]),
            Snap(1, []));

        Assert.Contains(AccessReviewEnvironment.Produccion, d.NuevosAccesos!.Select(i => i.Environment));
        Assert.Contains(AccessReviewEnvironment.Desarrollo, d.NuevosAccesos!.Select(i => i.Environment));
    }

    // ── Comparabilidad: un insumo parcial no produce altas ni bajas ───────────────
    // El caso peor era la corrida ANTERIOR parcial con la actual en ok: no había banner que avisara
    // nada y la franja imprimía en rojo "Global Admins nuevos: <todos los del tenant>" cuando nadie
    // recibió nada. El eje simplemente estaba vacío antes porque no se pudo leer el directorio.

    [Fact]
    public void Directorio_parcial_en_la_corrida_anterior_no_inventa_global_admins_nuevos()
    {
        var antes = Snap(1, [Row("u1")], admins: [], graph: "sin_consent");
        var ahora = Snap(2, [Row("u1")], admins: [Ga("a1"), Ga("a2")]);

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.True(d.HasPrevious);
        Assert.False(d.DirectorioComparable);
        Assert.Null(d.NuevosGlobalAdmins);
        Assert.Null(d.GlobalAdminsRemovidos);
        Assert.Null(d.NuevosGuests);
        Assert.Null(d.GuestsRemovidos);
    }

    [Fact]
    public void Directorio_parcial_no_afecta_al_eje_de_accesos()
    {
        // Los ejes son independientes: ARM se leyó bien en las dos corridas, así que las altas y bajas
        // de accesos siguen siendo afirmables aunque el directorio no lo sea.
        var antes = Snap(1, [Row("u1")], graph: "error");
        var ahora = Snap(2, [Row("u1"), Row("u2")]);

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.True(d.AccesosComparables);
        Assert.Equal(["u2"], d.NuevosAccesos!.Select(i => i.PrincipalObjectId));
        Assert.False(d.DirectorioComparable);
    }

    [Fact]
    public void Inventario_arm_parcial_no_inventa_accesos_nuevos_ni_removidos()
    {
        var antes = Snap(1, [Row("u1")], arm: "error");
        var ahora = Snap(2, [Row("u1"), Row("u2")]);

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.True(d.HasPrevious);
        Assert.False(d.AccesosComparables);
        Assert.Null(d.NuevosAccesos);
        Assert.Null(d.AccesosRemovidos);
        // El directorio sí se leyó completo en las dos: ese eje se mantiene.
        Assert.True(d.DirectorioComparable);
    }

    [Fact]
    public void Corrida_actual_parcial_tampoco_es_comparable()
    {
        var d = AccessReviewDeltaBuilder.Build(Snap(2, [Row("u1")], arm: "error"), Snap(1, [Row("u1"), Row("u2")]));

        Assert.False(d.AccesosComparables);
        Assert.Null(d.AccesosRemovidos);
    }

    [Fact]
    public void Sin_licencia_p1_sigue_siendo_comparable_en_el_directorio()
    {
        // Falta el último login, no el directorio: los Global Admins y los invitados se leyeron.
        var antes = Snap(1, [Row("u1")], admins: [Ga("a1")], graph: "sin_licencia_p1");
        var ahora = Snap(2, [Row("u1")], admins: [Ga("a1"), Ga("a2")], graph: "sin_licencia_p1");

        var d = AccessReviewDeltaBuilder.Build(ahora, antes);

        Assert.True(d.DirectorioComparable);
        Assert.Equal(["Admin a2"], d.NuevosGlobalAdmins);
    }

    [Fact]
    public void Corrida_con_status_error_no_es_comparable_en_ningun_eje()
    {
        var antes = new AccessReviewSnapshot(new AccessRunRef(1, 6, "error", Now, Now, "boom", null),
            [new AccessCredStatus(1, "cred", "ok", "ok", null)], [Row("u1")], [], []);

        var d = AccessReviewDeltaBuilder.Build(Snap(2, [Row("u1"), Row("u2")]), antes);

        Assert.True(d.HasPrevious);
        Assert.False(d.AccesosComparables);
        Assert.False(d.DirectorioComparable);
        Assert.Equal(1, d.PreviousRunId);   // se sigue diciendo CONTRA qué se intentó comparar
    }
}
