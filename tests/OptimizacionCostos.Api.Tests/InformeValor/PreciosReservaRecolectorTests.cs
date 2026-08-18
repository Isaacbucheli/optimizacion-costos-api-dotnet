using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Entrega 8, pieza A (Tarea 1): el catálogo de precios PAYG/RI para las líneas de reserva del
/// archivo de evolución. Unitario puro: el único puerto es <see cref="IPriceRepository"/> y acá
/// se falsifica; nada de HTTP ni SQL.
/// </summary>
public class PreciosReservaRecolectorTests
{
    /// <summary>La evolución trae la región en formato display ("US East 2"); el catálogo espera
    /// ARM ("eastus2"). Tokens del display contenidos en el nombre ARM, con preferencia por el
    /// match exacto de longitud: "US East" es eastus (no eastus2), "US East 2" es eastus2.</summary>
    [Theory]
    [InlineData("US East 2", "eastus2")]
    [InlineData("US East", "eastus")]
    [InlineData("West Europe", "westeurope")]
    [InlineData("Brazil South", "brazilsouth")]
    [InlineData("east us 2", "eastus2")]
    public void La_region_display_se_resuelve_a_arm(string display, string esperado) =>
        Assert.Equal(esperado, PreciosReservaRecolector.ResolverRegionArm(display));

    /// <summary>Lista cerrada a propósito (D9): una región que no se puede resolver de forma
    /// única deja la línea sin precio y declarada, nunca un match adivinado.</summary>
    [Theory]
    [InlineData("Region Inventada 9")]
    [InlineData("")]
    [InlineData("   ")]
    public void Una_region_desconocida_no_se_adivina(string display) =>
        Assert.Null(PreciosReservaRecolector.ResolverRegionArm(display));

    [Fact]
    public void Resolver_mensualiza_payg_y_ri_por_termino()
    {
        var precios = PreciosReservaRecolector.Resolver(new FakePrices(),
            [("Standard_D4s_v3", "US East 2", "P3Y"), ("Standard_Z9", "US East 2", "P1Y")]);

        var p = precios[PreciosReservaRecolector.Clave("Standard_D4s_v3", "US East 2", "P3Y")];
        Assert.Equal(0.20m * 730m, p.PaygMensual);
        Assert.Equal(2102.40m / 36m, p.RiMensual);
        Assert.Single(precios); // el SKU sin precio no entra: la fila quedará sin monto, declarada
    }

    /// <summary>P5Y no existe para VMs en el catálogo: la línea queda fuera del diccionario (la
    /// calculadora de la Tarea 2 publica su cargo sin ahorro, con motivo).</summary>
    [Fact]
    public void Un_termino_de_cinco_anios_no_resuelve_precio()
    {
        var precios = PreciosReservaRecolector.Resolver(new FakePrices(),
            [("Standard_D4s_v3", "US East 2", "P5Y")]);
        Assert.Empty(precios);
    }

    [Fact]
    public void Una_region_sin_resolucion_no_consulta_el_catalogo()
    {
        var fake = new FakePrices();
        var precios = PreciosReservaRecolector.Resolver(fake, [("Standard_D4s_v3", "Region Inventada 9", "P1Y")]);
        Assert.Empty(precios);
        Assert.Equal(0, fake.Llamadas); // sin región ARM no hay a quién preguntarle
    }

    /// <summary>Falso del repositorio de precios: solo <see cref="GetVmPrices"/> se usa en este
    /// recolector; el resto de la interfaz lanza para que un uso accidental se vea en el acto.</summary>
    private sealed class FakePrices : IPriceRepository
    {
        public int Llamadas { get; private set; }

        public VmPrices GetVmPrices(string armSkuName, string region, string osType)
        {
            Llamadas++;
            Assert.Equal("Linux", osType); // decisión 2026-08-18: PAYG Linux, conservador
            return armSkuName == "Standard_D4s_v3" && region == "eastus2"
                ? new VmPrices(PaygHourly: 0.20, Ri1yTotal: 1051.20, Ri3yTotal: 2102.40)
                : new VmPrices(null, null, null);
        }

        public double? GetDiskPrice(string skuTier, string region) => throw new NotImplementedException();
        public double? GetPublicIpMonthlyPrice(string skuName, string region, string allocationMethod = "Static") => throw new NotImplementedException();
        public ElasticPremiumBase? GetElasticPremiumBasePrice(string skuName, string region) => throw new NotImplementedException();
        public AppServicePrices GetAppServicePrices(string armSkuName, string region, bool isLinux) => throw new NotImplementedException();
        public MySqlFlexPrices GetMySqlFlexPrices(string armSkuName, string region, string skuTier = "") => throw new NotImplementedException();
        public double? GetMySqlStoragePricePerGb(string region) => throw new NotImplementedException();
        public RedisPrices GetRedisPrices(string skuTier, int skuCapacity, string region) => throw new NotImplementedException();
        public SqlDbPriceDetails? GetSqlDbPriceDetails(string skuName, string region, string skuTier = "", string computeTier = "") => throw new NotImplementedException();
        public SqlManagedInstancePrices GetSqlManagedInstancePrices(string region, string skuTier = "", string skuFamily = "", int vcores = 0, bool zoneRedundant = false) => throw new NotImplementedException();
        public SynapseDwPrices GetSynapseDwPrices(string dwLevel, string region) => throw new NotImplementedException();
        public ElasticPoolPrices? GetElasticPoolPrices(string skuTier, string skuFamily, int capacity, string region) => throw new NotImplementedException();
        public double? GetSnapshotPricePerGb(string region, string? storageType) => throw new NotImplementedException();
        public StorageFilesPrices GetStorageFilesPrices(string region, string tier, string redundancy) => throw new NotImplementedException();
    }
}
