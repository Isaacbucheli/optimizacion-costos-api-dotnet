using System.Security.Cryptography;
using System.Text;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>
/// Clave estable de un acceso efectivo, para poder decidir sobre él y que la decisión sobreviva a la
/// re-sincronización. Se hashea porque el `scope` es NVARCHAR(1000) y no cabe en un índice de SQL
/// Server junto al resto (límite de 900 bytes).
///
/// Dos cuidados que si se rompen hacen "perder" decisiones sin ningún error visible:
///  - Se usa el GUID del rol, no el `roleDefinitionId` completo: ARM lo prefija con la suscripción
///    consultada, así que una asignación heredada vuelve con N ids distintos para el mismo rol.
///  - Se normaliza a minúsculas: ARM no garantiza el casing de scopes ni de GUID.
/// </summary>
public static class AccessReviewAccessKey
{
    /// <param name="roleDefinitionIdOrKey">Id completo de ARM o el GUID pelado; da lo mismo.</param>
    public static string For(string principalObjectId, string roleDefinitionIdOrKey, string scope)
    {
        var roleKey = AccessReviewRoleClassifier.RoleKey(roleDefinitionIdOrKey ?? "");
        // El separador evita que ("ab","c") y ("a","bc") colisionen.
        return Hash($"{Norm(principalObjectId)}|{Norm(roleKey)}|{Norm(scope)}");
    }

    /// <summary>Clave de una decisión sobre un hallazgo de umbral, que no tiene accesos individuales
    /// que marcar. El prefijo la separa del espacio de claves de accesos.</summary>
    public static string ForFinding(string findingKey) => Hash($"finding|{Norm(findingKey)}");

    private static string Norm(string? value) => (value ?? "").Trim().ToLowerInvariant();

    // ToHexStringLower es .NET 9; acá el target es net8.0.
    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
