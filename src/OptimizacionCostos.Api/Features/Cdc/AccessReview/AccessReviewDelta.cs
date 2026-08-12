namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>Un acceso que apareció o desapareció respecto de la corrida anterior.</summary>
public sealed record AccessDeltaItem(
    string AccessKey, string PrincipalObjectId, string? DisplayName, string PrincipalType,
    string RoleName, string? RoleClass, string ScopeLevel, string? SubscriptionName, string Environment);

/// <summary>
/// Diferencia entre la corrida actual y la anterior finalizada. `PreviousRunId` null = primera
/// corrida del cliente: no hay novedad que reportar (distinto de "no cambió nada").
/// <para>
/// Los ejes van en NULL cuando su insumo estuvo incompleto en CUALQUIERA de las dos corridas
/// comparadas: null = "no comparable", que no es lo mismo que "no cambió". Sin esa distinción,
/// comparar contra una corrida parcial hacía que la franja afirmara en rojo "Global Admins nuevos:
/// &lt;todos&gt;" cuando nadie recibió nada: el eje simplemente estaba vacío en la corrida anterior
/// porque no se pudo leer el directorio.
/// </para>
/// </summary>
public sealed record AccessReviewDelta(
    int? PreviousRunId, DateTimeOffset? PreviousFinishedAt,
    IReadOnlyList<AccessDeltaItem>? NuevosAccesos,
    IReadOnlyList<AccessDeltaItem>? AccesosRemovidos,
    IReadOnlyList<string>? NuevosGlobalAdmins,
    IReadOnlyList<string>? GlobalAdminsRemovidos,
    int? NuevosGuests, int? GuestsRemovidos)
{
    public bool HasPrevious => PreviousRunId is not null;

    /// <summary>Los accesos (eje ARM) son comparables.</summary>
    public bool AccesosComparables => NuevosAccesos is not null;

    /// <summary>Los ejes de directorio (Global Admins, invitados) son comparables.</summary>
    public bool DirectorioComparable => NuevosGlobalAdmins is not null;

    public static AccessReviewDelta Empty { get; } = new(null, null, null, null, null, null, null, null);
}

/// <summary>
/// Compara dos snapshots por la MISMA clave de acceso que usa el bloque 3 para las decisiones. Es
/// importante que sea la misma: si el delta usara el `roleDefinitionId` completo, un acceso heredado
/// aparecería como "nuevo" en cada corrida porque ARM lo prefija con la suscripción consultada.
/// Puro (sin BD): lo consumen el response, los hallazgos y el Excel.
/// </summary>
public static class AccessReviewDeltaBuilder
{
    public static AccessReviewDelta Build(AccessReviewSnapshot current, AccessReviewSnapshot? previous)
    {
        if (previous is null) return AccessReviewDelta.Empty;

        // Un eje solo se compara si su insumo estuvo completo en LAS DOS corridas. Si en una de ellas
        // fallo, su ausencia no significa que algo se removio ni que algo aparecio.
        var armOk = AccessReviewAccountBuilder.ArmComplete(current) && AccessReviewAccountBuilder.ArmComplete(previous);
        var dirOk = AccessReviewAccountBuilder.GraphComplete(current)
                    && AccessReviewAccountBuilder.GraphComplete(previous);

        if (!armOk && !dirOk)
            return new AccessReviewDelta(previous.Run.RunId, previous.Run.FinishedAt,
                null, null, null, null, null, null);

        var antes = Index(previous);
        var ahora = Index(current);

        var nuevos = ahora.Where(kv => !antes.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var removidos = antes.Where(kv => !ahora.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();

        var gaAntes = previous.GlobalAdmins.Select(g => g.ObjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gaAhora = current.GlobalAdmins.Select(g => g.ObjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new AccessReviewDelta(
            previous.Run.RunId, previous.Run.FinishedAt,
            armOk ? [.. nuevos.OrderByDescending(i => Weight(i.RoleClass)).ThenBy(i => i.DisplayName ?? i.PrincipalObjectId)] : null,
            armOk ? [.. removidos.OrderByDescending(i => Weight(i.RoleClass)).ThenBy(i => i.DisplayName ?? i.PrincipalObjectId)] : null,
            dirOk ? [.. current.GlobalAdmins.Where(g => !gaAntes.Contains(g.ObjectId))
                .Select(g => g.DisplayName ?? g.Upn ?? g.ObjectId).Order()] : null,
            dirOk ? [.. previous.GlobalAdmins.Where(g => !gaAhora.Contains(g.ObjectId))
                .Select(g => g.DisplayName ?? g.Upn ?? g.ObjectId).Order()] : null,
            NuevosGuests: dirOk ? current.Guests.Count(g => previous.Guests.All(p => !string.Equals(p.ObjectId, g.ObjectId, StringComparison.OrdinalIgnoreCase))) : null,
            GuestsRemovidos: dirOk ? previous.Guests.Count(g => current.Guests.All(p => !string.Equals(p.ObjectId, g.ObjectId, StringComparison.OrdinalIgnoreCase))) : null);
    }

    /// <summary>Lo elevado primero: es lo que hay que mirar de lo que cambió.</summary>
    private static int Weight(string? roleClass) => roleClass switch
    {
        AccessReviewRoleClassifier.Owner => 3,
        AccessReviewRoleClassifier.OtorgaAccesos => 2,
        AccessReviewRoleClassifier.EscrituraTotal => 1,
        _ => 0,
    };

    private static Dictionary<string, AccessDeltaItem> Index(AccessReviewSnapshot s)
    {
        var map = new Dictionary<string, AccessDeltaItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in s.Assignments)
        {
            var key = AccessReviewAccessKey.For(a.PrincipalObjectId, a.RoleDefinitionId, a.Scope);
            // Primera fila gana: las derivadas de grupo comparten el acceso efectivo.
            map.TryAdd(key, new AccessDeltaItem(
                key, a.PrincipalObjectId, a.DisplayName, a.PrincipalType, a.RoleName, a.RoleClass,
                a.ScopeLevel, a.SubscriptionName,
                a.Environment ?? AccessReviewEnvironment.Classify(a.SubscriptionName)));
        }
        return map;
    }
}
