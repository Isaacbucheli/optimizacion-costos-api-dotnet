using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Identity;

/// <summary>Proyección pública de un usuario (port de public_user de app/auth.py).</summary>
/// <remarks>IsSuperAdmin se deriva de la config (SUPERADMIN_EMAILS), no se persiste; lo fija el controller.</remarks>
public sealed record PublicUser(int UserId, string Email, string FullName, string Role, bool IsActive, string CreatedAt, bool MustChangePassword = false, bool IsSuperAdmin = false);

/// <summary>Fila de login: incluye el hash y el estado (no se expone al cliente).</summary>
public sealed record LoginRow(int UserId, string Email, string FullName, string Role, string PasswordHash, bool IsActive, bool MustChangePassword = false);

/// <summary>Algún client_id solicitado no existe (→ 400).</summary>
public sealed class InvalidClientIdsException() : Exception("Some client_ids do not exist");

/// <summary>
/// Acceso a dbo.app_users + dbo.user_client_assignment. Port de las funciones de datos de
/// app/routes/auth.py + app/auth.py (ensure_auth_schema, _insert_app_user, _count_users,
/// _get_user_by_email, asignaciones). SQL siempre parametrizado.
/// </summary>
public interface IAppUserStore
{
    Task EnsureSchemaAsync(CancellationToken ct = default);
    Task<int> CountBootstrapUsersAsync(CancellationToken ct = default);
    Task<LoginRow?> GetForLoginAsync(string email, CancellationToken ct = default);
    Task<bool> EmailExistsAsync(string email, int? excludeUserId = null, CancellationToken ct = default);
    Task<IReadOnlyList<PublicUser>> ListUsersAsync(CancellationToken ct = default);
    Task<PublicUser> InsertUserAsync(string email, string fullName, string role, string passwordHash, bool mustChangePassword, CancellationToken ct = default);
    Task<PublicUser> UpsertBootstrapAdminAsync(string email, string fullName, string passwordHash, CancellationToken ct = default);
    Task<PublicUser?> GetByIdAsync(int userId, CancellationToken ct = default);
    Task<bool> UpdateUserAsync(int userId, string? email, string? fullName, string? role, string? passwordHash, bool? isActive, bool? mustChangePassword = null, CancellationToken ct = default);
    Task<string?> DeleteUserAsync(int userId, CancellationToken ct = default);
    Task<IReadOnlyList<int>> GetAssignmentsAsync(int userId, CancellationToken ct = default);
    Task<IReadOnlyList<int>> ReplaceAssignmentsAsync(int userId, IReadOnlyList<int> clientIds, CancellationToken ct = default);
    Task<bool> UserExistsAsync(int userId, CancellationToken ct = default);
    /// <summary>Fuerza rol=admin + is_active=1 para los emails superadmin que existan (auto-reparación).</summary>
    Task EnsureSuperAdminsAsync(IReadOnlyList<string> emails, CancellationToken ct = default);
    /// <summary>Revoca los tokens del usuario (WEB-12): los access emitidos antes de ahora
    /// dejan de valer vía app_users.tokens_revoked_at. La llaman logout, change-password y el
    /// reset de contraseña por admin. NUNCA el re-hash transparente del login (no es un cambio
    /// de credenciales del usuario).</summary>
    Task RevokeTokensAsync(int userId, CancellationToken ct = default);
}

