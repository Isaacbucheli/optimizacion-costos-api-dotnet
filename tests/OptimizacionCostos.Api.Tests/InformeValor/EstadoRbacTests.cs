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
    /// Este test nació invertido y con razón: mientras InformeValorController.Subir rechazaba el
    /// kind rbac con un 400, ningún motivo debía prometer esa carga. Ahora la carga existe, así
    /// que la verdad se dio vuelta y el test con ella. Lo que se fija ya no es una prohibición
    /// sino la correspondencia: el motivo tiene que decirle al consultor lo mismo que el módulo
    /// va a hacer con su archivo, porque un texto desalineado deja la función invisible (si dice
    /// que no puede subirlo) o promete algo que se descarta (si no dice que gana la base).
    /// Cubre los cinco casos de Resolver que producen un Motivo; el sexto (Completo) se verifica
    /// aparte, porque ahí el archivo no hace falta y mencionarlo sobraría.
    /// </summary>
    [Theory]
    [MemberData(nameof(TodosLosMotivos))]
    public void Cada_motivo_ofrece_el_excel_de_rbac_y_dice_si_es_obligatorio(string motivo)
    {
        Assert.Contains("Excel de RBAC", motivo, StringComparison.Ordinal);
        Assert.True(
            motivo.Contains("obligatorio", StringComparison.OrdinalIgnoreCase)
                || motivo.Contains("opcional", StringComparison.OrdinalIgnoreCase),
            "El motivo ofrece el Excel de RBAC sin decir si es obligatorio u opcional, y esos dos " +
            "casos piden acciones distintas del consultor: en uno el informe no puede armar el " +
            $"bloque de accesos sin el archivo, en el otro sí. Motivo: {motivo}");
    }

    /// <summary>
    /// El estado Completo es el único donde el archivo no hace falta, y además es el estado en el
    /// que gana la base y el módulo descarta lo que se suba. Ofrecerlo ahí mandaría al consultor a
    /// hacer un trabajo que se va a tirar.
    /// </summary>
    [Fact]
    public void El_motivo_de_completo_no_ofrece_el_excel_de_rbac()
    {
        var motivo = EstadoRbac.Resolver(Snap("ok", ("ok", "ok")), true).Motivo;
        Assert.DoesNotContain("Excel de RBAC", motivo, StringComparison.Ordinal);
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
