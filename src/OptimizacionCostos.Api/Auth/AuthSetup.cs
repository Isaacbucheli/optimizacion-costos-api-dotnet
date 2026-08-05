using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Auth;

public static class AuthSetup
{
    /// <summary>
    /// Minimo de bytes para JWT_SECRET. RFC 7518 3.2 pide que la llave de un HMAC sea de al menos
    /// el tamano de la salida del hash: 32 bytes para HS256.
    /// </summary>
    public const int MinSecretBytes = 32;

    /// <summary>
    /// Configura Bearer JWT compatible con los tokens del FastAPI:
    /// HS256 firmado con JWT_SECRET, sin issuer/audience, claim "sub" = email.
    /// Tras validar la firma, re-consulta el rol VIVO en dbo.app_users
    /// (el rol del token no autoriza por si solo). Asi los tokens del login
    /// actual sirven sin tocar el login.
    /// </summary>
    public static IServiceCollection AddBitJwtAuth(this IServiceCollection services, AppConfig config)
    {
        // Un JWT_SECRET corto pero no vacio pasaba en silencio: SymmetricSecurityKey acepta 4 bytes
        // (KeySize=32 bits) y HMACSHA256 acepta hasta una llave vacia, asi que la API arrancaba
        // normal firmando tokens que se rompen por fuerza bruta, sin dejar rastro en ningun log.
        // Preferimos no arrancar: la caida se nota de inmediato en /health, una firma debil no.
        var keyBytes = Encoding.UTF8.GetBytes(config.JwtSecret);
        if (keyBytes.Length < MinSecretBytes)
            throw new InvalidOperationException(
                $"JWT_SECRET debe ser un valor aleatorio de al menos {MinSecretBytes} bytes; " +
                $"el configurado tiene {keyBytes.Length}. La API no arranca con una firma debil.");

        var key = new SymmetricSecurityKey(keyBytes);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false; // conservar "sub" tal cual
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = "sub",
                    RoleClaimType = ClaimTypes.Role,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var email = ctx.Principal?.FindFirst("sub")?.Value;
                        if (string.IsNullOrWhiteSpace(email))
                        {
                            ctx.Fail("Invalid or expired token");
                            return;
                        }

                        var directory = ctx.HttpContext.RequestServices.GetRequiredService<IUserDirectory>();
                        var user = await directory.FindActiveByEmailAsync(email, ctx.HttpContext.RequestAborted);
                        if (user is null || !Roles.Valid.Contains(user.Role))
                        {
                            ctx.Fail("Invalid or expired token");
                            return;
                        }

                        // El rol que autoriza es el de BD, no el del token.
                        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
                        foreach (var stale in identity.FindAll(ClaimTypes.Role).ToList())
                            identity.RemoveClaim(stale);
                        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
                        identity.AddClaim(new Claim("name", user.FullName));
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }
}
