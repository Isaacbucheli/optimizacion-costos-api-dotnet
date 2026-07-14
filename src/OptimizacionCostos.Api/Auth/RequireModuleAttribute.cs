using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace OptimizacionCostos.Api.Auth;

public enum ModuleAccess { View, Edit }

/// <summary>
/// Exige permiso del módulo (matriz rol×módulo) además del [Authorize] del controller.
/// Solo RESTRINGE, nunca amplía: los [Authorize(Roles=Admin)] existentes se mantienen.
/// A nivel clase gatea la vista del módulo; en acciones de mutación se agrega la
/// variante Edit (ambos filtros corren). admin pasa siempre; lector jamás edita.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireModuleAttribute(string moduleKey, ModuleAccess access = ModuleAccess.View)
    : Attribute, IAsyncAuthorizationFilter
{
    public string ModuleKey { get; } = moduleKey;
    public ModuleAccess Access { get; } = access;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            // [Authorize] del controller ya devuelve 401 antes; esto es defensa extra.
            context.Result = new ObjectResult(new { detail = "Not authenticated" })
            { StatusCode = StatusCodes.Status401Unauthorized };
            return;
        }

        var role = user.FindFirst(ClaimTypes.Role)?.Value ?? "";
        var perms = context.HttpContext.RequestServices.GetRequiredService<IModulePermissionService>();
        var ok = await perms.HasAccessAsync(role, ModuleKey, Access == ModuleAccess.Edit, context.HttpContext.RequestAborted);
        if (!ok)
        {
            context.Result = new ObjectResult(new { detail = "Módulo no permitido para su perfil" })
            { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}
