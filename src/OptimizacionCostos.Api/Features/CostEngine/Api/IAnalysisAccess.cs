using System.Security.Claims;

namespace OptimizacionCostos.Api.Features.CostEngine.Api;

/// <summary>
/// Control de acceso por cliente. Port de app/access_control.py.
///
/// Reglas:
///   - admin: acceso global (ve y opera sobre todos los clientes) -> AccessibleClientIds = null.
///   - consultor / lector: solo los clientes asignados explicitamente en
///     dbo.user_client_assignment. Sin asignaciones no ven ningun cliente (deny por defecto).
/// </summary>
public interface IAnalysisAccess
{
    /// <summary>
    /// IDs de clientes accesibles para el usuario. <c>null</c> = acceso global (admin);
    /// el llamador debe interpretarlo como "todos". Para roles restringidos devuelve el
    /// conjunto (posiblemente vacio) de client_id asignados.
    /// </summary>
    Task<IReadOnlySet<int>?> AccessibleClientIdsAsync(ClaimsPrincipal user, CancellationToken ct = default);

    /// <summary>
    /// Verifica que el usuario pueda acceder al cliente dueño del analisis.
    /// 404 si el analisis no existe; 403 si el cliente no es accesible.
    /// </summary>
    Task<AccessCheck> AssertAnalysisAccessAsync(ClaimsPrincipal user, int analysisId, CancellationToken ct = default);

    /// <summary>
    /// Verifica que el usuario pueda acceder al cliente dueño del cost_result.
    /// 404 si el cost_result no existe; 403 si el cliente no es accesible.
    /// </summary>
    Task<AccessCheck> AssertCostResultAccessAsync(ClaimsPrincipal user, int costResultId, CancellationToken ct = default);

    /// <summary>
    /// Verifica acceso a un cliente por su id (port de assert_client_access). 403 si no es
    /// accesible. Usado por credenciales/suscripciones. admin = global.
    /// </summary>
    Task<AccessCheck> AssertClientAccessAsync(ClaimsPrincipal user, int clientId, CancellationToken ct = default);

    /// <summary>
    /// Verifica que el usuario pueda acceder al cliente dueño del archivo (analysis_files).
    /// Port de assert_file_access. 404 si el archivo no existe; 403 si el cliente no es accesible.
    /// </summary>
    Task<AccessCheck> AssertFileAccessAsync(ClaimsPrincipal user, int fileId, CancellationToken ct = default);
}
