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

    private void Gate(int credentialId) { if (FailCredentials.Contains(credentialId)) throw new HttpRequestException("403 consent"); }
    public Task<GraphUserSweep> SweepUsersAsync(int c, CancellationToken ct = default) { Gate(c); return Task.FromResult(Sweep); }
    public Task<IReadOnlyList<GraphDirectoryObject>> GetGroupTransitiveMembersAsync(int c, string g, CancellationToken ct = default)
        { Gate(c); return Task.FromResult<IReadOnlyList<GraphDirectoryObject>>(GroupMembers.GetValueOrDefault(g, [])); }
    public Task<IReadOnlyList<GraphDirectoryObject>> GetGlobalAdminsAsync(int c, CancellationToken ct = default)
        { Gate(c); return Task.FromResult<IReadOnlyList<GraphDirectoryObject>>(GlobalAdmins); }
    public Task<IReadOnlyDictionary<string, GraphDirectoryObject>> GetByIdsAsync(int c, IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Gate(c); return Task.FromResult<IReadOnlyDictionary<string, GraphDirectoryObject>>(
            ids.Where(Directory.ContainsKey).ToDictionary(i => i, i => Directory[i])); }
    public Task<string> GetMfaStatusAsync(int c, string u, CancellationToken ct = default)
        { Gate(c); return Task.FromResult(Mfa.GetValueOrDefault(u, "unavailable")); }
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
}