public sealed class SqlAppUserStore(ISqlConnectionFactory factory) : IAppUserStore
{
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);
        await EnsureAssignmentSchemaAsync(conn, ct);
    }

    // Port de ensure_auth_schema: crea app_users si falta + agrega columnas + backfill.
    private static async Task EnsureAuthSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.app_users', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.app_users (
                    user_id INT IDENTITY(1,1) PRIMARY KEY,
                    email NVARCHAR(255) NOT NULL UNIQUE,
                    full_name NVARCHAR(200) NOT NULL,
                    role NVARCHAR(30) NOT NULL,
                    password_hash NVARCHAR(255) NOT NULL,
                    is_active BIT NOT NULL DEFAULT 1,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2 NULL
                );
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = """
            IF COL_LENGTH('dbo.app_users', 'email') IS NULL
                ALTER TABLE dbo.app_users ADD email NVARCHAR(255) NULL;
            IF COL_LENGTH('dbo.app_users', 'full_name') IS NULL
                ALTER TABLE dbo.app_users ADD full_name NVARCHAR(200) NULL;
            IF COL_LENGTH('dbo.app_users', 'role') IS NULL
                ALTER TABLE dbo.app_users ADD role NVARCHAR(30) NULL;
            IF COL_LENGTH('dbo.app_users', 'password_hash') IS NULL
                ALTER TABLE dbo.app_users ADD password_hash NVARCHAR(255) NULL;
            IF COL_LENGTH('dbo.app_users', 'is_active') IS NULL
                ALTER TABLE dbo.app_users ADD is_active BIT NOT NULL CONSTRAINT DF_app_users_is_active DEFAULT 1;
            IF COL_LENGTH('dbo.app_users', 'created_at') IS NULL
                ALTER TABLE dbo.app_users ADD created_at DATETIME2 NOT NULL CONSTRAINT DF_app_users_created_at DEFAULT SYSUTCDATETIME();
            IF COL_LENGTH('dbo.app_users', 'updated_at') IS NULL
                ALTER TABLE dbo.app_users ADD updated_at DATETIME2 NULL;
            IF COL_LENGTH('dbo.app_users', 'must_change_password') IS NULL
                ALTER TABLE dbo.app_users ADD must_change_password BIT NOT NULL CONSTRAINT DF_app_users_must_change_password DEFAULT 0;
            IF COL_LENGTH('dbo.app_users', 'tokens_revoked_at') IS NULL
                ALTER TABLE dbo.app_users ADD tokens_revoked_at DATETIME2 NULL;
            """;
        await cmd2.ExecuteNonQueryAsync(ct);

        await using var cmd3 = conn.CreateCommand();
        cmd3.CommandText = """
            UPDATE dbo.app_users
            SET full_name = COALESCE(NULLIF(full_name, ''), email, 'Usuario BIT'),
                role = COALESCE(NULLIF(role, ''), 'lector')
            WHERE full_name IS NULL OR full_name = '' OR role IS NULL OR role = '';
            """;
        await cmd3.ExecuteNonQueryAsync(ct);
    }

    // Port de ensure_assignment_schema (mismo DDL que SqlAnalysisAccess).
    private static async Task EnsureAssignmentSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.user_client_assignment', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.user_client_assignment (
                    user_id INT NOT NULL,
                    client_id INT NOT NULL,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT PK_user_client_assignment PRIMARY KEY (user_id, client_id),
                    CONSTRAINT FK_uca_user FOREIGN KEY (user_id)
                        REFERENCES dbo.app_users (user_id) ON DELETE CASCADE,
                    CONSTRAINT FK_uca_client FOREIGN KEY (client_id)
                        REFERENCES dbo.clients (client_id) ON DELETE CASCADE
                );
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountBootstrapUsersAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM dbo.app_users
            WHERE is_active = 1
              AND password_hash LIKE 'pbkdf2_sha256$%'
              AND role IN ('admin', 'consultor', 'lector')
            """;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<LoginRow?> GetForLoginAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, email, full_name, role, password_hash, is_active, must_change_password
            FROM dbo.app_users WHERE LOWER(email) = LOWER(@email)
            """;
        cmd.Parameters.Add(new SqlParameter("@email", email));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new LoginRow(
            r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? "" : r.GetString(4), !r.IsDBNull(5) && r.GetBoolean(5),
            !r.IsDBNull(6) && r.GetBoolean(6));
    }

    public async Task RevokeTokensAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.app_users SET tokens_revoked_at = SYSUTCDATETIME() WHERE user_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", userId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> EmailExistsAsync(string email, int? excludeUserId = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = excludeUserId is null
            ? "SELECT 1 FROM dbo.app_users WHERE LOWER(email) = LOWER(@email)"
            : "SELECT 1 FROM dbo.app_users WHERE LOWER(email) = LOWER(@email) AND user_id <> @exclude";
        cmd.Parameters.Add(new SqlParameter("@email", email));
        if (excludeUserId is not null)
            cmd.Parameters.Add(new SqlParameter("@exclude", excludeUserId.Value));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<IReadOnlyList<PublicUser>> ListUsersAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, email, full_name, role, is_active, created_at, must_change_password
            FROM dbo.app_users ORDER BY created_at DESC
            """;
        var list = new List<PublicUser>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(MapPublic(r));
        return list;
    }

    public async Task<PublicUser> InsertUserAsync(
        string email, string fullName, string role, string passwordHash, bool mustChangePassword, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);
        await InsertAppUserDefensiveAsync(conn, email, fullName, role, passwordHash, mustChangePassword, ct);
        var user = await GetByEmailInternalAsync(conn, email, ct);
        return user!;
    }

    public async Task<PublicUser> UpsertBootstrapAdminAsync(
        string email, string fullName, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);

        await using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT user_id FROM dbo.app_users WHERE LOWER(email) = LOWER(@email)";
            find.Parameters.Add(new SqlParameter("@email", email));
            var existing = await find.ExecuteScalarAsync(ct);
            if (existing is not null)
            {
                await using var upd = conn.CreateCommand();
                upd.CommandText = """
                    UPDATE dbo.app_users
                    SET full_name = @name, role = 'admin', password_hash = @hash,
                        is_active = 1, must_change_password = 0, updated_at = SYSUTCDATETIME()
                    WHERE user_id = @id
                    """;
                upd.Parameters.Add(new SqlParameter("@name", fullName));
                upd.Parameters.Add(new SqlParameter("@hash", passwordHash));
                upd.Parameters.Add(new SqlParameter("@id", Convert.ToInt32(existing)));
                await upd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                // El admin inicial elige su propia contraseña: no se fuerza cambio.
                await InsertAppUserDefensiveAsync(conn, email, fullName, "admin", passwordHash, mustChangePassword: false, ct);
            }
        }
        return (await GetByEmailInternalAsync(conn, email, ct))!;
    }

    public async Task<PublicUser?> GetByIdAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, email, full_name, role, is_active, created_at, must_change_password
            FROM dbo.app_users WHERE user_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapPublic(r) : null;
    }

    public async Task<bool> UpdateUserAsync(
        int userId, string? email, string? fullName, string? role, string? passwordHash, bool? isActive,
        bool? mustChangePassword = null, CancellationToken ct = default)
    {
        // SET dinámico con nombres de columna de allowlist (no entran del cliente).
        var sets = new List<string>();
        var ps = new List<SqlParameter>();
        if (email is not null)
        {
            sets.Add("email = @email"); ps.Add(new SqlParameter("@email", email));
            sets.Add("username = @email"); // identidad de login sincronizada (si existe la col)
        }
        if (fullName is not null) { sets.Add("full_name = @full_name"); ps.Add(new SqlParameter("@full_name", fullName)); }
        if (role is not null) { sets.Add("role = @role"); ps.Add(new SqlParameter("@role", role)); }
        if (passwordHash is not null) { sets.Add("password_hash = @hash"); ps.Add(new SqlParameter("@hash", passwordHash)); }
        if (isActive is not null) { sets.Add("is_active = @active"); ps.Add(new SqlParameter("@active", isActive.Value ? 1 : 0)); }
        if (mustChangePassword is not null) { sets.Add("must_change_password = @mcp"); ps.Add(new SqlParameter("@mcp", mustChangePassword.Value ? 1 : 0)); }
        sets.Add("updated_at = SYSUTCDATETIME()");

        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);

        // "username = @email" solo si la columna existe (esquemas legacy de prod).
        if (email is not null && !await ColumnExistsAsync(conn, "username", ct))
            sets.Remove("username = @email");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE dbo.app_users SET {string.Join(", ", sets)} WHERE user_id = @id";
        foreach (var p in ps) cmd.Parameters.Add(p);
        cmd.Parameters.Add(new SqlParameter("@id", userId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<string?> DeleteUserAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);
        await EnsureAssignmentSchemaAsync(conn, ct);

        string? email;
        await using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT email FROM dbo.app_users WHERE user_id = @id";
            find.Parameters.Add(new SqlParameter("@id", userId));
            email = (await find.ExecuteScalarAsync(ct)) as string;
        }
        if (email is null) return null;

        await using (var delA = conn.CreateCommand())
        {
            delA.CommandText = "DELETE FROM dbo.user_client_assignment WHERE user_id = @id";
            delA.Parameters.Add(new SqlParameter("@id", userId));
            await delA.ExecuteNonQueryAsync(ct);
        }
        await using (var delU = conn.CreateCommand())
        {
            delU.CommandText = "DELETE FROM dbo.app_users WHERE user_id = @id";
            delU.Parameters.Add(new SqlParameter("@id", userId));
            await delU.ExecuteNonQueryAsync(ct);
        }
        return email;
    }

    public async Task<bool> UserExistsAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.app_users WHERE user_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", userId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task EnsureSuperAdminsAsync(IReadOnlyList<string> emails, CancellationToken ct = default)
    {
        var clean = emails.Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList();
        if (clean.Count == 0) return;

        await using var conn = await factory.OpenAsync(ct);
        await EnsureAuthSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        // IN parametrizado (@sa0, @sa1, ...) → SQL seguro. Solo corrige lo que difiere.
        var placeholders = string.Join(", ", clean.Select((_, i) => $"@sa{i}"));
        cmd.CommandText = $"""
            UPDATE dbo.app_users
            SET role = 'admin', is_active = 1
            WHERE LOWER(email) IN ({placeholders})
              AND (role <> 'admin' OR is_active = 0)
            """;
        for (var i = 0; i < clean.Count; i++)
            cmd.Parameters.Add(new SqlParameter($"@sa{i}", clean[i]));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<int>> GetAssignmentsAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAssignmentSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.user_client_assignment WHERE user_id = @id ORDER BY client_id";
        cmd.Parameters.Add(new SqlParameter("@id", userId));
        var list = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetInt32(0));
        return list;
    }

    public async Task<IReadOnlyList<int>> ReplaceAssignmentsAsync(
        int userId, IReadOnlyList<int> clientIds, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAssignmentSchemaAsync(conn, ct);
        var requested = await ValidateAndReplaceAsync(conn, userId, clientIds, ct);
        return requested;
    }

    // ----------------- helpers privados -----------------

    private static PublicUser MapPublic(SqlDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
        !r.IsDBNull(4) && r.GetBoolean(4),
        r.IsDBNull(5) ? "" : r.GetValue(5).ToString() ?? "",
        !r.IsDBNull(6) && r.GetBoolean(6));

    private static async Task<PublicUser?> GetByEmailInternalAsync(SqlConnection conn, string email, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, email, full_name, role, is_active, created_at, must_change_password
            FROM dbo.app_users WHERE LOWER(email) = LOWER(@email)
            """;
        cmd.Parameters.Add(new SqlParameter("@email", email));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapPublic(r) : null;
    }

    private static async Task<HashSet<string>> ColumnNamesAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.app_users')";
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) cols.Add(r.GetString(0));
        return cols;
    }

    private static async Task<bool> ColumnExistsAsync(SqlConnection conn, string column, CancellationToken ct)
        => (await ColumnNamesAsync(conn, ct)).Contains(column);

    private static async Task<bool> HasIdentityUserIdAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMNPROPERTY(OBJECT_ID('dbo.app_users'), 'user_id', 'IsIdentity')";
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is not null && v != DBNull.Value && Convert.ToInt32(v) == 1;
    }

    /// <summary>
    /// Port de _insert_app_user: inserta cubriendo columnas legacy (username/display_name/role_name)
    /// si existen, y calcula user_id manualmente si la PK no es IDENTITY (esquemas viejos de prod).
    /// Las columnas vienen de un allowlist fijo intersectado con el esquema real → SQL seguro.
    /// </summary>
    private static async Task InsertAppUserDefensiveAsync(
        SqlConnection conn, string email, string fullName, string role, string passwordHash,
        bool mustChangePassword, CancellationToken ct)
    {
        var cols = await ColumnNamesAsync(conn, ct);

        // (columna -> valor). created_at/updated_at son expresiones SQL (sin parámetro).
        var values = new Dictionary<string, string>
        {
            ["email"] = email, ["username"] = email,
            ["full_name"] = fullName, ["display_name"] = fullName,
            ["role"] = role, ["role_name"] = role,
            ["password_hash"] = passwordHash,
            ["is_active"] = "1",
            ["must_change_password"] = mustChangePassword ? "1" : "0",
            ["created_at"] = "SYSUTCDATETIME()", ["updated_at"] = "SYSUTCDATETIME()",
        };
        var ordered = new[]
        {
            "email", "username", "full_name", "display_name", "role", "role_name",
            "password_hash", "is_active", "must_change_password", "created_at", "updated_at",
        };

        var insertCols = ordered.Where(cols.Contains).ToList();
        var placeholders = new List<string>();
        var ps = new List<SqlParameter>();
        var idx = 0;
        foreach (var col in insertCols)
        {
            var val = values[col];
            if (val.EndsWith("()", StringComparison.Ordinal)) // SYSUTCDATETIME()
            {
                placeholders.Add(val);
            }
            else if (col is "is_active" or "must_change_password")
            {
                placeholders.Add(val); // literal 0/1, igual que Python (valor 1)
            }
            else
            {
                var p = $"@p{idx++}";
                placeholders.Add(p);
                ps.Add(new SqlParameter(p, val));
            }
        }

        await using var cmd = conn.CreateCommand();
        if (!await HasIdentityUserIdAsync(conn, ct))
        {
            await using var next = conn.CreateCommand();
            next.CommandText = "SELECT COALESCE(MAX(user_id), 0) + 1 FROM dbo.app_users WITH (UPDLOCK, HOLDLOCK)";
            var nextId = Convert.ToInt32(await next.ExecuteScalarAsync(ct));
            insertCols.Insert(0, "user_id");
            placeholders.Insert(0, "@uid");
            cmd.Parameters.Add(new SqlParameter("@uid", nextId));
        }

        cmd.CommandText =
            $"INSERT INTO dbo.app_users ({string.Join(", ", insertCols)}) VALUES ({string.Join(", ", placeholders)})";
        foreach (var p in ps) cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Port de _validate_client_ids + _replace_user_client_assignments.</summary>
    private static async Task<IReadOnlyList<int>> ValidateAndReplaceAsync(
        SqlConnection conn, int userId, IReadOnlyList<int> clientIds, CancellationToken ct)
    {
        var requested = clientIds.Distinct().OrderBy(x => x).ToList();

        if (requested.Count > 0)
        {
            // Validar existencia (parámetros, no interpolación).
            var inParams = requested.Select((_, i) => $"@c{i}").ToList();
            await using var check = conn.CreateCommand();
            check.CommandText = $"SELECT client_id FROM dbo.clients WHERE client_id IN ({string.Join(", ", inParams)})";
            for (var i = 0; i < requested.Count; i++)
                check.Parameters.Add(new SqlParameter($"@c{i}", requested[i]));
            var existing = new HashSet<int>();
            await using (var r = await check.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) existing.Add(r.GetInt32(0));
            if (requested.Any(id => !existing.Contains(id)))
                throw new InvalidClientIdsException();
        }

        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM dbo.user_client_assignment WHERE user_id = @id";
            del.Parameters.Add(new SqlParameter("@id", userId));
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var clientId in requested)
        {
            await using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO dbo.user_client_assignment (user_id, client_id) VALUES (@u, @c)";
            ins.Parameters.Add(new SqlParameter("@u", userId));
            ins.Parameters.Add(new SqlParameter("@c", clientId));
            await ins.ExecuteNonQueryAsync(ct);
        }
        return requested;
    }
}
