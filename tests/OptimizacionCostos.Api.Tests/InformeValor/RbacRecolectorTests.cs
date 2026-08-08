using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican las funciones de mapeo puras y, para <see cref="RbacRecolector.LeerAsync"/>,
/// un <see cref="IAccessReviewStore"/> falso en memoria (mismo patrón que <c>FakeStore</c> de
/// AccessReviewSyncServiceTests). La deduplicación en sí (<see cref="AccessReviewAssignments.Distinct"/>)
/// ya tiene su propia razón de ser documentada en ese módulo: acá se prueba que
/// <see cref="RbacRecolector"/> la reciba intacta desde <c>GetSnapshotAsync</c> y no la reimplemente.
/// </summary>
public sealed class RbacRecolectorTests
{
    private const string RoleGuid = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";

    private static string RoleDefPrefixadoPor(string sub) =>
        $"/subscriptions/{sub}/providers/Microsoft.Authorization/roleDefinitions/{RoleGuid}";

    private static AccessAssignmentRow Row(
        string principal = "u1", string type = "User", string scope = "/subscriptions/s1",
        string scopeLevel = "subscription", string roleDef = "def-1", string roleName = "Rol",
        string? displayName = "Ana Perez", string? login = "ana@x.com",
        string? viaGroupId = null, string sub = "s1", string? subName = null,
        bool? accountEnabled = true, DateTimeOffset? lastSignIn = null,
        string? roleClass = null, bool isCustomRole = false) =>
        new(sub, subName ?? $"Sub {sub}", null, scope, scopeLevel, roleName, roleDef, principal, type,
            displayName, login, "Member", viaGroupId, viaGroupId is null ? null : "Grupo",
            accountEnabled, lastSignIn, "enabled", roleClass, isCustomRole);

    private static AccessReviewSnapshot Snap(IEnumerable<AccessAssignmentRow> assignments) =>
        new(new AccessRunRef(1, 7, "ok", null, null, null, null),
            [new AccessCredStatus(1, "cred", "ok", "ok", null)],
            [.. assignments], [], []);

    /// <summary>
    /// ARM repite cada asignación heredada una vez por suscripción consultada. Medido en un
    /// cliente real: 6013 filas crudas con 1068 duplicados de 124 asignaciones, o sea un 21%
    /// de sobreconteo. La clave de deduplicación no incluye la suscripción a propósito.
    /// </summary>
    [Fact]
    public void Deduplica_la_misma_asignacion_repetida_por_suscripcion()
    {
        var crudas = new[]
        {
            Row(scope: "/", scopeLevel: "root", sub: "s1", roleDef: RoleDefPrefixadoPor("s1")),
            Row(scope: "/", scopeLevel: "root", sub: "s2", roleDef: RoleDefPrefixadoPor("s2")),
            Row(scope: "/", scopeLevel: "root", sub: "s3", roleDef: RoleDefPrefixadoPor("s3")),
        };

        // Camino recomendado por el brief: no reimplementar la deduplicación, reusar la misma que
        // ya corre dentro de GetSnapshotAsync al leer (AccessReviewAssignments.Distinct) y
        // proyectar el resultado. Un SELECT plano sobre las 3 filas crudas daría 3 RbacFila.
        var efectivas = AccessReviewAssignments.Distinct(crudas);
        var filas = RbacRecolector.Mapear(Snap(efectivas));

        Assert.Single(filas);
    }

    [Fact]
    public void El_role_key_es_el_ultimo_segmento_del_id_de_rol()
    {
        var fila = RbacRecolector.MapearFila(Row(roleDef: RoleDefPrefixadoPor("s1")));

        Assert.Equal(RoleGuid, fila.RoleKey);
    }

