using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Pruebas de <see cref="VmSizeVcpu"/>, el resolvedor del conteo de vCores.
///
/// Contexto (bug encontrado 2026-08-21): el cálculo deducía los vCores del primer número del nombre
/// del tamaño. En los tamaños de núcleo restringido eso cobra el doble de licencia SQL, y en las
/// familias viejas se equivoca en cualquier dirección. Los casos de abajo son tamaños reales de
/// Azure, varios de ellos tomados del inventario de clientes.
/// </summary>
public sealed class VmSizeVcpuTests
{
    // ------------------------------------------------------------------------------------
    // El dato del inventario (Microsoft.Compute/skus) gana sobre cualquier lectura del nombre.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void El_vcpu_count_del_inventario_gana()
    {
        var r = VmSizeVcpu.Resolve(16, "Standard_E32-16s_v3");

        Assert.Equal(16, r.Vcpus);
        Assert.Equal(VcpuSource.Inventory, r.Source);
        Assert.False(r.IsDerivedFromName);
    }

    [Fact]
    public void Un_vcpu_count_en_cero_o_negativo_no_cuenta_como_dato()
    {
        Assert.Equal(VcpuSource.SizeName, VmSizeVcpu.Resolve(0, "Standard_D8s_v3").Source);
        Assert.Equal(VcpuSource.SizeName, VmSizeVcpu.Resolve(-1, "Standard_D8s_v3").Source);
    }

    // ------------------------------------------------------------------------------------
    // Núcleo restringido: el número DESPUÉS del guion es el que Azure licencia.
    // ------------------------------------------------------------------------------------
    [Theory]
    [InlineData("Standard_E32-16s_v3", 16)]    // el caso que cobraba 32 y duplicaba la licencia
    [InlineData("Standard_E32-16s_v4", 16)]
    [InlineData("Standard_E32-16as_v4", 16)]
    [InlineData("Standard_E16-4ads_v5", 4)]
    [InlineData("Standard_E8-4as_v4", 4)]
    [InlineData("Standard_E8-4s_v3", 4)]
    [InlineData("Standard_E4-2s_v5", 2)]
    [InlineData("Standard_E64-32ads_v5", 32)]
    [InlineData("Standard_DS13-4_v2", 4)]
    [InlineData("Standard_M128-32ms", 32)]
    [InlineData("Standard_M8-2ms", 2)]
    public void Nucleo_restringido_usa_los_vcores_activos(string size, int esperado)
    {
        var r = VmSizeVcpu.Resolve(null, size);

        Assert.Equal(esperado, r.Vcpus);
        Assert.Equal(VcpuSource.ConstrainedSuffix, r.Source);
        Assert.False(r.IsDerivedFromName);
    }

    // ------------------------------------------------------------------------------------
    // Familias viejas: el número del nombre es un índice de tamaño, no el conteo de vCPU.
    // ------------------------------------------------------------------------------------
    [Theory]
    [InlineData("Standard_DS11_v2", 2)]        // el nombre dice 11, son 2
    [InlineData("Standard_DS12_v2", 4)]
    [InlineData("Standard_DS13_v2", 8)]
    [InlineData("Standard_DS14_v2", 16)]
    [InlineData("Standard_DS15_v2", 20)]
    [InlineData("Standard_D3_v2", 4)]
    [InlineData("Standard_DS3_v2", 4)]
    [InlineData("Standard_DS4_v2", 8)]
    [InlineData("Standard_DS5_v2", 16)]        // el nombre dice 5, son 16
    [InlineData("Standard_D5_v2", 16)]
    [InlineData("Standard_D13", 8)]
    [InlineData("Standard_G5", 32)]            // el nombre dice 5, son 32
    [InlineData("Standard_GS4", 16)]
    [InlineData("Standard_A9", 16)]
    public void Familias_viejas_salen_de_la_tabla(string size, int esperado)
    {
        var r = VmSizeVcpu.Resolve(null, size);

        Assert.Equal(esperado, r.Vcpus);
        Assert.Equal(VcpuSource.LegacyTable, r.Source);
        Assert.False(r.IsDerivedFromName);
    }

    // ------------------------------------------------------------------------------------
    // Familias modernas: el primer número del nombre SÍ es el conteo. Queda marcado como
    // deducido para que la nota del cálculo lo diga.
    // ------------------------------------------------------------------------------------
    [Theory]
    [InlineData("Standard_B2ms", 2)]
    [InlineData("Standard_B4ms", 4)]
    [InlineData("Standard_B8ms", 8)]
    [InlineData("Standard_D4s_v3", 4)]
    [InlineData("Standard_D8s_v3", 8)]
    [InlineData("Standard_D8ds_v5", 8)]
    [InlineData("Standard_D16ads_v5", 16)]
    [InlineData("Standard_E2ds_v5", 2)]
    [InlineData("Standard_E8ds_v5", 8)]
    [InlineData("Standard_E16as_v5", 16)]
    [InlineData("Standard_F8s_v2", 8)]
    [InlineData("Standard_M128ms", 128)]
    public void Familias_modernas_salen_del_nombre(string size, int esperado)
    {
        var r = VmSizeVcpu.Resolve(null, size);

        Assert.Equal(esperado, r.Vcpus);
        Assert.Equal(VcpuSource.SizeName, r.Source);
        Assert.True(r.IsDerivedFromName);
    }

    // ------------------------------------------------------------------------------------
    // Sin dato: null a propósito. El llamador marca la fila en vez de cobrar un número inventado.
    // ------------------------------------------------------------------------------------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Standard_SinNumero")]
    public void Sin_conteo_devuelve_null_y_no_adivina(string? size)
    {
        var r = VmSizeVcpu.Resolve(null, size);

        Assert.Null(r.Vcpus);
        Assert.Equal(VcpuSource.Unknown, r.Source);
    }

    // ------------------------------------------------------------------------------------
    // Regresión directa del bug: el tamaño que cobraba el doble.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Regresion_E32_16s_v3_no_vuelve_a_dar_32()
    {
        Assert.NotEqual(32, VmSizeVcpu.Resolve(null, "Standard_E32-16s_v3").Vcpus);
        Assert.Equal(16, VmSizeVcpu.Resolve(null, "Standard_E32-16s_v3").Vcpus);
    }
}
