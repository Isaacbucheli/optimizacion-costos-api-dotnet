using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using static OptimizacionCostos.Api.Tests.Cdc.AccessReview.AccessReviewSnapshotTestHelper;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

/// <summary>
/// Completitud del inventario ARM. La gemela del eje de identidad (<see cref="AccessReviewAccountBuilder.GraphComplete"/>)
/// ya vive pública al lado; este archivo cubre la promoción de <c>ArmComplete</c> al mismo lugar.
/// <para>
/// El segundo test de la Task 1 original (los dos ejes de Graph ante sin_licencia_p1) depende de
/// <see cref="AccessReviewAccountBuilder.SignInComplete"/> (creado en la Task 2 como
/// <c>EstadoRbac.LoginMedido</c> del informe de valor, y promovido acá al lado de
/// <c>GraphComplete</c>/<c>ArmComplete</c> en la revisión de rama de la Entrega 2a): queda
/// deliberadamente afuera de este archivo para no acoplar las tareas (ver informe de la Task 1);
/// esa cobertura vive en <c>EstadoRbacTests</c>.
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
    /// El segundo test que la Task 1 dejó deliberadamente afuera (ver comentario de clase): ahora
    /// que <c>SignInComplete</c> es pública acá, esta clase puede cubrir su propia promoción igual
    /// que hace con <c>ArmComplete</c>. Más estricta que <c>GraphComplete</c>: exige además que
    /// NINGUNA credencial esté <c>sin_licencia_p1</c> (esa es la que expone el último login).
    /// </summary>
    [Fact]
    public void SignInComplete_exige_directorio_completo_y_ninguna_credencial_sin_licencia_p1()
    {
        Assert.True(AccessReviewAccountBuilder.SignInComplete(Snap("ok", ("ok", "ok"), ("ok", "ok"))));
        Assert.False(AccessReviewAccountBuilder.SignInComplete(Snap("partial", ("ok", "ok"), ("ok", "sin_licencia_p1"))));
        Assert.False(AccessReviewAccountBuilder.SignInComplete(Snap("partial", ("ok", "no_aplica"))));
        Assert.False(AccessReviewAccountBuilder.SignInComplete(Snap("error", ("ok", "ok"))));
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
