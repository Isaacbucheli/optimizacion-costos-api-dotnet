using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

/// <summary>
/// Tarea 3 del plan de la entrega 6: la tabla "reservas contra la propia factura" (spec, tabla por
/// VM del HTML de referencia). Pura, sin IO ni reloj (ver <c>SinRelojDelSistemaTests</c>, que
/// escanea <c>Calculo/</c> completo, incluida esta subcarpeta): la fecha de corte y el rango llegan
/// resueltos en <see cref="ContextoInformeValor"/>, y el inicio de cada reserva sale de
/// <see cref="AhorroReservasCalculador.InicioDeReserva"/> (mismo helper de la Tarea 2, una sola
/// regla para "ExpiresOn menos Term" en todo el módulo).
///
/// <para><b>Por qué hace falta un segundo archivo.</b> BITCOST (tabla de hechos, <see cref="FacturacionRow"/>)
/// no trae el precio facturado de una reserva: la reserva de un recurso no cambia su nombre en la
/// factura, así que el "antes" y el "después" de esa misma línea se ven como el mismo recurso a
/// tarifa distinta, nunca como un cargo de reserva separado y visible. El archivo de evolución sí
/// lo trae, en líneas <c>IsReservation=true</c> cuyo <see cref="EvolucionRow.ResourceName"/> tiene
/// la forma literal <c>"Reserved VM Instance, SKU, región, término"</c> — un recurso pivotante que
/// agrupa TODAS las reservas de ese SKU+región+término del cliente, sin decir a qué VM puntual
/// corresponden. Unir esa línea con la VM que sí conoce la foto de reservas (<see cref="FotoReservas"/>,
/// Tarea 1: consumidores confirmados por Azure) es exactamente lo que hace esta clase.</para>
///
/// <para><b>El match es por SKU+término, no por nombre de recurso.</b> La línea de evolución no
/// tiene una terna de recurso (sub/rg/nombre) que cruzar contra <see cref="ConsumidorReserva"/>: solo
/// tiene SKU, región y término. Por eso el cruce va al revés de como cruza el resto del módulo
/// (nunca D11): se parte <see cref="EvolucionRow.ResourceName"/> por <c>", "</c> y se compara el SKU
/// (contra <see cref="ConsumidorReserva.SkuName"/> o <see cref="ReservaActiva.Producto"/>) y el
/// término (mapa literal <c>"1 Year"→"P1Y"</c>, <c>"3 Years"→"P3Y"</c>, <c>"5 Years"→"P5Y"</c>,
/// contra <see cref="ReservaActiva.Term"/>); la región solo desempata si dos reservas activas
/// comparten SKU+término.</para>
///
/// <para><b>Compartidas: la línea es del SKU, no de una reserva puntual.</b> Si dos compras
/// separadas del mismo SKU+término+región del cliente coexisten, el archivo de evolución las ve como
/// UNA sola línea (mismo texto de recurso, mismo pivote): el cargo mensual de esa línea se reparte
/// entre TODOS los consumidores confirmados de TODAS las reservas que matchean esa línea, no solo
/// los de la reserva que se está mirando en el momento — de lo contrario, la misma reserva compartida
/// entre N compras cobraría N veces su parte.</para>
///
/// <para><b>La demanda usa la MISMA identidad de recurso que el resto del módulo, en su variante de
/// cruce (no la de reporte).</b> Hay dos formatos de identidad conviviendo en este módulo: el de
/// CRUCE (<c>ClaveTerna</c> en <see cref="AhorroReservasCalculador"/>, normalizado a minúsculas,
/// para ENCONTRAR filas) y el de REPORTE (D11 en <c>ConsumoCalculador</c>/<c>AtribucionCalculador</c>,
/// sin normalizar, para publicarse como texto y cruzarse contra otro conjunto ya publicado con ese
/// mismo formato). Esta clase solo necesita encontrar filas — nunca publica la identidad como texto
/// — así que usa la variante de cruce: normalizada a minúsculas, con el mismo respaldo a nombre de
/// suscripción que ya usa <c>ConsumoCalculador</c> cuando <see cref="FacturacionRow.SubscriptionId"/>
/// falta.</para>
/// </summary>
public static class ReservasFacturadasCalculador
{
    private const string PrefijoLineaReserva = "Reserved VM Instance";

