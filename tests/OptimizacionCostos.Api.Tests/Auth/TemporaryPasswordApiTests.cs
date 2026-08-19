using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Auth;

/// <summary>
/// Flujo de contraseña temporal: crear usuario / reset por admin marcan
/// must_change_password=1; login y /auth/me exponen la bandera; POST
/// /auth/change-password valida la actual, exige mínimo 8 y apaga la bandera.
/// Sin BD (store fake) pero con el pipeline MVC real (auth, JSON snake_case).
/// </summary>
public sealed class TemporaryPasswordApiTests : IClassFixture<TemporaryPasswordApiTests.Factory>
{
    private readonly Factory _factory;
    public TemporaryPasswordApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string? email, string? role)
    {
        var client = _factory.CreateClient();
        if (email is not null && role is not null)
        {
            _factory.Directory.Add(email, role);
            var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    // ---- Login expone la bandera ----

    [Fact]
    public async Task Login_con_contrasena_temporal_devuelve_bandera_encendida()
    {
        _factory.Users.AddUser("temp1@bit.ec", "Temporal123", "lector", mustChange: true);
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login", new { username = "temp1@bit.ec", password = "Temporal123" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("must_change_password").GetBoolean());
    }

    [Fact]
    public async Task Login_normal_devuelve_bandera_apagada()
    {
        _factory.Users.AddUser("normal1@bit.ec", "Definitiva123", "lector", mustChange: false);
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login", new { username = "normal1@bit.ec", password = "Definitiva123" });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("must_change_password").GetBoolean());
    }

    // ---- Re-hash transparente en el login (endurecimiento a 600k iteraciones) ----

    [Fact]
    public async Task Login_con_hash_legacy_entra_y_lo_rehashea_sin_tocar_la_bandera()
    {
        // Vector real del stack anterior: "Secreta123!" con 120000 iteraciones (formato 3 partes).
        const string legacyHash =
            "pbkdf2_sha256$0123456789abcdef0123456789abcdef$8be50fe9e14c2d384adf4f43f6439ec07a1aebf8cee936f3c987a16e8b1fd00b";
        var u = _factory.Users.AddUser("legacy1@bit.ec", "placeholder1", "lector", mustChange: false);
        u.PasswordHash = legacyHash;

        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login", new { username = "legacy1@bit.ec", password = "Secreta123!" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var row = _factory.Users.Find("legacy1@bit.ec")!;
        Assert.StartsWith($"pbkdf2_sha256${PasswordHasher.Iterations}$", row.PasswordHash); // endurecido
        Assert.True(PasswordHasher.Verify("Secreta123!", row.PasswordHash)); // misma contraseña
        Assert.False(row.MustChangePassword); // el rehash NO enciende la bandera

        // El siguiente login ya no re-hashea (formato actual) y sigue funcionando.
        var again = await client.PostAsJsonAsync("/auth/login", new { username = "legacy1@bit.ec", password = "Secreta123!" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Login_con_hash_legacy_y_bandera_encendida_la_conserva()
    {
        const string legacyHash =
            "pbkdf2_sha256$0123456789abcdef0123456789abcdef$8be50fe9e14c2d384adf4f43f6439ec07a1aebf8cee936f3c987a16e8b1fd00b";
        var u = _factory.Users.AddUser("legacy2@bit.ec", "placeholder1", "lector", mustChange: true);
        u.PasswordHash = legacyHash;

        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login", new { username = "legacy2@bit.ec", password = "Secreta123!" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("must_change_password").GetBoolean()); // sigue temporal
        Assert.True(_factory.Users.Find("legacy2@bit.ec")!.MustChangePassword);
    }

    [Fact]
    public async Task Me_incluye_la_bandera()
    {
        _factory.Users.AddUser("temp2@bit.ec", "Temporal123", "lector", mustChange: true);
        var client = ClientFor("temp2@bit.ec", Roles.Lector);
        var body = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        Assert.True(body.GetProperty("must_change_password").GetBoolean());
    }

    // ---- Alta y reset por admin marcan la contraseña como temporal ----

    [Fact]
    public async Task Crear_usuario_marca_contrasena_temporal()
    {
        var client = ClientFor("admin-alta@bit.ec", Roles.Admin);
        var res = await client.PostAsJsonAsync("/auth/users",
            new { email = "nuevo1@bit.ec", full_name = "Nuevo Uno", role = "lector", password = "Temporal123" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("must_change_password").GetBoolean());
        Assert.True(_factory.Users.Find("nuevo1@bit.ec")!.MustChangePassword);
    }

    [Fact]
    public async Task Reset_de_contrasena_por_admin_marca_temporal()
    {
        var target = _factory.Users.AddUser("reseteado@bit.ec", "Original123", "lector", mustChange: false);
        var client = ClientFor("admin-reset@bit.ec", Roles.Admin);
        var res = await client.PutAsJsonAsync($"/auth/users/{target.UserId}", new { password = "Reseteada123" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(_factory.Users.Find("reseteado@bit.ec")!.MustChangePassword);
    }

    [Fact]
    public async Task Admin_cambia_su_propia_contrasena_sin_marcar_temporal()
    {
        var self = _factory.Users.AddUser("admin-propio@bit.ec", "Propia1234", "admin", mustChange: false);
        var client = ClientFor("admin-propio@bit.ec", Roles.Admin);
        var res = await client.PutAsJsonAsync($"/auth/users/{self.UserId}", new { password = "NuevaPropia123" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(_factory.Users.Find("admin-propio@bit.ec")!.MustChangePassword);
    }

    [Fact]
    public async Task Actualizar_con_contrasena_corta_devuelve_400()
    {
        var target = _factory.Users.AddUser("corta@bit.ec", "Original123", "lector", mustChange: false);
        var client = ClientFor("admin-corta@bit.ec", Roles.Admin);
        var res = await client.PutAsJsonAsync($"/auth/users/{target.UserId}", new { password = "corta" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- POST /auth/change-password ----

    [Fact]
    public async Task ChangePassword_sin_token_devuelve_401()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/change-password",
            new { current_password = "x", new_password = "NuevaClave123" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_actual_incorrecta_devuelve_400()
    {
        _factory.Users.AddUser("cp1@bit.ec", "Temporal123", "lector", mustChange: true);
        var client = ClientFor("cp1@bit.ec", Roles.Lector);
        var res = await client.PostAsJsonAsync("/auth/change-password",
            new { current_password = "Equivocada1", new_password = "NuevaClave123" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True(_factory.Users.Find("cp1@bit.ec")!.MustChangePassword); // sin cambios
    }

    [Fact]
    public async Task ChangePassword_nueva_corta_devuelve_400()
    {
        _factory.Users.AddUser("cp2@bit.ec", "Temporal123", "lector", mustChange: true);
        var client = ClientFor("cp2@bit.ec", Roles.Lector);
        var res = await client.PostAsJsonAsync("/auth/change-password",
            new { current_password = "Temporal123", new_password = "corta" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_igual_a_la_actual_devuelve_400()
    {
        _factory.Users.AddUser("cp3@bit.ec", "Temporal123", "lector", mustChange: true);
        var client = ClientFor("cp3@bit.ec", Roles.Lector);
        var res = await client.PostAsJsonAsync("/auth/change-password",
            new { current_password = "Temporal123", new_password = "Temporal123" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ok_apaga_bandera_y_el_nuevo_login_funciona()
    {
        _factory.Users.AddUser("cp4@bit.ec", "Temporal123", "lector", mustChange: true);
        var client = ClientFor("cp4@bit.ec", Roles.Lector);

        var res = await client.PostAsJsonAsync("/auth/change-password",
            new { current_password = "Temporal123", new_password = "Definitiva456" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.False(body.GetProperty("must_change_password").GetBoolean());

        var row = _factory.Users.Find("cp4@bit.ec")!;
        Assert.False(row.MustChangePassword);
        Assert.True(PasswordHasher.Verify("Definitiva456", row.PasswordHash));

        // Login con la nueva contraseña: OK y sin bandera. Con la vieja: 401.
        var anon = _factory.CreateClient();
        var relogin = await anon.PostAsJsonAsync("/auth/login", new { username = "cp4@bit.ec", password = "Definitiva456" });
        Assert.Equal(HttpStatusCode.OK, relogin.StatusCode);
        var reloginBody = await relogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(reloginBody.GetProperty("must_change_password").GetBoolean());

        var oldLogin = await anon.PostAsJsonAsync("/auth/login", new { username = "cp4@bit.ec", password = "Temporal123" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
    }

    // ---- Infraestructura ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;

        public FakeUserDirectory Directory { get; } = new();
        public FakeAppUserStore Users { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAppUserStore>();
                services.AddSingleton<IAppUserStore>(Users);
                services.RemoveAll<IRefreshTokenStore>();
                services.AddSingleton<IRefreshTokenStore>(new FakeRefreshTokenStore());
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }
}

/// <summary>Usuario mutable del store en memoria.</summary>
public sealed class FakeAppUser
{
    public int UserId { get; init; }
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Role { get; set; } = "lector";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime? TokensRevokedAt { get; set; }
    public string CreatedAt { get; } = "2026-01-01T00:00:00";
}

/// <summary>IAppUserStore en memoria (sin SQL) para probar AuthController completo.</summary>
public sealed class FakeAppUserStore : IAppUserStore
{
    private readonly List<FakeAppUser> _users = [];
    private readonly Dictionary<int, List<int>> _assignments = [];
    private int _seq;

    /// <summary>Vacía el store (para fixtures compartidos entre tests).</summary>
    public void Clear() { _users.Clear(); _assignments.Clear(); _seq = 0; }

    public FakeAppUser AddUser(string email, string password, string role, bool mustChange)
    {
        var u = new FakeAppUser
        {
            UserId = ++_seq,
            Email = email,
            FullName = "Fake " + email,
            Role = role,
            PasswordHash = PasswordHasher.Hash(password),
            MustChangePassword = mustChange,
        };
        _users.Add(u);
        return u;
    }

    public FakeAppUser? Find(string email) =>
        _users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));

    private static PublicUser Map(FakeAppUser u) =>
        new(u.UserId, u.Email, u.FullName, u.Role, u.IsActive, u.CreatedAt, u.MustChangePassword);

    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<int> CountBootstrapUsersAsync(CancellationToken ct = default) =>
        Task.FromResult(_users.Count(u => u.IsActive));

    public Task<LoginRow?> GetForLoginAsync(string email, CancellationToken ct = default)
    {
        var u = Find(email);
        return Task.FromResult(u is null
            ? null
            : new LoginRow(u.UserId, u.Email, u.FullName, u.Role, u.PasswordHash, u.IsActive, u.MustChangePassword));
    }

    public Task<bool> EmailExistsAsync(string email, int? excludeUserId = null, CancellationToken ct = default)
    {
        var u = Find(email);
        return Task.FromResult(u is not null && u.UserId != excludeUserId);
    }

    public Task RevokeTokensAsync(int userId, CancellationToken ct = default)
    {
        var u = _users.FirstOrDefault(x => x.UserId == userId);
        if (u is not null) u.TokensRevokedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PublicUser>> ListUsersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PublicUser>>(_users.Select(Map).ToList());

    public Task<PublicUser> InsertUserAsync(
        string email, string fullName, string role, string passwordHash, bool mustChangePassword, CancellationToken ct = default)
    {
        var u = new FakeAppUser
        {
            UserId = ++_seq,
            Email = email,
            FullName = fullName,
            Role = role,
            PasswordHash = passwordHash,
            MustChangePassword = mustChangePassword,
        };
        _users.Add(u);
        return Task.FromResult(Map(u));
    }

    public Task<PublicUser> UpsertBootstrapAdminAsync(
        string email, string fullName, string passwordHash, CancellationToken ct = default)
    {
        var u = Find(email);
        if (u is null)
        {
            u = new FakeAppUser { UserId = ++_seq, Email = email };
            _users.Add(u);
        }
        u.FullName = fullName;
        u.Role = "admin";
        u.PasswordHash = passwordHash;
        u.IsActive = true;
        u.MustChangePassword = false;
        return Task.FromResult(Map(u));
    }

    public Task<PublicUser?> GetByIdAsync(int userId, CancellationToken ct = default)
    {
        var u = _users.FirstOrDefault(x => x.UserId == userId);
        return Task.FromResult(u is null ? null : (PublicUser?)Map(u));
    }

    public Task<bool> UpdateUserAsync(
        int userId, string? email, string? fullName, string? role, string? passwordHash, bool? isActive,
        bool? mustChangePassword = null, CancellationToken ct = default)
    {
        var u = _users.FirstOrDefault(x => x.UserId == userId);
        if (u is null) return Task.FromResult(false);
        if (email is not null) u.Email = email;
        if (fullName is not null) u.FullName = fullName;
        if (role is not null) u.Role = role;
        if (passwordHash is not null) u.PasswordHash = passwordHash;
        if (isActive is not null) u.IsActive = isActive.Value;
        if (mustChangePassword is not null) u.MustChangePassword = mustChangePassword.Value;
        return Task.FromResult(true);
    }

    public Task<string?> DeleteUserAsync(int userId, CancellationToken ct = default)
    {
        var u = _users.FirstOrDefault(x => x.UserId == userId);
        if (u is null) return Task.FromResult<string?>(null);
        _users.Remove(u);
        _assignments.Remove(userId);
        return Task.FromResult<string?>(u.Email);
    }

    public Task<IReadOnlyList<int>> GetAssignmentsAsync(int userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<int>>(_assignments.TryGetValue(userId, out var ids) ? ids : []);

    public Task<IReadOnlyList<int>> ReplaceAssignmentsAsync(
        int userId, IReadOnlyList<int> clientIds, CancellationToken ct = default)
    {
        var ordered = clientIds.Distinct().OrderBy(x => x).ToList();
        _assignments[userId] = ordered;
        return Task.FromResult<IReadOnlyList<int>>(ordered);
    }

    public Task<bool> UserExistsAsync(int userId, CancellationToken ct = default) =>
        Task.FromResult(_users.Any(x => x.UserId == userId));

    public Task EnsureSuperAdminsAsync(IReadOnlyList<string> emails, CancellationToken ct = default)
    {
        var set = new HashSet<string>(emails.Select(e => e.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        foreach (var u in _users.Where(u => set.Contains(u.Email.Trim().ToLowerInvariant())))
        {
            u.Role = "admin";
            u.IsActive = true;
        }
        return Task.CompletedTask;
    }
}
