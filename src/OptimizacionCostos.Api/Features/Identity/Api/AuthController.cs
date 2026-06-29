using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Features.Identity.Api;

/// <summary>
/// Autenticación + administración de usuarios. Port de app/routes/auth.py (prefix /auth).
/// Emisión de JWT (login/bootstrap), estado, perfil, y CRUD de usuarios + asignación de
/// clientes (solo admin). El rol que autoriza es el VIVO en BD (AuthSetup lo re-resuelve).
/// </summary>
[ApiController]
[Route("auth")]
public sealed class AuthController(
    IAppUserStore users,
    TokenIssuer tokens,
    AppConfig config) : ControllerBase
{
    // -------------------- DTOs --------------------
    public sealed record LoginRequest(string? Username, string? Password);
    public sealed record BootstrapRequest(string? Email, string? FullName, string? Password);
    public sealed record UserCreateRequest(string? Email, string? FullName, string? Role, string? Password, List<int>? ClientIds);
    public sealed record UserUpdateRequest(string? Email, string? FullName, string? Role, string? Password, bool? IsActive);
    public sealed record ClientAssignmentRequest(List<int>? ClientIds);

    // -------------------- GET /auth/status --------------------
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var hasUsers = await users.CountBootstrapUsersAsync(ct) > 0;
        return Ok(new Dictionary<string, object?>
        {
            ["has_users"] = hasUsers,
            ["roles"] = new[] { "admin", "consultor", "lector" },
        });
    }

    // -------------------- POST /auth/bootstrap --------------------
    [HttpPost("bootstrap")]
    [AllowAnonymous]
    public async Task<IActionResult> Bootstrap([FromBody] BootstrapRequest payload, CancellationToken ct)
    {
        if (!config.AuthBootstrapEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { detail = "Initial admin bootstrap is disabled" });
        if (string.IsNullOrWhiteSpace(payload.Email) || (payload.Email?.Length ?? 0) < 3)
            return BadRequest(new { detail = "email is required (min 3)" });
        if (string.IsNullOrWhiteSpace(payload.FullName))
            return BadRequest(new { detail = "full_name is required" });
        if ((payload.Password?.Length ?? 0) < 8)
            return BadRequest(new { detail = "password must be at least 8 chars" });

        if (await users.CountBootstrapUsersAsync(ct) > 0)
            return Conflict(new { detail = "Initial admin already exists" });

        var email = payload.Email!.ToLowerInvariant();
        var hash = PasswordHasher.Hash(payload.Password!);
        var user = await users.UpsertBootstrapAdminAsync(email, payload.FullName!.Trim(), hash, ct);
        return Ok(TokenResponse(user));
    }

    // -------------------- POST /auth/login --------------------
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.Username) || string.IsNullOrEmpty(payload.Password))
            return Unauthorized(new { detail = "Invalid credentials" });

        var row = await users.GetForLoginAsync(payload.Username!, ct);
        if (row is null || !row.IsActive || !PasswordHasher.Verify(payload.Password!, row.PasswordHash))
            return Unauthorized(new { detail = "Invalid credentials" });

        var user = new PublicUser(row.UserId, row.Email, row.FullName, row.Role, row.IsActive, "");
        return Ok(TokenResponse(user));
    }

    // -------------------- GET /auth/me --------------------
    // FastAPI relee el usuario de BD y devuelve email/full_name/role (+user_id/is_active). El front
    // (AuthGate) consume full_name/email, así que se exponen además de sub/name (compatibilidad).
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var email = User.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(email))
        {
            var row = await users.GetForLoginAsync(email, ct);
            if (row is not null)
                return Ok(new Dictionary<string, object?>
                {
                    ["sub"] = row.Email,
                    ["user_id"] = row.UserId,
                    ["email"] = row.Email,
                    ["name"] = row.FullName,
                    ["full_name"] = row.FullName,
                    ["role"] = row.Role,
                    ["is_active"] = row.IsActive,
                });
        }
        // Fallback a claims (token válido sin fila): mantiene la sesión funcionando.
        var name = User.FindFirst("name")?.Value;
        return Ok(new Dictionary<string, object?>
        {
            ["sub"] = email,
            ["name"] = name,
            ["full_name"] = name,
            ["email"] = email,
            ["role"] = User.FindFirst(ClaimTypes.Role)?.Value,
        });
    }

    // -------------------- GET /auth/users --------------------
    [HttpGet("users")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ListUsers(CancellationToken ct) => Ok(await users.ListUsersAsync(ct));

    // -------------------- POST /auth/users --------------------
    [HttpPost("users")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreateUser([FromBody] UserCreateRequest payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.Email) || (payload.Email?.Length ?? 0) < 3)
            return BadRequest(new { detail = "email is required (min 3)" });
        if (string.IsNullOrWhiteSpace(payload.FullName))
            return BadRequest(new { detail = "full_name is required" });
        if ((payload.Password?.Length ?? 0) < 8)
            return BadRequest(new { detail = "password must be at least 8 chars" });
        if (!TryNormalizeRole(payload.Role, out var role))
            return BadRequest(new { detail = "Invalid role" });

        var email = payload.Email!.ToLowerInvariant();
        if (await users.EmailExistsAsync(email, null, ct))
            return Conflict(new { detail = "User already exists" });

        var created = await users.InsertUserAsync(email, payload.FullName!.Trim(), role, PasswordHasher.Hash(payload.Password!), ct);
        if (role is Roles.Consultor or Roles.Lector)
        {
            try { await users.ReplaceAssignmentsAsync(created.UserId, payload.ClientIds ?? [], ct); }
            catch (InvalidClientIdsException) { return BadRequest(new { detail = "Some client_ids do not exist" }); }
        }
        return Ok(created);
    }

    // -------------------- PUT /auth/users/{id} --------------------
    [HttpPut("users/{userId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> UpdateUser(int userId, [FromBody] UserUpdateRequest payload, CancellationToken ct)
    {
        string? email = payload.Email?.Trim().ToLowerInvariant();
        string? role = null;
        if (payload.Role is not null)
        {
            if (!TryNormalizeRole(payload.Role, out role))
                return BadRequest(new { detail = "Invalid role" });
        }
        if (payload.Email is null && payload.FullName is null && payload.Role is null
            && payload.Password is null && payload.IsActive is null)
            return BadRequest(new { detail = "No fields to update" });

        if (email is not null && await users.EmailExistsAsync(email, userId, ct))
            return Conflict(new { detail = "User already exists" });

        var hash = payload.Password is null ? null : PasswordHasher.Hash(payload.Password);
        var ok = await users.UpdateUserAsync(userId, email, payload.FullName?.Trim(), role, hash, payload.IsActive, ct);
        if (!ok) return NotFound(new { detail = "User not found" });
        return Ok(await users.GetByIdAsync(userId, ct));
    }

    // -------------------- DELETE /auth/users/{id} --------------------
    [HttpDelete("users/{userId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteUser(int userId, CancellationToken ct)
    {
        // Resolver el email ANTES de borrar para el chequeo de "no eliminarse a sí mismo".
        var target = await users.GetByIdAsync(userId, ct);
        if (target is null) return NotFound(new { detail = "User not found" });

        var actorEmail = (User.FindFirst("sub")?.Value ?? "").Trim().ToLowerInvariant();
        if (actorEmail.Length > 0 && actorEmail == target.Email.Trim().ToLowerInvariant())
            return BadRequest(new { detail = "No puede eliminar su propio usuario" });

        var deletedEmail = await users.DeleteUserAsync(userId, ct);
        if (deletedEmail is null) return NotFound(new { detail = "User not found" });
        return Ok(new { deleted = true, user_id = userId, email = deletedEmail });
    }

    // -------------------- GET /auth/users/{id}/clients --------------------
    [HttpGet("users/{userId:int}/clients")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> GetUserClients(int userId, CancellationToken ct)
    {
        var ids = await users.GetAssignmentsAsync(userId, ct);
        return Ok(new { user_id = userId, client_ids = ids });
    }

    // -------------------- PUT /auth/users/{id}/clients --------------------
    [HttpPut("users/{userId:int}/clients")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> SetUserClients(int userId, [FromBody] ClientAssignmentRequest payload, CancellationToken ct)
    {
        if (!await users.UserExistsAsync(userId, ct))
            return NotFound(new { detail = "User not found" });
        try
        {
            var ids = await users.ReplaceAssignmentsAsync(userId, payload.ClientIds ?? [], ct);
            return Ok(new { user_id = userId, client_ids = ids });
        }
        catch (InvalidClientIdsException)
        {
            return BadRequest(new { detail = "Some client_ids do not exist" });
        }
    }

    // -------------------- helpers --------------------
    private Dictionary<string, object?> TokenResponse(PublicUser user)
    {
        var issued = tokens.Create(user.Email, user.FullName, user.Role);
        return new Dictionary<string, object?>
        {
            ["access_token"] = issued.AccessToken,
            ["token_type"] = "bearer",
            ["expires_in"] = issued.ExpiresInSeconds,
            ["user_id"] = user.UserId,
            ["email"] = user.Email,
            ["full_name"] = user.FullName,
            ["role"] = user.Role,
            ["is_active"] = user.IsActive,
            ["created_at"] = user.CreatedAt,
        };
    }

    private static bool TryNormalizeRole(string? role, out string normalized)
    {
        normalized = (role ?? "").Trim().ToLowerInvariant();
        return Roles.Valid.Contains(normalized);
    }
}