    /// <summary>Y no la primera alfabéticamente, que es lo que quedaría tras deduplicar sin
    /// llevar el conjunto completo (caso real: una asignación de root que alcanzaba 29
    /// suscripciones perdía 28 si solo se conservaba la de la fila que ganó el dedup).</summary>
    [Fact]
    public void Una_asignacion_de_root_devuelve_todas_las_suscripciones_alcanzadas()
    {
        var crudas = new[]
        {
            // Suscripciones a propósito fuera de orden alfabético en la lista de entrada.
            Row(scope: "/", scopeLevel: "root", sub: "s3", subName: "Zulu", roleDef: RoleDefPrefixadoPor("s3")),
            Row(scope: "/", scopeLevel: "root", sub: "s1", subName: "Alfa", roleDef: RoleDefPrefixadoPor("s1")),
            Row(scope: "/", scopeLevel: "root", sub: "s2", subName: "Bravo", roleDef: RoleDefPrefixadoPor("s2")),
        };

        var efectivas = AccessReviewAssignments.Distinct(crudas);
        var fila = Assert.Single(RbacRecolector.Mapear(Snap(efectivas)));

        Assert.Equal(["s1", "s2", "s3"], fila.SuscripcionesAlcanzadas.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("root", fila.ScopeLevel);
    }

    /// <summary>Guarda de la regla anterior: fuera de root/management_group, el alcance es la
    /// suscripción propia, no "todas las que vio la corrida".</summary>
    [Fact]
    public void Una_asignacion_de_suscripcion_devuelve_solo_su_propia_suscripcion()
    {
        var fila = RbacRecolector.MapearFila(Row(scope: "/subscriptions/s1", scopeLevel: "subscription", sub: "s1"));

        Assert.Equal(["s1"], fila.SuscripcionesAlcanzadas);
    }

    [Fact]
    public void El_estado_de_cuenta_viaja_como_booleano_y_no_como_texto()
    {
        var p = typeof(RbacFila).GetProperty("CuentaHabilitada");
        Assert.Equal(typeof(bool?), p!.PropertyType);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void El_booleano_de_cuenta_pasa_intacto(bool? habilitada)
    {
        var fila = RbacRecolector.MapearFila(Row(accountEnabled: habilitada));

        Assert.Equal(habilitada, fila.CuentaHabilitada);
    }

    [Fact]
    public void Mapea_los_campos_de_identidad_y_alcance_tal_cual()
    {
        var fila = RbacRecolector.MapearFila(Row(
            principal: "u9", type: "Group", scope: "/subscriptions/s1/resourceGroups/rg",
            scopeLevel: "resource_group", roleName: "Colaborador", displayName: "Grupo X",
            login: "grupo@x.com", viaGroupId: "g1", sub: "s1", subName: "Sub Uno"));

        Assert.Equal("u9", fila.PrincipalObjectId);
        Assert.Equal("Grupo X", fila.Nombre);
        Assert.Equal("grupo@x.com", fila.Login);
        Assert.Equal("Group", fila.PrincipalType);
        Assert.Equal("Colaborador", fila.Rol);
        Assert.Equal("/subscriptions/s1/resourceGroups/rg", fila.Scope);
        Assert.Equal("resource_group", fila.ScopeLevel);
        Assert.Equal("s1", fila.SubscriptionId);
        Assert.Equal("Sub Uno", fila.SubscriptionName);
        Assert.Equal("g1", fila.ViaGrupoId);
    }

    /// <summary>
    /// IMPORTANTE 3 de la revisión de rama: sin estos dos campos, la calculadora tendría que
    /// portar el regex de la plantilla sobre el nombre del rol en inglés en vez de reusar la
    /// clasificación por permisos reales que ya hace Revisión de accesos
    /// (AccessReviewRoleClassifier) — y contradecirla justo en los roles personalizados.
    /// </summary>
    [Fact]
    public void Mapea_la_clasificacion_de_rol_y_si_es_personalizado()
    {
        var fila = RbacRecolector.MapearFila(Row(
            roleClass: AccessReviewRoleClassifier.OtorgaAccesos, isCustomRole: true));

        Assert.Equal(AccessReviewRoleClassifier.OtorgaAccesos, fila.RoleClass);
        Assert.True(fila.IsCustomRole);
    }

    /// <summary>Rol no resoluble en la suscripción (o corrida vieja, anterior a que se guardara la
    /// clasificación): RoleClass viaja null, no una cadena vacía ni un valor inventado.</summary>
    [Fact]
    public void Sin_clasificacion_de_rol_RoleClass_es_nulo_y_no_es_personalizado_por_defecto()
    {
        var fila = RbacRecolector.MapearFila(Row());

        Assert.Null(fila.RoleClass);
        Assert.False(fila.IsCustomRole);
    }

    [Fact]
    public void Sin_ultimo_login_el_texto_es_nulo()
    {
        var fila = RbacRecolector.MapearFila(Row(lastSignIn: null));

        Assert.Null(fila.UltimoLoginTexto);
    }

    /// <summary>Texto crudo (ISO 8601 en UTC), no una frase relativa: la misma filosofía que
    /// EsfuerzoTexto en la matriz. Formatear o clasificar "hace N días" es decisión de la
    /// calculadora, que conoce la fecha de corte del informe.</summary>
    [Fact]
    public void Con_ultimo_login_el_texto_es_la_fecha_en_utc_formato_iso()
    {
        var login = new DateTimeOffset(2026, 7, 15, 10, 22, 0, TimeSpan.Zero);
        var fila = RbacRecolector.MapearFila(Row(lastSignIn: login));

        Assert.Equal("2026-07-15T10:22:00.0000000Z", fila.UltimoLoginTexto);
    }

    // ── LeerAsync: la orquestación real (última corrida finalizada -> snapshot -> mapeo) ──

    private sealed class FakeAccessReviewStore(AccessRunRef? run, AccessReviewSnapshot? snapshot) : IAccessReviewStore
    {
        public Task<int> CreateRunAsync(int clientId, string? requestedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkRunningAsync(int runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkFinishedAsync(int runId, string status, string? error, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsRunActiveAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> MarkOrphanedRunningAsFailedAsync(string error, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveResultsAsync(int runId, IReadOnlyList<AccessAssignmentRow> a, IReadOnlyList<AccessGuestRow> g,
            IReadOnlyList<AccessGlobalAdminRow> ga, IReadOnlyList<AccessCredStatus> cs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccessRunRef?> GetLatestRunAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AccessRunRef>> ListRunsAsync(int clientId, int top = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccessRunRef?> GetPreviousFinishedRunAsync(int clientId, int beforeRunId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AccessRunRef?> GetLatestFinishedRunAsync(int clientId, CancellationToken ct = default) => Task.FromResult(run);
        public Task<AccessReviewSnapshot?> GetSnapshotAsync(int runId, CancellationToken ct = default) => Task.FromResult(snapshot);
    }

    [Fact]
    public async Task LeerAsync_sin_corrida_finalizada_devuelve_vacio()
    {
        var store = new FakeAccessReviewStore(run: null, snapshot: null);

        var filas = await RbacRecolector.LeerAsync(store, clientId: 7);

        Assert.Empty(filas);
    }

    [Fact]
    public async Task LeerAsync_arma_las_filas_desde_la_ultima_corrida_finalizada()
    {
        var run = new AccessRunRef(42, 7, "partial", null, null, null, null);
        var snapshot = Snap([Row(principal: "u1"), Row(principal: "u2", roleDef: "def-2")]);
        var store = new FakeAccessReviewStore(run, snapshot);

        var filas = await RbacRecolector.LeerAsync(store, clientId: 7);

        Assert.Equal(2, filas.Count);
    }
}
