using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

/// <summary>
/// Completitud del inventario ARM. La gemela del eje de identidad (<see cref="AccessReviewAccountBuilder.GraphComplete"/>)
/// ya vive pública al lado; este archivo cubre la promoción de <c>ArmComplete</c> al mismo lugar.
/// <para>
/// El segundo test de la Task 1 original (los dos ejes de Graph ante sin_licencia_p1) depende de
/// <c>EstadoRbac.LoginMedido</c>, que se crea en la Task 2: queda deliberadamente afuera de este
/// archivo para no acoplar las tareas (ver informe de la Task 1).
/// </para>
/// </summary>
public sealed class AccessReviewCompletitudTests
{
    private static AccessReviewSnapshot Snap(string runStatus, params (string Arm, string Graph)[] creds) =>
        new(new AccessRunRef(1, 1, runStatus, null, null, null, null),
            [.. creds.Select((c, i) => new AccessCredStatus(i + 1, null, c.Arm, c.Graph, null))],
            [], [], []);

    [Fact]
    public void ArmComplete_es_publico_y_exige_todas_las_credenciales_en_ok()
    {
        Assert.True(AccessReviewAccountBuilder.ArmComplete(Snap("ok", ("ok", "ok"), ("ok", "no_aplica"))));
        Assert.False(AccessReviewAccountBuilder.ArmComplete(Snap("ok", ("ok", "ok"), ("error", "ok"))));
        Assert.False(AccessReviewAccountBuilder.ArmComplete(Snap("error", ("ok", "ok"))));
    }
}
