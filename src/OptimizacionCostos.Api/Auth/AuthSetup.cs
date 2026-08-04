using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Auth;

public static class AuthSetup
{
    /// <summary>
    /// Configura Bearer JWT compatible con los tokens del FastAPI:
    /// HS256 firmado con JWT_SECRET, sin issuer/audience, claim "sub" = email.
    /// Tras validar la firma, re-consulta el rol VIVO en dbo.app_users
    /// (el rol del token no autoriza por si solo). Asi los tokens del login
    /// actual sirven sin tocar el login.
    /// </summary>
    public static IServiceCollection AddBitJwtAuth(this IServiceCollection services, AppConfig config)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.JwtSecret));

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
                    OnMessageReceived = ctx =>
                    {
                        if (string.IsNullOrEmpty(ctx.Token) && ctx.Request.Cookies.TryGetValue(BrowserSession.SessionCookieName, out var token))
                            ctx.Token = token;
                        return Task.CompletedTask;
                    },
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
