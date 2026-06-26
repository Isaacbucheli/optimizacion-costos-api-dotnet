namespace OptimizacionCostos.Api.Auth;

/// <summary>Usuario vivo de dbo.app_users (la fuente de verdad del rol, no el token).</summary>
public sealed record AppUser(string Email, string FullName, string Role, bool IsActive);

public interface IUserDirectory
{
    /// <summary>Devuelve el usuario activo por email, o null si no existe / esta inactivo.</summary>
    Task<AppUser?> FindActiveByEmailAsync(string email, CancellationToken ct = default);
}
