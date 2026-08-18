using System.Globalization;
using System.Text.Json.Serialization;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

/// <summary>Una línea de reserva del archivo de evolución, publicada como fila del respaldo
/// (entrega 8, pieza A): lo observable desde la factura — el cargo y desde cuándo — más el
/// ahorro estimado por catálogo cuando el precio se pudo resolver.</summary>
public sealed record ReservaArchivoFila(
    [property: JsonPropertyName("linea")] string Linea,
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("term")] string TermTexto,
    [property: JsonPropertyName("cargo")] decimal CargoMes,
    [property: JsonPropertyName("ahorro")] decimal? AhorroMes,
    [property: JsonPropertyName("desde")] string Desde,
    [property: JsonPropertyName("vence")] string? Vence,
    [property: JsonPropertyName("heredada")] bool Heredada,
    [property: JsonPropertyName("sinMonto")] string? MotivoSinMonto);

/// <summary>El respaldo completo: las líneas y sus totales (suma exacta de filas ya redondeadas
/// una vez cada una, E1). <see cref="SinPrecio"/> cuenta las filas publicadas con cargo pero sin
/// ahorro — el hueco se declara, nunca se rellena.</summary>
public sealed record ReservasArchivoModelo(
    [property: JsonPropertyName("filas")] IReadOnlyList<ReservaArchivoFila> Filas,
    [property: JsonPropertyName("totalCargo")] decimal TotalCargo,
    [property: JsonPropertyName("totalAhorro")] decimal TotalAhorro,
    [property: JsonPropertyName("sinPrecio")] int SinPrecio);

/// <summary>
/// Entrega 8, pieza A (Tarea 2): las reservas del cliente leídas SOLO desde el archivo de
/// evolución, para cuando la foto de Azure no midió (sin credenciales, sin permiso de reservas,
/// o una lectura que falló). Pura, sin IO ni reloj (<c>SinRelojDelSistemaTests</c> escanea
/// <c>Calculo/</c> completo): los precios llegan resueltos por
/// <see cref="PreciosReservaRecolector"/> y las filas de evolución ya persistidas.
///
/// <para><b>La foto sigue siendo la autoridad.</b> Esta calculadora ni se invoca cuando la foto
/// midió (gate en el ensamblador): no puede haber doble conteo por construcción. Lo que la foto
/// sabe y el archivo no —qué VMs consumen cada reserva, el vencimiento exacto, compras fuera de
/// la ventana del archivo— acá no se inventa: la tabla por VM no existe en modo respaldo.</para>
///
/// <para><b>Compra observada contra heredada (decisión 2026-08-18).</b> El mes de compra es el
/// primer mes con cargo de la línea mirando el archivo COMPLETO (no el rango del informe: una
/// compra de enero no se vuelve "de marzo" porque el informe arranque en marzo). Si la línea
/// factura desde el primer mes del archivo, la compra es anterior a lo observable: la fila queda
/// HEREDADA, sin vencimiento derivable, y quien la consuma (Tarea 4 del registro) no la proyecta
/// a fin de año — la salvaguarda 4 (la proyección respeta vencimientos) pesa más que la cifra.
/// Dentro del rango sí suma: su cargo es un hecho facturado mes a mes.</para>
///
/// <para><b>El ahorro es <c>cargo × (PAYG − RI) / RI</c></b>: el cargo de la línea dividido por
/// el precio RI mensual da las unidades equivalentes, y cada unidad ahorra PAYG − RI — la forma
/// factorizada evita materializar unidades fraccionales. Sin precio resuelto (SKU raro, término
/// de 5 años, región no resoluble): la fila se publica con su cargo y sin ahorro, con motivo —
/// nunca un factor fijo ni una inferencia desde la caída de la factura (E2/D3, sobreestimó
/// 2.1×).</para>
/// </summary>
public static class ReservasArchivoCalculador
{
    private const string PrefijoLineaReserva = "Reserved VM Instance";

    /// <summary>Mismo mapa literal que la tabla por VM (privado allá, reimplementado acá — el
    /// mismo criterio que ya documentan las constantes reimplementadas del módulo).</summary>
    private static readonly Dictionary<string, string> TerminoPorTexto = new(StringComparer.Ordinal)
    {
        ["1 Year"] = "P1Y",
        ["3 Years"] = "P3Y",
        ["5 Years"] = "P5Y",
    };

    private static readonly Dictionary<string, int> MesesPorTermino = new(StringComparer.OrdinalIgnoreCase)
    {
        ["P1Y"] = 12,
        ["P3Y"] = 36,
        ["P5Y"] = 60,
    };

    private sealed record Linea(string ResourceName, string Sku, string Region, string TermTexto, string? TermIso);

    /// <summary>Las (SKU, región, término ISO) únicas de las líneas de reserva del archivo, para
    /// que el controller resuelva sus precios ANTES de llamar al ensamblador (el catálogo es IO y
    /// esta calculadora es pura). Términos sin ISO reconocido no viajan: no hay precio que
    /// pedirles.</summary>
    public static IReadOnlyList<(string Sku, string Region, string TermIso)> LineasParaPrecios(
        IReadOnlyList<EvolucionRow> evolucion) =>
        LineasDeReserva(evolucion)
            .Where(l => l.TermIso is not null)
            .Select(l => (l.Sku, l.Region, l.TermIso!))
            .Distinct()
            .ToList();

