using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// La fecha de corte y toda fecha relativa se resuelven en America/Guayaquil, una sola vez
/// (Global Constraints del plan de la entrega 2b): <see cref="ResolverFechaEnGuayaquil"/> es el
/// único punto del módulo que convierte un instante a fecha local. El resultado (un
/// <see cref="DateOnly"/>) se guarda en <see cref="ContextoInformeValor.Corte"/> y de ahí en
/// adelante viaja como valor.
///
/// <para><b>Nota de la Tarea 8 (cierre de deuda):</b> esta clase tenía un segundo helper,
/// <c>TryParseFormatoEeuu</c>, para el otro problema de fecha del port (D13): el parser de fechas
/// de retiro de la plantilla (<c>parseFecha</c>) tiene un ternario que no hace nada y siempre
/// interpreta día/mes, así que un export de Advisor en formato de Estados Unidos (mes primero) se
/// leía mal, y un "mes" fuera de rango desbordaba al año siguiente sin aviso (<c>Date.UTC</c> no
/// valida el mes). Ese helper quedó documentado como "sin llamador conocido" desde que se escribió
/// (los recolectores de la 2a ya entregan <see cref="RetiroFila.FechaRetiro"/> tipado como
/// <see cref="DateOnly"/>? desde una columna <c>DATETIME2</c>, nunca como texto crudo de un CSV en
/// este camino). Confirmado sin ningún llamador de producción en toda la entrega 2b: se borró
/// junto con sus tests, en vez de dejar un parser con un contrato sutil (mes antes que día)
/// esperando a que alguien lo use mal el día que sí aparezca un insumo con fechas en texto.</para>
/// </summary>
public static class Fechas
{
    // America/Guayaquil: UTC-5 fijo. Ecuador no observa horario de verano desde 1992, así que el
    // desplazamiento nunca cambia; no hace falta calendario de transiciones.
    private static readonly TimeSpan OffsetGuayaquil = TimeSpan.FromHours(-5);

    /// <summary>
    /// Resuelve por Id de IANA, disponible en .NET 8 tanto en Windows como en Linux (el runtime
    /// usa datos ICU/tzdb completos por defecto). Si el runtime no tuviera el dato —una imagen de
    /// contenedor con los datos de zona horaria recortados, por ejemplo—, cae a una zona fija
    /// UTC-5: exacta siempre para Guayaquil, no una aproximación de emergencia.
    /// </summary>
    public static readonly TimeZoneInfo ZonaGuayaquil = ResolverZonaGuayaquil();

    private static TimeZoneInfo ResolverZonaGuayaquil()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Guayaquil"); }
        catch (TimeZoneNotFoundException) { return ZonaFija(); }
        catch (InvalidTimeZoneException) { return ZonaFija(); }
    }

    private static TimeZoneInfo ZonaFija() =>
        TimeZoneInfo.CreateCustomTimeZone("America/Guayaquil (fijo)", OffsetGuayaquil, "Guayaquil", "Guayaquil");

    /// <summary>
    /// Convierte un instante a la fecha (sin hora) que marca un reloj en Guayaquil en ese momento.
    /// Se llama UNA SOLA vez por cálculo, en el punto de entrada que construye
    /// <see cref="ContextoInformeValor"/>: ningún bloque vuelve a convertir zonas horarias, así
    /// que dos corridas con el mismo instante de corte dan siempre la misma fecha.
    /// </summary>
    public static DateOnly ResolverFechaEnGuayaquil(DateTimeOffset instante) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, ZonaGuayaquil).DateTime);

    /// <summary>
    /// La clave de mes calendario ("aaaa-MM") de un índice <c>año * 12 + mes</c> con el mes en
    /// 1..12, que es como se ordenan y comparan los períodos en las consultas del módulo. El mes 12
    /// es el caso que se rompe si uno divide de más: 2025*12+12 tiene que volver a leerse como
    /// 2025-12, nunca como 2026-00.
    /// </summary>
    public static string ClaveDeMes(int indiceAnioMes) =>
        $"{(indiceAnioMes - 1) / 12:D4}-{((indiceAnioMes - 1) % 12) + 1:D2}";
}
