using System.Globalization;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Trae las asignaciones RBAC de la última corrida finalizada (<c>ok</c> o <c>partial</c>) de
/// Revisión de accesos de un cliente.
///
/// <para><b>No deduplica acá.</b> Un <c>SELECT</c> plano sobre <c>cdc_access_assignment</c> infla
/// las asignaciones ~21%: ARM repite cada asignación heredada de <c>root</c> o
/// <c>management_group</c> una vez por suscripción consultada (medido en un cliente real: 6013
/// filas crudas, 1068 duplicados exactos de 124 asignaciones). La deduplicación ya vive en
/// <see cref="AccessReviewAssignments.Distinct"/> y corre dentro de
/// <see cref="IAccessReviewStore.GetSnapshotAsync"/> al leer: este recolector reusa ese método en
/// vez de reimplementarla, así que <see cref="Mapear"/> recibe <see cref="AccessAssignmentRow"/>
/// que ya están deduplicadas y solo proyecta.</para>
///
/// <para>No asegura el schema de Revisión de accesos ni administra la conexión: eso es
/// responsabilidad de <see cref="IAccessReviewStore"/> y del ensamblador que llama a este
/// recolector junto a los demás del informe.</para>
/// </summary>
public static class RbacRecolector
{
    public static async Task<IReadOnlyList<RbacFila>> LeerAsync(
        IAccessReviewStore store, int clientId, CancellationToken ct = default)
    {
        var run = await store.GetLatestFinishedRunAsync(clientId, ct);
        if (run is null) return [];

        var snapshot = await store.GetSnapshotAsync(run.RunId, ct);
        if (snapshot is null) return [];

        return Mapear(snapshot);
    }

    /// <summary>Proyecta las asignaciones ya deduplicadas del snapshot. Internal, visible para
    /// los tests (mismo mecanismo que <see cref="AdvisorRecolector.MapearFila"/>): la única
    /// entrada pública del recolector es <see cref="LeerAsync"/>.</summary>
    internal static IReadOnlyList<RbacFila> Mapear(AccessReviewSnapshot snapshot) =>
        [.. snapshot.Assignments.Select(MapearFila)];

    internal static RbacFila MapearFila(AccessAssignmentRow a) => new(
        PrincipalObjectId: a.PrincipalObjectId,
        Nombre: a.DisplayName,
        Login: a.Login,
        PrincipalType: a.PrincipalType,
        Rol: a.RoleName,
        RoleKey: AccessReviewRoleClassifier.RoleKey(a.RoleDefinitionId),
        Scope: a.Scope,
        ScopeLevel: a.ScopeLevel,
        SubscriptionId: a.SubscriptionId,
        SubscriptionName: a.SubscriptionName,
        // SeenInSubscriptions ya viene lleno por AccessReviewAssignments.Distinct (root y
        // management_group con el conjunto completo, el resto con su propia suscripción). El
        // fallback es solo para una fila cruda que llegara sin pasar por el dedup.
        SuscripcionesAlcanzadas: a.SeenInSubscriptions ?? [a.SubscriptionId],
        CuentaHabilitada: a.AccountEnabled,
        UltimoLoginTexto: a.LastSignIn?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        ViaGrupoId: a.ViaGroupId);
}
