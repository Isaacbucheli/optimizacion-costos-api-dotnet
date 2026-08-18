using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Entrega 8, pieza A (Tarea 2): las reservas detectadas SOLO desde el archivo de evolución,
/// para cuando la foto de Azure no midió. Calculadora pura: recibe filas y precios ya resueltos.
/// </summary>
public class ReservasArchivoCalculadorTests
{
    private static EvolucionRow Fila(string recurso, int anio, int mes, decimal pvp, bool reserva = false) =>
        new(NaturalKeyHash: "h", Category: reserva ? "Virtual Machines" : "Storage", Subcategory: null,
            ResourceName: recurso, IsReservation: reserva, Pvp: pvp,
            PeriodYear: (short)anio, PeriodMonth: (byte)mes);

    [Fact]
    public void Una_compra_observada_gana_mes_de_ejecucion_y_vencimiento()
    {
        // Archivo ene-abr 2026; la línea de reserva aparece en marzo → compra observada, P3Y vence 2029-03.
        var evolucion = new List<EvolucionRow>
        {
            Fila("vm-app-01", 2026, 1, 100m),
            Fila("Reserved VM Instance, Standard_D4s_v3, US East 2, 3 Years", 2026, 3, 300m, reserva: true),
            Fila("Reserved VM Instance, Standard_D4s_v3, US East 2, 3 Years", 2026, 4, 300m, reserva: true),
        };
        var precios = new Dictionary<string, PrecioReservaVm>
        {
            [PreciosReservaRecolector.Clave("Standard_D4s_v3", "US East 2", "P3Y")] = new(PaygMensual: 500m, RiMensual: 300m),
        };

        var m = ReservasArchivoCalculador.Calcular(evolucion, precios)!;
        var f = Assert.Single(m.Filas);
        Assert.False(f.Heredada);
        Assert.Equal("2026-03", f.Desde);
        Assert.Equal("2029-03", f.Vence);
        Assert.Equal(300m, f.CargoMes);
        Assert.Equal(200m, f.AhorroMes); // 300 × (500−300)/300
        Assert.Equal(300m, m.TotalCargo);
        Assert.Equal(200m, m.TotalAhorro);
        Assert.Equal(0, m.SinPrecio);
    }

    [Fact]
    public void Una_linea_desde_el_primer_mes_del_archivo_es_heredada_sin_vencimiento()
    {
        var evolucion = new List<EvolucionRow>
        {
            Fila("Reserved VM Instance, Standard_B4ms, US East, 1 Year", 2026, 1, 120m, reserva: true),
            Fila("vm-app-01", 2026, 1, 40m),
            Fila("Reserved VM Instance, Standard_B4ms, US East, 1 Year", 2026, 2, 120m, reserva: true),
        };
        var m = ReservasArchivoCalculador.Calcular(evolucion, new Dictionary<string, PrecioReservaVm>())!;
        var f = Assert.Single(m.Filas);
        Assert.True(f.Heredada);
        Assert.Equal("2026-01", f.Desde);
        Assert.Null(f.Vence);
        Assert.Null(f.AhorroMes);            // sin precio en el diccionario
        Assert.NotNull(f.MotivoSinMonto);    // declarado, nunca inventado
        Assert.Equal(1, m.SinPrecio);
    }

    /// <summary>El pivot puede traer varias filas de la misma línea en el mismo mes (una por
    /// categoría): primero se suma por mes y recién ahí se toma la mediana — el mismo criterio
    /// del cargo mensual estable que ya usa la tabla por VM (mes de compra prorrateado).</summary>
    [Fact]
    public void El_cargo_es_la_mediana_de_los_totales_mensuales()
    {
        var linea = "Reserved VM Instance, Standard_D4s_v3, US East 2, 3 Years";
        var evolucion = new List<EvolucionRow>
        {
            Fila("vm-app-01", 2026, 1, 10m),
            Fila(linea, 2026, 2, 150m, reserva: true), // mes de compra, prorrateado
            Fila(linea, 2026, 3, 200m, reserva: true),
            Fila(linea, 2026, 3, 100m, reserva: true), // segunda fila del MISMO mes: suma 300
            Fila(linea, 2026, 4, 300m, reserva: true),
        };
        var m = ReservasArchivoCalculador.Calcular(evolucion, new Dictionary<string, PrecioReservaVm>())!;
        Assert.Equal(300m, Assert.Single(m.Filas).CargoMes); // mediana de [150, 300, 300]
    }

    [Fact]
    public void Sin_lineas_de_reserva_devuelve_null() =>
        Assert.Null(ReservasArchivoCalculador.Calcular(
            [Fila("vm-app-01", 2026, 1, 40m)], new Dictionary<string, PrecioReservaVm>()));

    /// <summary>Una línea que no tiene la forma exacta de cuatro partes no es una reserva de VM:
    /// se descarta en silencio, igual que hace la tabla por VM (is_reservation solo detecta esa
    /// familia).</summary>
    [Fact]
    public void Una_linea_con_otra_forma_se_descarta()
    {
        var evolucion = new List<EvolucionRow>
        {
            Fila("Reserved Capacity, algo raro", 2026, 1, 99m, reserva: true),
            Fila("vm-app-01", 2026, 1, 40m),
        };
        Assert.Null(ReservasArchivoCalculador.Calcular(evolucion, new Dictionary<string, PrecioReservaVm>()));
    }

    [Fact]
    public void Lineas_para_precios_extrae_sku_region_y_termino_iso()
    {
        var lineas = ReservasArchivoCalculador.LineasParaPrecios(
            [Fila("Reserved VM Instance, Standard_D4s_v3, US East 2, 3 Years", 2026, 3, 300m, reserva: true)]);
        Assert.Equal(("Standard_D4s_v3", "US East 2", "P3Y"), Assert.Single(lineas));
    }

    /// <summary>Un término de 5 años se detecta (la fila se publica con su cargo) pero no puede
    /// llevar precio: LineasParaPrecios lo reporta con su ISO y es el recolector quien no lo
    /// resuelve — acá solo se verifica que la fila queda sin monto y con motivo.</summary>
    [Fact]
    public void Un_termino_de_cinco_anios_publica_cargo_sin_ahorro()
    {
        var evolucion = new List<EvolucionRow>
        {
            Fila("vm-app-01", 2026, 1, 10m),
            Fila("Reserved VM Instance, Standard_D4s_v3, US East 2, 5 Years", 2026, 2, 300m, reserva: true),
        };
        var m = ReservasArchivoCalculador.Calcular(evolucion, new Dictionary<string, PrecioReservaVm>())!;
        var f = Assert.Single(m.Filas);
        Assert.Equal("2031-02", f.Vence); // P5Y = 60 meses desde 2026-02
        Assert.Null(f.AhorroMes);
        Assert.NotNull(f.MotivoSinMonto);
    }
}
