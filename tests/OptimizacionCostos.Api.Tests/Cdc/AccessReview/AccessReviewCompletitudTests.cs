using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using static OptimizacionCostos.Api.Tests.Cdc.AccessReview.AccessReviewSnapshotTestHelper;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

/// <summary>
/// Completitud del inventario ARM. La gemela del eje de identidad (<see cref="AccessReviewAccountBuilder.GraphComplete"/>)
/// ya vive pública al lado; este archivo cubre la promoción de <c>ArmComplete</c> al mismo lugar.
/// <para>
/// El segundo test de la Task 1 original (los dos ejes de Graph ante sin_licencia_p1) depende de
/// <c>EstadoRbac.LoginMedido</c>, que se crea en la Task 2: queda deliberadamente afuera de este
/// archivo para no acoplar las tareas (ver informe de la Task 1).
/// </para>
/// <para>
/// El armado del snapshot (<c>Snap</c>) vive en <see cref="AccessReviewSnapshotTestHelper"/>,
/// compartido con <c>EstadoRbacTests</c> del Informe de Valor: las dos reglas de completitud
/// necesitan exactamente el mismo snapshot mínimo.
/// </para>
/// </summary>
public sealed class AccessReviewCompletitudTests
{
    [Fact]
    public void ArmComplete_es_publico_y_exige_todas_las_credenciales_en_ok()
    {
        Assert.True(AccessReviewAccountBuilder.ArmComplete(Snap("ok", ("ok", "ok"), ("ok", "no_aplica"))));
        Assert.False(AccessReviewAccountBuilder.ArmComplete(Snap("ok", ("ok", "ok"), ("error", "ok"))));
        Assert.False(AccessReviewAccountBuilder.ArmComplete(Snap("error", ("ok", "ok"))));
    }

    /// <summary>
    /// Red sin base de datos para la unica clausula que justifica el metodo: si alguien la borra
    /// o pega de vuelta el <c>run_id &lt;</c> de su gemelo <c>GetPreviousFinishedRunAsync</c>, un
    /// cliente con una corrida en error recibiria un inventario a medias presentado como completo,
    /// sin que se vea en ninguna pantalla.
    /// </summary>
    [Fact]
    public void GetLatestFinishedRunAsync_filtra_estado_y_no_filtra_por_run_id()
    {
        Assert.Contains("status IN ('ok','partial')", SqlAccessReviewStore.LatestFinishedRunSql, StringComparison.Ordinal);
        Assert.DoesNotContain("run_id <", SqlAccessReviewStore.LatestFinishedRunSql, StringComparison.Ordinal);
    }
}