    /// <summary>Mapa literal del término de texto de la evolución (columna del pivot BITCOST) al
    /// código ISO 8601 que ya usa <see cref="ReservaActiva.Term"/> y
    /// <see cref="AhorroReservasCalculador.InicioDeReserva"/>. Los tres términos que Azure Reserved
    /// Instances ofrece hoy; si aparece uno nuevo, la línea simplemente no matchea (va a
    /// <c>SinLineaEnEvolucion</c> desde el lado de la reserva) en vez de reventar.</summary>
    private static readonly Dictionary<string, string> TerminoPorTexto = new(StringComparer.Ordinal)
    {
        ["1 Year"] = "P1Y",
        ["3 Years"] = "P3Y",
        ["5 Years"] = "P5Y",
    };

    private sealed record LineaReserva(string ResourceName, string Sku, string Region, string? Term, IReadOnlyList<EvolucionRow> Filas);

    public static ReservasFacturadasModelo Calcular(
        FotoReservas foto, IReadOnlyList<EvolucionRow> evolucion, IReadOnlyList<FacturacionRow> facturacion,
        ContextoInformeValor contexto)
    {
        if (!foto.Medido) return Degradado(foto.Motivo);

        var lineasEnRango = evolucion
            .Where(e => e.IsReservation
                && ConsumoCalculador.EnRango(e.PeriodYear, e.PeriodMonth, contexto.PeriodStart, contexto.PeriodEnd))
            .ToList();

        // Regla 5, la otra mitad: la foto SI trae reservas activas pero el archivo de evolucion no
        // trae NINGUNA linea de reserva para el rango del informe. Publicar una tabla vacia acá se
        // vería identico a "el cliente no tiene reservas", que es una afirmacion distinta y falsa.
        if (foto.Reservas.Count > 0 && lineasEnRango.Count == 0)
            return Degradado("El archivo de evolución no trae líneas de reserva para el rango del informe.");

        var lineas = lineasEnRango
            .GroupBy(e => e.ResourceName)
            .Select(g => ParsearLinea(g.Key, g.ToList()))
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();

        var porIdentidad = facturacion
            .GroupBy(f => ClaveIdentidad(f.SubscriptionId, f.SubscriptionName, f.ResourceGroup, f.ResourceName))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FacturacionRow>)g.ToList());

        // Regla 2: cuántos consumidores en total comparten cada línea (por su ResourceName, único
        // por SKU+región+término), sumando entre TODAS las reservas activas que matchean esa misma
        // línea — no solo los de una reserva puntual.
        var consumidoresPorLinea = new Dictionary<string, int>();
        var reservaConLinea = new List<(ReservaActiva Reserva, LineaReserva Linea)>();
        var sinLinea = new List<string>();

        foreach (var reserva in foto.Reservas)
        {
            var linea = EncontrarLinea(reserva, lineas);
            if (linea is null)
            {
                sinLinea.Add(NombreDeLaReserva(reserva));
                continue;
            }

            reservaConLinea.Add((reserva, linea));
            consumidoresPorLinea[linea.ResourceName] =
                consumidoresPorLinea.GetValueOrDefault(linea.ResourceName) + reserva.Consumidores.Count;
        }

        var filas = new List<ReservaVmFila>();
        foreach (var (reserva, linea) in reservaConLinea)
        {
            var totalConsumidores = consumidoresPorLinea[linea.ResourceName];
            if (totalConsumidores == 0) continue; // nadie confirmado bajo esta linea: nada que publicar por VM.

            var tasaCruda = TasaMensualDeLaLinea(linea.Filas);
            var reservaMes = Redondeo.ComoJs(tasaCruda / totalConsumidores);
            var compartida = totalConsumidores > 1;
            var inicio = AhorroReservasCalculador.InicioDeReserva(reserva.ExpiresOn, reserva.Term);

            foreach (var consumidor in reserva.Consumidores)
            {
                var (demanda, nota) = PorDemandaDelMesBase(consumidor, inicio, porIdentidad);
                var ahorro = demanda is { } d ? d - reservaMes : (decimal?)null;

                filas.Add(new ReservaVmFila(
                    Vm: consumidor.ResourceName ?? consumidor.InstanceId,
                    Sku: consumidor.SkuName ?? linea.Sku,
                    PorDemandaMes: demanda,
                    ReservaMes: reservaMes,
                    AhorroMes: ahorro,
                    Compartida: compartida,
                    Vence: reserva.ExpiresOn,
                    PorVencer: reserva.Expiring,
                    Nota: nota));
            }
        }

        // Regla 6: los totales son la suma EXACTA de filas que YA vienen redondeadas una vez cada
        // una (E1) — nunca una segunda pasada de redondeo sobre la suma. Solo entran las filas con
        // ahorro calculable: sin demanda no hay nada comparable que sumar a un total de ahorro.
        var conAhorro = filas.Where(f => f.AhorroMes is not null).ToList();
        var totalDemanda = conAhorro.Sum(f => f.PorDemandaMes!.Value);
        var totalReserva = conAhorro.Sum(f => f.ReservaMes);
        var totalAhorro = conAhorro.Sum(f => f.AhorroMes!.Value);

        return new ReservasFacturadasModelo(
            Medido: true, Motivo: null, Filas: filas,
            TotalDemanda: totalDemanda, TotalReserva: totalReserva, TotalAhorro: totalAhorro,
            AhorroAnualizado: Redondeo.ComoJs(totalAhorro * 12),
            SinLineaEnEvolucion: sinLinea,
            ConsumidoresNoLeidos: foto.Reservas.Count(r => r.ConsumidoresNoLeidos));
    }

    private static ReservasFacturadasModelo Degradado(string? motivo) => new(
        Medido: false, Motivo: motivo, Filas: [], TotalDemanda: 0m, TotalReserva: 0m, TotalAhorro: 0m,
        AhorroAnualizado: 0m, SinLineaEnEvolucion: [], ConsumidoresNoLeidos: 0);

    private static string NombreDeLaReserva(ReservaActiva reserva) =>
        reserva.Nombre ?? reserva.Producto ?? reserva.ReservationId ?? "(reserva sin nombre)";

    /// <summary>Regla 1: parsea <c>"Reserved VM Instance, SKU, región, término"</c>. Líneas que no
    /// tienen exactamente esa forma (cuatro partes, prefijo exacto) se descartan en silencio: no son
    /// reservas de VM, y <c>is_reservation</c> ya declaró (entrega 5, herencia) que solo detecta esa
    /// familia — cualquier otra forma es una sorpresa de datos, no un caso a inventar contenido
    /// para.</summary>
    private static LineaReserva? ParsearLinea(string resourceName, IReadOnlyList<EvolucionRow> filas)
    {
        var partes = resourceName.Split(", ");
        if (partes.Length != 4 || partes[0] != PrefijoLineaReserva) return null;

        var sku = partes[1];
        var region = partes[2];
        var term = TerminoPorTexto.GetValueOrDefault(partes[3]);
        return new LineaReserva(resourceName, sku, region, term, filas);
    }

    /// <summary>Regla 1: SKU + término, región solo si hay ambigüedad. El SKU de la reserva sale de
    /// cualquiera de sus consumidores confirmados o de <see cref="ReservaActiva.Producto"/> —
    /// cualquiera de los dos que contenga el SKU de la línea alcanza, porque ninguno de los dos es
    /// consistentemente el más específico entre clientes.</summary>
    private static LineaReserva? EncontrarLinea(ReservaActiva reserva, IReadOnlyList<LineaReserva> lineas)
    {
        var candidatas = lineas
            .Where(l => l.Term is not null && string.Equals(l.Term, reserva.Term, StringComparison.OrdinalIgnoreCase)
                && CoincideSku(l.Sku, reserva))
            .ToList();

        if (candidatas.Count > 1 && !string.IsNullOrWhiteSpace(reserva.Region))
        {
            var porRegion = candidatas
                .Where(l => string.Equals(l.Region, reserva.Region, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (porRegion.Count > 0) candidatas = porRegion;
        }

        return candidatas.Count > 0 ? candidatas[0] : null;
    }

    private static bool CoincideSku(string skuLinea, ReservaActiva reserva) =>
        (reserva.Producto is not null && reserva.Producto.Contains(skuLinea, StringComparison.OrdinalIgnoreCase))
        || reserva.Consumidores.Any(c => c.SkuName is not null && c.SkuName.Contains(skuLinea, StringComparison.OrdinalIgnoreCase));

    /// <summary>Regla 1, la tarifa mensual estable de la línea: los meses de compra suelen venir
    /// prorrateados (la reserva empieza a mitad de mes), así que un solo valor atípico puede
    /// distorsionar un promedio simple. Con tres o más meses facturados, la mediana absorbe ese
    /// atípico sin necesidad de identificar CUÁL mes fue el prorrateado (mismo argumento que
    /// <c>ConsumoCalculador.CalcularAhorro</c> usa la mediana posicional para su línea base); con
    /// menos de tres no hay forma de que un estadístico de posición sea más confiable que el
    /// promedio simple, así que se usa ese.</summary>
    private static decimal TasaMensualDeLaLinea(IReadOnlyList<EvolucionRow> filas)
    {
        var valores = filas.Select(f => f.Pvp).OrderBy(v => v).ToList();
        return valores.Count >= 3 ? Mediana(valores) : valores.Average();
    }

    // Reimplementada a propósito, no reusada desde ConsumoCalculador.Mediana (privada de otra
    // tarea): tres líneas de agrupación no justifican ensanchar la visibilidad de una clase ajena
    // (mismo criterio que ya documentan AhorroReservasCalculador/AtribucionCalculador para sus
    // propias constantes/claves reimplementadas).
    private static decimal Mediana(List<decimal> ordenados)
    {
        var n = ordenados.Count;
        return n % 2 == 1 ? ordenados[n / 2] : (ordenados[(n / 2) - 1] + ordenados[n / 2]) / 2m;
    }

    /// <summary>Regla 3: el Pvp del último mes completo ANTERIOR al mes de
    /// <see cref="AhorroReservasCalculador.InicioDeReserva"/>, sumando entre categorías si el mismo
    /// recurso factura más de una fila ese mes (mismo criterio de agregación que
    /// <c>ConsumoCalculador</c> usa para sus recursos por mes). Sin inicio derivable, sin fila en
    /// BITCOST para esta terna, o sin ningún mes anterior al inicio: <c>null</c> con el motivo — la
    /// fila de la VM se publica igual (Regla 3, "la fila se publica igual").</summary>
    private static (decimal? Demanda, string? Nota) PorDemandaDelMesBase(
        ConsumidorReserva consumidor, DateOnly? inicio, IReadOnlyDictionary<string, IReadOnlyList<FacturacionRow>> porIdentidad)
    {
        if (inicio is null)
            return (null, "No se pudo derivar el inicio de la reserva (el término no se reconoce o falta la " +
                          "fecha de vencimiento): sin inicio no hay mes base que buscar en la facturación.");

        var clave = ClaveIdentidad(consumidor.SubscriptionId, null, consumidor.ResourceGroup, consumidor.ResourceName);
        if (!porIdentidad.TryGetValue(clave, out var filas) || filas.Count == 0)
            return (null, "Este recurso no aparece en la facturación (BITCOST) cargada: sin mes base, el ahorro por demanda no se calcula.");

        var claveInicio = ClaveMes(inicio.Value.Year, inicio.Value.Month);
        var antes = filas.Where(f => ClaveMes(f.Year, f.Month) < claveInicio).ToList();
        if (antes.Count == 0)
            return (null, "Sin facturación anterior al inicio de la reserva para este recurso: sin mes base, el ahorro por demanda no se calcula.");

        var ultimoMes = antes.Max(f => ClaveMes(f.Year, f.Month));
        var demandaCruda = antes.Where(f => ClaveMes(f.Year, f.Month) == ultimoMes).Sum(f => f.Pvp);
        return (Redondeo.ComoJs(demandaCruda), null);
    }

    private static int ClaveMes(int anio, int mes) => (anio * 12) + mes;

    /// <summary>Identidad de CRUCE (ver el comentario de clase): normalizada a minúsculas, con
    /// respaldo a nombre de suscripción cuando <see cref="FacturacionRow.SubscriptionId"/> falta —
    /// mismo respaldo que <c>ConsumoCalculador.Calcular</c> usa para su propio id de recurso
    /// (<c>f.SubscriptionId ?? sub</c>). <see cref="ConsumidorReserva"/> no trae un nombre de
    /// suscripción propio: se le pasa <c>null</c>, así que su clave solo cae al placeholder cuando
    /// tampoco tiene <see cref="ConsumidorReserva.SubscriptionId"/> — el mismo caso límite en el que
    /// tampoco tendría con qué más identificarse.</summary>
    private static string ClaveIdentidad(string? subscriptionId, string? subscriptionName, string? resourceGroup, string? resourceName)
    {
        var sub = subscriptionId ?? (string.IsNullOrWhiteSpace(subscriptionName) ? "(sin suscripción)" : subscriptionName);
        return Norm(sub) + "|" + Norm(resourceGroup) + "|" + Norm(resourceName);
    }

    private static string Norm(string? v) => (v ?? string.Empty).Trim().ToLowerInvariant();
}
