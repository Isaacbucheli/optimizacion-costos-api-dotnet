using System.Collections.Concurrent;
using System.Net;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

// ── Fakes en memoria ─────────────────────────────────────────
public sealed class FakeArm : IAccessReviewArmClient
{
    public Dictionary<string, List<ArmRoleAssignment>> BySub = [];
    public HashSet<int> FailCredentials = [];
    public Task<IReadOnlyList<ArmRoleAssignment>> GetRoleAssignmentsAsync(int credentialId, string subscriptionId, CancellationToken ct = default) =>
        FailCredentials.Contains(credentialId)
            ? throw new HttpRequestException("401")
            : Task.FromResult<IReadOnlyList<ArmRoleAssignment>>(BySub.GetValueOrDefault(subscriptionId, []));
}

public sealed class FakeGraph : IAccessReviewGraphClient
{
    public GraphUserSweep Sweep = new(new Dictionary<string, GraphUser>(), true);
    public Dictionary<string, List<GraphDirectoryObject>> GroupMembers = [];
    public List<GraphDirectoryObject> GlobalAdmins = [];
    public Dictionary<string, GraphDirectoryObject> Directory = [];
    public Dictionary<string, string> Mfa = [];
    public HashSet<int> FailCredentials = [];
    public Exception? GlobalAdminsError;
    // Registro de llamadas: hay tipos de principal que NO se deben intentar resolver ni expandir.
    public List<string> GroupExpansions = [];
    public List<string> RequestedIds = [];
    // MFA se resuelve en paralelo → el registro tiene que ser seguro para concurrencia.
    public ConcurrentBag<string> MfaCalls = [];

    private void Gate(int credentialId) { if (FailCredentials.Contains(credentialId)) throw new HttpRequestException("403 consent", null, HttpStatusCode.Forbidden); }
    public Task<GraphUserSweep> SweepUsersAsync(int c, CancellationToken ct = default) { Gate(c); return Task.FromResult(Sweep); }
    public Task<IReadOnlyList<GraphDirectoryObject>> GetGroupTransitiveMembersAsync(int c, string g, CancellationToken ct = default)
        { Gate(c); GroupExpansions.Add(g); return Task.FromResult<IReadOnlyList<GraphDirectoryObject>>(GroupMembers.GetValueOrDefault(g, [])); }
    public Task<IReadOnlyList<GraphDirectoryObject>> GetGlobalAdminsAsync(int c, CancellationToken ct = default)
        { Gate(c); if (GlobalAdminsError is not null) throw GlobalAdminsError; return Task.FromResult<IReadOnlyList<GraphDirectoryObject>>(GlobalAdmins); }
    public Task<IReadOnlyDictionary<string, GraphDirectoryObject>> GetByIdsAsync(int c, IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Gate(c); RequestedIds.AddRange(ids); return Task.FromResult<IReadOnlyDictionary<string, GraphDirectoryObject>>(
            ids.Where(Directory.ContainsKey).ToDictionary(i => i, i => Directory[i])); }
    public Task<string> GetMfaStatusAsync(int c, string u, CancellationToken ct = default)
        { Gate(c); MfaCalls.Add(u); return Task.FromResult(Mfa.GetValueOrDefault(u, "unavailable")); }
}

public sealed class FakeStore : IAccessReviewStore
{
    public List<AccessAssignmentRow> Assignments = [];
    public List<AccessGuestRow> Guests = [];
    public List<AccessGlobalAdminRow> Gas = [];
    public List<AccessCredStatus> CredStatuses = [];
    public (string Status, string? Error)? Finished;

