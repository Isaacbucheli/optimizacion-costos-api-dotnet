using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Pruebas del add-on de licencia de SQL Server en <see cref="ComputeVmCalculator"/>, todas nacidas
/// de la revisión del 2026-08-21 (ver <see cref="VmSizeVcpuTests"/> para el resolvedor de vCores).
///
/// Lo que se corrigió y acá queda fijado:
///   - El conteo de vCores sale de VmSizeVcpu, no del primer número del nombre del tamaño.
///   - Una VM DESASIGNADA no paga licencia (Azure deja de cobrar al desasignar); una VM apagada
///     desde el sistema operativo sí sigue pagando.
///   - Si no se puede determinar el conteo de vCores, la licencia NO se cotiza y queda una nota
///     visible, en vez de un cero silencioso.
///   - La edición Web cobra (0.008/vCore/hora) y la licencia DR no cobra.
/// </summary>
public sealed class ComputeVmSqlAddonTests
{
    private const double Hours = 730.0;

    private static ComputeVmCalculator Build(FakePricingConstants? consts = null)
        => new(new FakePriceRepository { GetVmPricesFn = (_, _, _) => new VmPrices(0.10, 600.0, 1500.0) },
               consts ?? new FakePricingConstants());

    private static ResourceRow Vm(string size, string powerState, string? edition, string? license, int? vcpuCount = null)
    {
        var pairs = new List<(string, object?)>
        {
            ("resource_id", 1), ("vm_size", size), ("location", "eastus2"), ("os_type", "Linux"),
            ("power_state", powerState), ("sql_image_sku", edition), ("sql_license_type", license),
        };
        if (vcpuCount is not null)
        {
            pairs.Add(("vcpu_count", vcpuCount.Value));
        }
        return Res.Row(pairs.ToArray());
    }

    // ------------------------------------------------------------------------------------
    // El bug original: un E32-16s_v3 Enterprise cobraba 32 vCores de licencia.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Nucleo_restringido_licencia_los_vcores_activos_y_no_los_del_tamano_base()
    {
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_E32-16s_v3", "VM running", "Enterprise", "PAYG")), 99)[0];

        var esperado = 0.375 * 16 * Hours;                          // 4,380.00 y no 8,760.00
        Assert.Equal(esperado, result.SqlAddonMonthly!.Value, 5);
        Assert.Equal(0.10 * Hours + esperado, result.PaygMonthly!.Value, 5);
        Assert.Contains("16 vCores", result.CalculationNotes);
    }

    [Fact]
    public void El_vcpu_count_del_inventario_manda_sobre_el_nombre()
    {
        // Nombre que deduciría 32; el inventario dice 16 y ese es el que se cobra.
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_E32-16s_v3", "VM running", "Enterprise", "PAYG", vcpuCount: 16)), 99)[0];

        Assert.Equal(0.375 * 16 * Hours, result.SqlAddonMonthly!.Value, 5);
        Assert.DoesNotContain("deducidos del nombre", result.CalculationNotes);
    }

    [Fact]
    public void Cuando_los_vcores_salen_del_nombre_la_nota_lo_dice()
    {
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_D8s_v3", "VM running", "Standard", "PAYG")), 99)[0];

        Assert.Equal(0.10 * 8 * Hours, result.SqlAddonMonthly!.Value, 5);
        Assert.Contains("8 vCores deducidos del nombre del tamaño", result.CalculationNotes);
    }

