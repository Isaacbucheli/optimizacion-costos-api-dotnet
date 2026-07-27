using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>Clase de privilegio de un rol + si es personalizado.</summary>
public sealed record RoleClassification(string RoleClass, bool IsCustom);

/// <summary>
/// Clasifica una definición de rol de Azure por su clase de privilegio, derivándola de los
/// permisos reales (`properties.permissions`) y NO de una tabla de GUIDs ni del nombre del rol.
/// Es exacto, independiente del idioma del tenant, y clasifica correctamente los roles
/// personalizados. Puro: no toca red ni BD (el payload lo trae ya `AccessReviewArmClient`).
/// </summary>
public static class AccessReviewRoleClassifier
{
    public const string Owner = "owner";
    public const string OtorgaAccesos = "otorga_accesos";
    public const string EscrituraTotal = "escritura_total";
    public const string EscrituraServicio = "escritura_servicio";
    public const string Lectura = "lectura";

    /// <summary>Acción sonda: es lo único que separa a Owner de Contributor (ambos tienen "*").</summary>
    private const string GrantProbe = "Microsoft.Authorization/roleAssignments/write";

    private static readonly ConcurrentDictionary<string, Regex> PatternCache = new();

    /// <summary>Elevado = puede otorgar accesos o escribir sobre cualquier provider. Deja fuera a
    /// los *Contributor de servicio a propósito: incluirlos diluye la señal de riesgo.</summary>
    public static bool IsElevated(string? roleClass) =>
        roleClass is Owner or OtorgaAccesos or EscrituraTotal;

    /// <summary>
    /// GUID de la definición de rol, sin la ruta. Necesario porque ARM prefija el `roleDefinitionId`
    /// con la suscripción CONSULTADA: una asignación heredada (root o management group) vuelve una vez
    /// por suscripción, cada vez con un id distinto para el mismo rol. Comparar ids completos hace que
    /// el mismo rol se cuente N veces.
    /// </summary>
    public static string RoleKey(string roleDefinitionId)
    {
        var i = roleDefinitionId.LastIndexOf('/');
        return i >= 0 && i < roleDefinitionId.Length - 1 ? roleDefinitionId[(i + 1)..] : roleDefinitionId;
    }

    public static RoleClassification Classify(JsonElement properties)
    {
        var isCustom = properties.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            && string.Equals(t.GetString(), "CustomRole", StringComparison.OrdinalIgnoreCase);

        List<string> actions = [], notActions = [], dataActions = [];
        if (properties.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
            foreach (var block in perms.EnumerateArray())
            {
                Collect(block, "actions", actions);
                Collect(block, "notActions", notActions);
                Collect(block, "dataActions", dataActions);
            }

        var puedeOtorgar = Granted(GrantProbe, actions, notActions);
        var escrituraEnActions = actions.Where(IsWritePattern).ToList();
        var escrituraTotal = escrituraEnActions.Any(SpansProviders);
        // `dataActions` cuenta para escritura: un rol de plano de datos (p. ej. Storage File Data SMB
        // Share Elevated Contributor) tiene `actions` de solo lectura y toda la escritura ahí.
        // `notDataActions` se ignora a propósito: en la práctica acota, no elimina, la escritura, y
        // comparar patrón contra patrón daría una precisión falsa.
        var hayEscritura = escrituraEnActions.Count > 0 || dataActions.Any(IsWritePattern);

        var roleClass = (puedeOtorgar, escrituraTotal, hayEscritura) switch
        {
            (true, true, _) => Owner,
            (true, _, _) => OtorgaAccesos,
            (_, true, _) => EscrituraTotal,
            (_, _, true) => EscrituraServicio,
            _ => Lectura,
        };
        return new RoleClassification(roleClass, isCustom);
    }

    private static void Collect(JsonElement block, string prop, List<string> into)
    {
        if (!block.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var el in arr.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                into.Add(s);
    }

    /// <summary>La acción está concedida si algún patrón de actions la cubre y ninguno de notActions
    /// la excluye (notActions tiene precedencia, igual que en Azure).</summary>
    private static bool Granted(string action, List<string> actions, List<string> notActions) =>
        Covers(actions, action) && !Covers(notActions, action);

    private static bool Covers(List<string> patterns, string action) =>
        patterns.Any(p => ToRegex(p).IsMatch(action));

    /// <summary>Patrón de escritura: su último segmento es "*", "write", "delete" o "action".
    /// Los de solo lectura terminan en "read" (p. ej. "*/read", "Microsoft.Compute/*/read").</summary>
    private static bool IsWritePattern(string pattern)
    {
        var last = pattern[(pattern.LastIndexOf('/') + 1)..];
        return last is "*" || last.Equals("write", StringComparison.OrdinalIgnoreCase)
            || last.Equals("delete", StringComparison.OrdinalIgnoreCase)
            || last.Equals("action", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Abarca varios providers si el primer segmento del patrón lleva comodín:
    /// "*" y "*/write" sí; "Microsoft.Compute/*" no.</summary>
    private static bool SpansProviders(string pattern)
    {
        var slash = pattern.IndexOf('/');
        var first = slash < 0 ? pattern : pattern[..slash];
        return first.Contains('*');
    }

    private static Regex ToRegex(string pattern) => PatternCache.GetOrAdd(pattern, p =>
        new Regex($"^{Regex.Escape(p).Replace("\\*", ".*")}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
}