    public Task<int> CreateRunAsync(int clientId, string? actor, CancellationToken ct = default) => Task.FromResult(1);
    public Task MarkRunningAsync(int runId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkFinishedAsync(int runId, string status, string? error, CancellationToken ct = default)
        { Finished = (status, error); return Task.CompletedTask; }
    public Task<bool> IsRunActiveAsync(int clientId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<int> MarkOrphanedRunningAsFailedAsync(string error, CancellationToken ct = default) => Task.FromResult(0);
    public Task SaveResultsAsync(int runId, IReadOnlyList<AccessAssignmentRow> a, IReadOnlyList<AccessGuestRow> g,
        IReadOnlyList<AccessGlobalAdminRow> ga, IReadOnlyList<AccessCredStatus> cs, CancellationToken ct = default)
    {
        Assignments = [.. a]; Guests = [.. g]; Gas = [.. ga]; CredStatuses = [.. cs];
        return Task.CompletedTask;
    }
    public Task<AccessRunRef?> GetLatestRunAsync(int clientId, CancellationToken ct = default) => Task.FromResult<AccessRunRef?>(null);
    public Task<IReadOnlyList<AccessRunRef>> ListRunsAsync(int clientId, int top = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccessRunRef>>([]);
    public Task<AccessReviewSnapshot?> GetSnapshotAsync(int runId, CancellationToken ct = default) => Task.FromResult<AccessReviewSnapshot?>(null);
    public Task<AccessRunRef?> GetPreviousFinishedRunAsync(int clientId, int beforeRunId, CancellationToken ct = default)
        => Task.FromResult<AccessRunRef?>(null);
}

/// <summary>Subclase que fija las credenciales sin tocar SQL (seam de test).</summary>
public sealed class TestableSyncService(
    IAccessReviewArmClient arm, IAccessReviewGraphClient graph, IAccessReviewStore store,
    IReadOnlyList<AccessCredentialUnit> units)
    : AccessReviewSyncService(arm, graph, store, null!,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AccessReviewSyncService>.Instance)
{
    protected override Task<IReadOnlyList<AccessCredentialUnit>> CredentialUnitsAsync(int clientId, CancellationToken ct)
        => Task.FromResult(units);
}

public class AccessReviewSyncServiceTests
{
    private static readonly AccessCredentialUnit Cred1 = new(1, "cred-principal", "app_secret",
        [("s1", "Sub Uno", "Enabled")]);

    private static GraphUser User(string id, string? upn = null, string userType = "Member",
        bool enabled = true, DateTimeOffset? lastSignIn = null) =>
        new(id, $"User {id}", upn ?? $"{id}@x.com", null, userType, enabled, null, null, lastSignIn);

    [Fact]
    public async Task Expande_grupos_con_filas_derivadas_via_grupo()
    {
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "g1", "Group", "def-1", "Reader")] } };
        var graph = new FakeGraph
        {
            Sweep = new(new Dictionary<string, GraphUser> { ["u1"] = User("u1") }, true),
            GroupMembers = { ["g1"] = [new("u1", "#microsoft.graph.user", "User u1", null, "u1@x.com", "Member")] },
            Directory = { ["g1"] = new("g1", "#microsoft.graph.group", "Grupo Lectores", null, null, null) },
            Mfa = { ["u1"] = "enabled" },
        };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 10);

        // Fila del grupo + fila derivada del miembro.
        Assert.Equal(2, store.Assignments.Count);
        var groupRow = Assert.Single(store.Assignments, r => r.PrincipalType == "Group");
        Assert.Equal("Grupo Lectores", groupRow.DisplayName);
        var derived = Assert.Single(store.Assignments, r => r.ViaGroupId == "g1");
        Assert.Equal("u1", derived.PrincipalObjectId);
        Assert.Equal("Grupo Lectores", derived.ViaGroupName);
        Assert.Equal("enabled", derived.MfaStatus);
        Assert.Equal("ok", store.Finished!.Value.Status);
    }

    [Fact]
    public async Task Credencial_sin_graph_guarda_guids_y_marca_sin_consent()
    {
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "u9", "User", "def-1", "Owner")] } };
        var graph = new FakeGraph { FailCredentials = { 1 } };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 10);

        var row = Assert.Single(store.Assignments);
        Assert.Null(row.DisplayName);           // GUID sin resolver
        Assert.Equal("u9", row.PrincipalObjectId);
        var cs = Assert.Single(store.CredStatuses);
        Assert.Equal("ok", cs.ArmStatus);
        Assert.Equal("sin_consent", cs.GraphStatus);
        Assert.Equal("partial", store.Finished!.Value.Status);
    }

    [Fact]
    public async Task Credencial_lighthouse_no_intenta_graph()
    {
        var lighthouse = new AccessCredentialUnit(2, "sesion-lh", "user_session", [("s1", "Sub Uno", "Enabled")]);
        var arm = new FakeArm { BySub = { ["s1"] = [] } };
        var graph = new FakeGraph { FailCredentials = { 2 } }; // si la llamara, fallaría con error
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [lighthouse]).RunAsync(1, 10);

        var cs = Assert.Single(store.CredStatuses);
        Assert.Equal("no_aplica", cs.GraphStatus);
        Assert.Equal("partial", store.Finished!.Value.Status);
    }

    [Fact]
    public async Task Guests_y_global_admins_se_persisten_con_mfa()
    {
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "guest1", "User", "def-1", "Reader")] } };
        var graph = new FakeGraph
        {
            Sweep = new(new Dictionary<string, GraphUser>
            {
                ["guest1"] = User("guest1", "ana_ext#EXT#@x.com", "Guest", true,
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                ["admin1"] = User("admin1"),
            }, true),
            GlobalAdmins = [new("admin1", "#microsoft.graph.user", "User admin1", null, "admin1@x.com", "Member")],
            Mfa = { ["guest1"] = "disabled", ["admin1"] = "enabled" },
        };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 10);

        var guest = Assert.Single(store.Guests);
        Assert.Equal("disabled", guest.MfaStatus);
        Assert.Contains("Reader (Sub Uno)", guest.RolesInSubs);
        var ga = Assert.Single(store.Gas);
        Assert.Equal("enabled", ga.MfaStatus);
        // La asignación del guest queda marcada como Guest.
        var row = Assert.Single(store.Assignments);
        Assert.Equal("Guest", row.UserType);
    }

    [Fact]
    public async Task Arm_fallido_marca_error_en_credencial_y_run_partial()
    {
        var arm = new FakeArm { FailCredentials = { 1 } };
        var graph = new FakeGraph { Sweep = new(new Dictionary<string, GraphUser>(), true) };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 10);

        var cs = Assert.Single(store.CredStatuses);
        Assert.Equal("error", cs.ArmStatus);
        Assert.Equal("partial", store.Finished!.Value.Status);
    }

    [Fact]
    public async Task Graph_falla_despues_del_sweep_no_persiste_guests_ni_gas()
    {
        // El sweep tiene éxito (member + guest) pero la llamada posterior a Global Admins revienta:
        // el estado debe quedar sin_consent y NO deben persistirse guests/GAs de ese sweep.
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "u1", "User", "def-1", "Reader")] } };
        var graph = new FakeGraph
        {
            Sweep = new(new Dictionary<string, GraphUser>
            {
                ["u1"] = User("u1"),
                ["guest1"] = User("guest1", "ana_ext#EXT#@x.com", "Guest"),
            }, true),
            GlobalAdminsError = new HttpRequestException("403", null, HttpStatusCode.Forbidden),
        };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 10);

        var cs = Assert.Single(store.CredStatuses);
        Assert.Equal("sin_consent", cs.GraphStatus);
        Assert.Empty(store.Guests);
        Assert.Equal("partial", store.Finished!.Value.Status);
    }

    [Fact]
    public async Task Graph_404_no_es_consent_se_reporta_como_error()
    {
        // Regresión (caso BANCO DELTA): un 404 de Graph (p.ej. recurso inexistente) quedaba
        // etiquetado sin_consent con el mensaje "Revisar admin consent", con los permisos bien
        // otorgados. Solo 401/403 son consent; el resto es "error" con detalle honesto.
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "u1", "User", "def-1", "Reader")] } };
        var graph = new FakeGraph
        {
            Sweep = new(new Dictionary<string, GraphUser> { ["u1"] = User("u1") }, true),
            GlobalAdminsError = new HttpRequestException("404 Not Found", null, HttpStatusCode.NotFound),
        };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 10);

        var cs = Assert.Single(store.CredStatuses);
        Assert.Equal("error", cs.GraphStatus);
        Assert.DoesNotContain("consent", cs.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("partial", store.Finished!.Value.Status);
    }

    [Fact]
    public async Task Dos_credenciales_mismo_tenant_deduplican_guests_y_gas()
    {
        // Dos credenciales (simulan el mismo tenant vía la misma instancia de FakeGraph) resuelven
        // el mismo guest y el mismo Global Admin: no deben duplicarse en el resultado final.
        var cred2 = new AccessCredentialUnit(2, "cred-secundaria", "app_secret", [("s2", "Sub Dos", "Enabled")]);
        var arm = new FakeArm { BySub = {
            ["s1"] = [new("/subscriptions/s1", "subscription", "guest1", "User", "def-1", "Reader")],
            ["s2"] = [new("/subscriptions/s2", "subscription", "guest1", "User", "def-1", "Reader")],
        } };
        var graph = new FakeGraph
        {
            Sweep = new(new Dictionary<string, GraphUser>
            {
                ["guest1"] = User("guest1", "ana_ext#EXT#@x.com", "Guest"),
                ["admin1"] = User("admin1"),
            }, true),
            GlobalAdmins = [new("admin1", "#microsoft.graph.user", "User admin1", null, "admin1@x.com", "Member")],
        };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1, cred2]).RunAsync(1, 10);

        var guest = Assert.Single(store.Guests);
        Assert.Equal("guest1", guest.ObjectId);
        var ga = Assert.Single(store.Gas);
        Assert.Equal("admin1", ga.ObjectId);
    }

    [Fact]
    public async Task Consulta_mfa_una_sola_vez_por_identidad()
    {
        // El prefetch paralelo NO debe multiplicar llamadas: una por identidad única, aunque la
        // misma cuenta aparezca en N asignaciones y además sea guest y Global Admin.
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "u1", "User", "def-1", "Reader"),
            new("/subscriptions/s1/resourceGroups/rg", "resource_group", "u1", "User", "def-1", "Reader"),
            new("/subscriptions/s1", "subscription", "u1", "User", "def-2", "Owner"),
            new("/subscriptions/s1", "subscription", "u2", "User", "def-1", "Reader")] } };
        var graph = new FakeGraph
        {
            Sweep = new(new Dictionary<string, GraphUser>
            {
                ["u1"] = User("u1"),
                ["u2"] = User("u2", "ana_ext#EXT#@x.com", "Guest"),
            }, true),
            GlobalAdmins = [new("u1", "#microsoft.graph.user", "User u1", null, "u1@x.com", "Member")],
        };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 7);

        Assert.Equal(2, graph.MfaCalls.Count);
        Assert.Equal(["u1", "u2"], graph.MfaCalls.Order());
        Assert.All(store.Assignments, a => Assert.Equal("unavailable", a.MfaStatus));
    }

    [Fact]
    public async Task Sin_consent_no_consulta_mfa()
    {
        // Sin Graph no hay a quién preguntar: ni una llamada, y el estado queda null (no medido).
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "u1", "User", "def-1", "Reader")] } };
        var graph = new FakeGraph { FailCredentials = { 1 } };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 7);

        Assert.Empty(graph.MfaCalls);
        Assert.Null(Assert.Single(store.Assignments).MfaStatus);
    }

    [Fact]
    public async Task Resuelve_mfa_de_miembros_de_grupo_guests_y_global_admins()
    {
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "g1", "Group", "def-1", "Reader")] } };
        var graph = new FakeGraph
        {
            Sweep = new(new Dictionary<string, GraphUser>
            {
                ["miembro"] = User("miembro"),
                ["invitado"] = User("invitado", "x_ext#EXT#@x.com", "Guest"),
                ["admin"] = User("admin"),
            }, true),
            GroupMembers = { ["g1"] = [new("miembro", "#microsoft.graph.user", "User miembro", null, "miembro@x.com", "Member")] },
            Directory = { ["g1"] = new("g1", "#microsoft.graph.group", "Grupo", null, null, null) },
            GlobalAdmins = [new("admin", "#microsoft.graph.user", "User admin", null, "admin@x.com", "Member")],
            Mfa = { ["miembro"] = "enabled", ["invitado"] = "disabled", ["admin"] = "enabled" },
        };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 7);

        Assert.Equal(["admin", "invitado", "miembro"], graph.MfaCalls.Order());
        Assert.Equal("enabled", store.Assignments.Single(a => a.ViaGroupId is not null).MfaStatus);
        Assert.Equal("disabled", Assert.Single(store.Guests).MfaStatus);
        Assert.Equal("enabled", Assert.Single(store.Gas).MfaStatus);
    }

    [Fact]
    public async Task Principals_de_otro_tenant_se_persisten_sin_resolver_ni_expandir()
    {
        // ForeignGroup vive en el tenant de otro (típico de MSP): intentar expandirlo o resolverlo
        // no tiene sentido, y su nombre vacío NO es una asignación huérfana.
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "fg1", "ForeignGroup", "def-1", "Owner", "owner"),
            new("/subscriptions/s1", "subscription", "d1", "Device", "def-2", "Reader", "lectura"),
            new("/subscriptions/s1", "subscription", "x1", "Unknown", "def-2", "Reader", "lectura")] } };
        var graph = new FakeGraph();
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 7);

        Assert.Equal(3, store.Assignments.Count);
        Assert.Equal(["ForeignGroup", "Device", "Unknown"], store.Assignments.Select(a => a.PrincipalType));
        Assert.All(store.Assignments, a => Assert.Null(a.DisplayName));
        Assert.Empty(graph.GroupExpansions);
        Assert.DoesNotContain("fg1", graph.RequestedIds);
        Assert.Equal("owner", store.Assignments[0].RoleClass);
    }

    [Fact]
    public async Task Filas_derivadas_heredan_la_clase_de_rol()
    {
        var arm = new FakeArm { BySub = { ["s1"] = [
            new("/subscriptions/s1", "subscription", "g1", "Group", "def-1", "Soporte N3", "owner", true)] } };
        var graph = new FakeGraph
        {
            Sweep = new(new Dictionary<string, GraphUser> { ["u1"] = User("u1") }, true),
            GroupMembers = { ["g1"] = [new("u1", "#microsoft.graph.user", "User u1", null, "u1@x.com", "Member")] },
            Directory = { ["g1"] = new("g1", "#microsoft.graph.group", "Grupo Admins", null, null, null) },
        };
        var store = new FakeStore();

        await new TestableSyncService(arm, graph, store, [Cred1]).RunAsync(1, 7);

        Assert.Equal(2, store.Assignments.Count);
        Assert.All(store.Assignments, a => Assert.Equal("owner", a.RoleClass));
        Assert.All(store.Assignments, a => Assert.True(a.IsCustomRole));
        var derivada = Assert.Single(store.Assignments.Where(a => a.ViaGroupId is not null));
        Assert.Equal("u1", derivada.PrincipalObjectId);
        Assert.Equal("g1", derivada.ViaGroupId);
    }
}
