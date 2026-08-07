using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

/// <summary>
/// Armado mínimo de <see cref="AccessReviewSnapshot"/> para pruebas de las dos reglas de
/// completitud (<see cref="AccessReviewAccountBuilder.ArmComplete"/> y
/// <see cref="AccessReviewAccountBuilder.GraphComplete"/>): una credencial por cada par de estados,
/// sin asignaciones, invitados ni admins globales, que es todo lo que esas reglas miran.
/// <para>
/// Compartido entre <see cref="AccessReviewCompletitudTests"/> y <c>EstadoRbacTests</c> (Informe de
/// Valor) para no duplicar el armado del snapshot.
/// </para>
/// </summary>
internal static class AccessReviewSnapshotTestHelper
{
    public static AccessReviewSnapshot Snap(string runStatus, params (string Arm, string Graph)[] creds) =>
        new(new AccessRunRef(1, 1, runStatus, null, null, null, null),
            [.. creds.Select((c, i) => new AccessCredStatus(i + 1, null, c.Arm, c.Graph, null))],
            [], [], []);
}