    // ------------------------------------------------------------------------------------
    // Desasignada vs apagada: Azure solo deja de cobrar la licencia al desasignar.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Vm_desasignada_no_paga_licencia()
    {
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_D4ads_v5", "VM deallocated", "Standard", "PAYG")), 99)[0];

        Assert.Null(result.SqlAddonMonthly);
        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);   // solo el compute referencial
        Assert.Contains("sin cargo de licencia (VM desasignada)", result.CalculationNotes);
    }

    [Fact]
    public void Vm_apagada_desde_el_so_sigue_pagando_licencia()
    {
        // "VM stopped" no es deallocated: Azure sigue facturando compute y licencia.
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_D4ads_v5", "VM stopped", "Standard", "PAYG")), 99)[0];

        Assert.Equal(0.10 * 4 * Hours, result.SqlAddonMonthly!.Value, 5);
    }

    // ------------------------------------------------------------------------------------
    // Sin conteo de vCores: no se cotiza y se avisa. Nunca un cero callado.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Sin_poder_determinar_los_vcores_la_licencia_no_se_cotiza_pero_se_avisa()
    {
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_SinNumero", "VM running", "Enterprise", "PAYG")), 99)[0];

        Assert.Null(result.SqlAddonMonthly);
        Assert.Contains("licencia NO cotizada", result.CalculationNotes);
        Assert.Contains("Standard_SinNumero", result.CalculationNotes);
    }

    // ------------------------------------------------------------------------------------
    // Ediciones y tipos de licencia contra los meters reales de la Retail API.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Edicion_web_cobra_licencia()
    {
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_D8s_v3", "VM running", "Web", "PAYG")), 99)[0];

        Assert.Equal(0.008 * 8 * Hours, result.SqlAddonMonthly!.Value, 5);
    }

    [Theory]
    [InlineData("Enterprise", "AHUB")]   // beneficio híbrido: el cliente trae la licencia
    [InlineData("Standard", "AHUB")]
    [InlineData("Enterprise", "DR")]     // réplica de recuperación: meter en 0
    [InlineData("Developer", "PAYG")]    // sin cargo de licencia por edición
    [InlineData("Express", "PAYG")]
    public void Ediciones_y_licencias_sin_cargo(string edition, string license)
    {
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_D8s_v3", "VM running", edition, license)), 99)[0];

        Assert.Null(result.SqlAddonMonthly);
        Assert.Equal(0.10 * Hours, result.PaygMonthly!.Value, 5);
    }

    [Fact]
    public void Sin_recurso_de_sql_vm_no_hay_add_on_ni_aviso()
    {
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_D8s_v3", "VM running", null, null)), 99)[0];

        Assert.Null(result.SqlAddonMonthly);
        Assert.DoesNotContain("licencia NO cotizada", result.CalculationNotes ?? "");
    }

    // ------------------------------------------------------------------------------------
    // "Unknown" es un valor legítimo de sqlImageSku en ARM (el enum documentado es Enterprise,
    // Standard, Express, Web, Unknown, Developer). Detrás puede haber una Enterprise sin cotizar,
    // así que el $0 tiene que quedar dicho.
    // ------------------------------------------------------------------------------------
    [Theory]
    [InlineData("Unknown", "PAYG")]
    [InlineData("EdicionQueNoExiste", "PAYG")]
    public void Edicion_no_reconocida_avisa_en_vez_de_callar(string edition, string license)
    {
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_D8s_v3", "VM running", edition, license)), 99)[0];

        Assert.Null(result.SqlAddonMonthly);
        Assert.Contains("licencia NO cotizada", result.CalculationNotes);
        Assert.Contains(edition, result.CalculationNotes);
    }

    [Fact]
    public void Un_tipo_de_licencia_desconocido_cobra_tarifa_completa()
    {
        // El enum de ARM es PAYG, AHUB y DR. Ante un valor fuera de esos, con una edición que sí
        // factura, se cobra como PAYG: sobreestimar es preferible a regalar la licencia en silencio.
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_D8s_v3", "VM running", "Enterprise", "LicenciaRara")), 99)[0];

        Assert.Equal(0.375 * 8 * Hours, result.SqlAddonMonthly!.Value, 5);
    }

    [Fact]
    public void Un_cero_legitimo_no_genera_aviso()
    {
        // Developer/Express y AHUB/DR son $0 de verdad: no deben ensuciar las notas.
        foreach (var (edition, license) in new[] { ("Developer", "PAYG"), ("Express", "PAYG"), ("Enterprise", "AHUB") })
        {
            var result = Build().Calculate(Res.Rows(
                Vm("Standard_D8s_v3", "VM running", edition, license)), 99)[0];

            Assert.Null(result.SqlAddonMonthly);
            Assert.DoesNotContain("licencia NO cotizada", result.CalculationNotes ?? "");
        }
    }

    // ------------------------------------------------------------------------------------
    // El add-on se sigue sumando al RI: no existe meter de reserva para la licencia SQL
    // (verificado 2026-08-21 filtrando type eq 'Reservation' en la Retail API).
    // ------------------------------------------------------------------------------------
    [Fact]
    public void El_add_on_se_suma_al_ri_porque_la_reserva_de_compute_no_cubre_la_licencia()
    {
        var result = Build().Calculate(Res.Rows(
            Vm("Standard_D8s_v3", "VM running", "Standard", "PAYG")), 99)[0];

        var addon = 0.10 * 8 * Hours;
        Assert.Equal(600.0 / 12.0 + addon, result.Ri1yMonthly!.Value, 5);
        Assert.Equal(1500.0 / 36.0 + addon, result.Ri3yMonthly!.Value, 5);
    }
}