    /// <summary><c>null</c> cuando el archivo no trae ninguna línea de reserva reconocible: es el
    /// gate del ensamblador — sin líneas no hay respaldo que publicar, y el eje queda en su
    /// degradación actual (no medido, con motivo).</summary>
    public static ReservasArchivoModelo? Calcular(
        IReadOnlyList<EvolucionRow> evolucion,
        IReadOnlyDictionary<string, PrecioReservaVm> precios)
    {
        var lineas = LineasDeReserva(evolucion);
        if (lineas.Count == 0) return null;

        // El primer mes del ARCHIVO sale de todas las filas (reservas o no): contra eso se decide
        // si una compra es observable o heredada.
        var primerMesArchivo = evolucion.Min(e => Ordinal(e.PeriodYear, e.PeriodMonth));

        var cargosPorLinea = evolucion
            .Where(e => e.IsReservation)
            .GroupBy(e => e.ResourceName)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(e => Ordinal(e.PeriodYear, e.PeriodMonth))
                .ToDictionary(m => m.Key, m => m.Sum(e => e.Pvp)));

        var filas = new List<ReservaArchivoFila>();
        foreach (var linea in lineas)
        {
            var porMes = cargosPorLinea[linea.ResourceName]
                .Where(kv => kv.Value > 0m)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (porMes.Count == 0) continue; // una línea sin ningún cargo no afirma nada

            var desdeOrdinal = porMes.Keys.Min();
            var heredada = desdeOrdinal == primerMesArchivo;
            var cargo = Redondeo.ComoJs(MedianaOPromedio(porMes.Values.OrderBy(v => v).ToList()));

            string? vence = null;
            if (!heredada && linea.TermIso is not null && MesesPorTermino.TryGetValue(linea.TermIso, out var meses))
                vence = DesdeOrdinal(desdeOrdinal + meses);

            decimal? ahorro = null;
            string? motivoSinMonto = null;
            if (linea.TermIso is not null
                && precios.TryGetValue(PreciosReservaRecolector.Clave(linea.Sku, linea.Region, linea.TermIso), out var precio)
                && precio.RiMensual > 0m)
            {
                ahorro = Redondeo.ComoJs(cargo * (precio.PaygMensual - precio.RiMensual) / precio.RiMensual);
            }
            else
            {
                motivoSinMonto = "El catálogo de precios no tiene PAYG/RI para este SKU, región o " +
                    "término: se publica el cargo, sin ahorro.";
            }

            filas.Add(new ReservaArchivoFila(
                Linea: linea.ResourceName,
                Sku: linea.Sku,
                Region: linea.Region,
                TermTexto: linea.TermTexto,
                CargoMes: cargo,
                AhorroMes: ahorro,
                Desde: DesdeOrdinal(desdeOrdinal),
                Vence: vence,
                Heredada: heredada,
                MotivoSinMonto: motivoSinMonto));
        }

        if (filas.Count == 0) return null;

        // Regla 6 del módulo: los totales son la suma EXACTA de filas ya redondeadas una vez.
        return new ReservasArchivoModelo(
            Filas: filas,
            TotalCargo: filas.Sum(f => f.CargoMes),
            TotalAhorro: filas.Where(f => f.AhorroMes is not null).Sum(f => f.AhorroMes!.Value),
            SinPrecio: filas.Count(f => f.AhorroMes is null));
    }

    /// <summary>Parsea <c>"Reserved VM Instance, SKU, región, término"</c> (cuatro partes,
    /// prefijo exacto): cualquier otra forma se descarta en silencio — no es una reserva de VM, y
    /// <c>is_reservation</c> ya declaró que solo detecta esa familia. Término no mapeable deja
    /// <see cref="Linea.TermIso"/> null: la fila igual se publica, sin precio.</summary>
    private static List<Linea> LineasDeReserva(IReadOnlyList<EvolucionRow> evolucion) =>
        evolucion
            .Where(e => e.IsReservation)
            .Select(e => e.ResourceName)
            .Distinct(StringComparer.Ordinal)
            .Select(nombre =>
            {
                var partes = nombre.Split(", ");
                if (partes.Length != 4 || partes[0] != PrefijoLineaReserva) return null;
                return new Linea(nombre, partes[1], partes[2], partes[3], TerminoPorTexto.GetValueOrDefault(partes[3]));
            })
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();

    /// <summary>Mediana con 3+ valores (absorbe el mes de compra prorrateado sin identificarlo),
    /// promedio simple con menos: el mismo criterio del cargo mensual estable de la tabla por VM,
    /// reimplementado por el mismo motivo que la mediana de esa clase.</summary>
    private static decimal MedianaOPromedio(List<decimal> ordenados)
    {
        if (ordenados.Count < 3) return ordenados.Average();
        var n = ordenados.Count;
        return n % 2 == 1 ? ordenados[n / 2] : (ordenados[(n / 2) - 1] + ordenados[n / 2]) / 2m;
    }

    private static int Ordinal(short anio, byte mes) => (anio * 12) + mes;

    private static string DesdeOrdinal(int ordinal)
    {
        var anio = (ordinal - 1) / 12;
        var mes = ordinal - (anio * 12);
        return anio.ToString("D4", CultureInfo.InvariantCulture) + "-" + mes.ToString("D2", CultureInfo.InvariantCulture);
    }
}
