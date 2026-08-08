using System.Globalization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Los dos problemas de fecha del port (D13 del plan de la entrega 2b), resueltos por separado
/// porque son independientes entre sí.
///
/// <para><b>1. Fechas de origen en formato de Estados Unidos.</b> El parser de fechas de retiro
/// de la plantilla (<c>parseFecha</c>) tiene un ternario que no hace nada
/// (<c>a&gt;12?b:b</c>, las dos ramas devuelven <c>b</c>) y por eso siempre interpreta el primer
/// número como día y el segundo como mes. El export de Advisor viene en formato de Estados Unidos
/// (mes primero), así que la plantilla lee "03/05/2026" como 3 de mayo cuando es 5 de marzo. Con
/// un día mayor a 12 el error es silencioso y peor: JavaScript's <c>Date.UTC</c> no valida el mes,
/// así que un "mes" fuera de rango 1-12 desborda hacia el año siguiente sin ningún aviso.
/// <see cref="TryParseFormatoEeuu"/> fija el formato de origen explícitamente (mes primero) y,
/// como usa <c>DateOnly.TryParseExact</c> en vez de aritmética manual, un valor fuera de rango
/// simplemente no convierte: es un aviso para quien llama, nunca un desborde.</para>
///
/// <para>A la fecha de esta entrega, los recolectores de la 2a (<c>RetirosRecolector</c>,
/// <c>AdvisorRecolector</c>) ya entregan las fechas de retiro tipadas como <see cref="DateOnly"/>
/// leídas de una columna de base de datos, no como texto crudo de un CSV: este parser queda
/// disponible para cualquier insumo que sí traiga la fecha como texto (por ejemplo, si el bloque
/// de postura llega a necesitar leer un CSV de Advisor directamente), pero hoy ningún llamador
/// conocido lo necesita. Se documenta la duda en el informe de esta entrega.</para>
///
/// <para><b>2. La fecha de corte y toda fecha relativa se resuelven en America/Guayaquil, una
/// sola vez</b> (Global Constraints): <see cref="ResolverFechaEnGuayaquil"/> es el único punto
/// del módulo que convierte un instante a fecha local. El resultado (un <see cref="DateOnly"/>)
/// se guarda en <see cref="ContextoInformeValor.Corte"/> y de ahí en adelante viaja como valor.
/// </para>
/// </summary>
public static class Fechas
{
    private static readonly string[] FormatosEeuu = ["M/d/yyyy", "MM/dd/yyyy", "M/dd/yyyy", "MM/d/yyyy"];

    /// <summary>
    /// Fecha en formato de Estados Unidos (mes/día/año), nunca día/mes. Devuelve false —en vez de
    /// lanzar o desbordar— cuando <paramref name="raw"/> no matchea o el mes/día no es válido
    /// (p. ej. "13/5/2026"): es responsabilidad de quien llama convertir ese false en un aviso
    /// para el consultor, nunca en una fecha inventada.
    /// </summary>
    public static bool TryParseFormatoEeuu(string? raw, out DateOnly fecha)
    {
        fecha = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return DateOnly.TryParseExact(
            raw.Trim(), FormatosEeuu, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha);
    }

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
}
