using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Pendientes;

namespace OptimizacionCostos.Api.Tests.Pendientes;

/// <summary>
/// Área → módulo de permiso y normalización de los valores de dominio. El detalle que importa:
/// el estado en la BD del tablero es EN_PROGRESO con guion bajo (auditado 2026-07-28), así que
/// "en progreso" tiene que terminar con guion bajo o crearíamos un cuarto estado que nadie filtra.
/// </summary>
public sealed class PendientesDomainTests
{
    [Theory]
    [InlineData("CDC", "CDC", Modules.PendientesCdc)]
    [InlineData("cdc", "CDC", Modules.PendientesCdc)]
    [InlineData(" Cdc ", "CDC", Modules.PendientesCdc)]
    [InlineData("INFRA", "INFRA", Modules.PendientesInfra)]
    [InlineData("infra", "INFRA", Modules.PendientesInfra)]
    public void Resolve_acepta_las_dos_areas_case_insensitive(string raw, string area, string moduleKey)
    {
        var resolved = PendientesArea.Resolve(raw);
        Assert.NotNull(resolved);
        Assert.Equal(area, resolved!.Value.Area);
        Assert.Equal(moduleKey, resolved.Value.ModuleKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PROD")]
    [InlineData("CDC2")]
    public void Resolve_rechaza_lo_que_no_es_area(string? raw) =>
        Assert.Null(PendientesArea.Resolve(raw));

    [Fact]
    public void Las_dos_claves_estan_en_el_catalogo_de_modulos()
    {
        Assert.Contains(Modules.PendientesCdc, Modules.ValidKeys);
        Assert.Contains(Modules.PendientesInfra, Modules.ValidKeys);
    }

    [Theory]
    [InlineData("en progreso", "EN_PROGRESO")]
    [InlineData("EN PROGRESO", "EN_PROGRESO")]
    [InlineData("en_progreso", "EN_PROGRESO")]
    [InlineData("abierto", "ABIERTO")]
    [InlineData(" Cerrado ", "CERRADO")]
    public void Normalize_lleva_el_estado_al_valor_exacto_de_la_BD(string raw, string expected) =>
        Assert.Equal(expected, PendientesDomain.Normalize(raw, PendientesDomain.Estados));

    [Theory]
    [InlineData("PENDIENTE_URGENTE")]
    [InlineData("REABIERTO")]
    [InlineData("")]
    [InlineData(null)]
    public void Normalize_devuelve_null_fuera_de_la_lista_blanca(string? raw) =>
        Assert.Null(PendientesDomain.Normalize(raw, PendientesDomain.Estados));

    [Fact]
    public void Los_defaults_son_valores_validos()
    {
        Assert.Contains(PendientesDomain.TipoDefault, PendientesDomain.Tipos);
        Assert.Contains(PendientesDomain.PrioridadDefault, PendientesDomain.Prioridades);
        Assert.Contains(PendientesDomain.EstadoDefault, PendientesDomain.Estados);
    }
}
