using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using static OptimizacionCostos.Api.Tests.Cdc.AccessReview.AccessReviewSnapshotTestHelper;

namespace OptimizacionCostos.Api.Tests.InformeValor;

public sealed class EstadoRbacTests
{
    // El armado del snapshot (Snap) vive en AccessReviewSnapshotTestHelper, compartido con
    // AccessReviewCompletitudTests: las dos reglas de completitud necesitan el mismo snapshot mínimo.

    [Fact]
    public void Sin_corrida_finalizada_el_archivo_es_obligatorio()
    {
        var r = EstadoRbac.Resolver(null, tieneSuscripcionesAdministradas: true);
        Assert.Equal(DisponibilidadRbac.NoDisponible, r.Disponibilidad);
        Assert.Contains("corrida", r.Motivo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sin_suscripciones_administradas_el_archivo_es_la_unica_salida()
    {
        var r = EstadoRbac.Resolver(null, tieneSuscripcionesAdministradas: false);
        Assert.Equal(DisponibilidadRbac.NoDisponible, r.Disponibilidad);
        Assert.Contains("suscripciones", r.Motivo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARM_en_error_es_no_disponible_aunque_haya_asignaciones()
    {
        var r = EstadoRbac.Resolver(Snap("partial", ("error", "ok")), true);
        Assert.Equal(DisponibilidadRbac.NoDisponible, r.Disponibilidad);
    }

    /// <summary>
    /// Una credencial Lighthouse fuerza graph_status='no_aplica' y la corrida cierra en
    /// 'partial' PARA SIEMPRE, por naturaleza del cliente y no por una falla. Gatear por el
    /// estado de la corrida dejaría a ese cliente pidiendo el archivo eternamente aunque su
    /// inventario ARM esté completo al 100%.
    /// </summary>
    [Fact]
    public void Lighthouse_con_ARM_completo_es_parcial_y_no_no_disponible()
    {
        var r = EstadoRbac.Resolver(Snap("partial", ("ok", "no_aplica")), true);
        Assert.Equal(DisponibilidadRbac.ParcialFaltaIdentidad, r.Disponibilidad);
        Assert.False(r.Ejes.EstadoCuentaMedido);
        Assert.False(r.Ejes.UltimoLoginMedido);
    }

    /// <summary>Sin licencia P1: el estado de cuenta SÍ se midió, el último login NO.</summary>
    [Fact]
    public void Sin_licencia_p1_separa_los_dos_ejes()
    {
        var r = EstadoRbac.Resolver(Snap("partial", ("ok", "sin_licencia_p1")), true);
        Assert.Equal(DisponibilidadRbac.ParcialFaltaIdentidad, r.Disponibilidad);
        Assert.True(r.Ejes.EstadoCuentaMedido);
        Assert.False(r.Ejes.UltimoLoginMedido);
    }

    [Fact]
    public void Todo_ok_es_completo_con_los_dos_ejes_medidos()
    {
        var r = EstadoRbac.Resolver(Snap("ok", ("ok", "ok")), true);
        Assert.Equal(DisponibilidadRbac.Completo, r.Disponibilidad);
        Assert.True(r.Ejes.EstadoCuentaMedido);
        Assert.True(r.Ejes.UltimoLoginMedido);
    }

    /// <summary>
    /// IMPORTANTE 5 de la revisión de rama: los cinco motivos le decían al consultor que suba el
    /// Excel de RBAC, y InformeValorController.Subir rechaza ese kind con un 400 ("llega en la
    /// entrega 2"). Ningún motivo debería prometer una carga que hoy falla. Cubre los cinco casos
    /// de Resolver que producen un Motivo (NoDisponible x3, ParcialFaltaIdentidad x2); el sexto
    /// (Completo) no tiene nada que prometer.
    /// </summary>
    [Theory]
    [MemberData(nameof(TodosLosMotivos))]
    public void Ningun_motivo_promete_subir_el_excel_de_rbac(string motivo)
    {
        Assert.DoesNotContain("sube", motivo, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> TodosLosMotivos()
    {
        yield return [EstadoRbac.Resolver(null, tieneSuscripcionesAdministradas: false).Motivo];
        yield return [EstadoRbac.Resolver(null, tieneSuscripcionesAdministradas: true).Motivo];
        yield return [EstadoRbac.Resolver(Snap("partial", ("error", "ok")), true).Motivo];
        yield return [EstadoRbac.Resolver(Snap("partial", ("ok", "no_aplica")), true).Motivo];
        yield return [EstadoRbac.Resolver(Snap("partial", ("ok", "sin_licencia_p1")), true).Motivo];
    }
}
