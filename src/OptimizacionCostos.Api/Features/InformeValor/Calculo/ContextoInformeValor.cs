namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Parámetros comunes a los cinco bloques de la calculadora. Ningún calculador de bloque lee la
/// hora del sistema (Global Constraints del plan de la entrega 2b): el corte y el rango llegan
/// acá, ya resueltos, y todo el cálculo cuelga de este único punto de entrada de tiempo.
///
/// <para><see cref="Corte"/> se resuelve en <c>America/Guayaquil</c> una sola vez, antes de
/// construir este contexto (ver <see cref="Fechas.ResolverFechaEnGuayaquil"/>): ningún bloque
/// vuelve a convertir zonas horarias. Es la fecha contra la que se clasifican los retiros de
/// Azure (vencido / menos de tres meses / menos de un año) y contra la que se decide qué mes es
/// "hoy" para cualquier cálculo relativo. Un informe generado dos veces con el mismo
/// <see cref="Corte"/> tiene que dar exactamente las mismas cifras: por eso es un valor, no un
/// reloj.</para>
///
/// <para><see cref="PeriodStart"/>/<see cref="PeriodEnd"/> son el rango que filtra todo (D0 del
/// plan): a diferencia de la plantilla, que consume el archivo entero, la calculadora restringe
/// cada insumo a este rango antes de agrupar o sumar nada.</para>
///
/// <para><see cref="MesesParcialesForzados"/> es un tri-estado (spec §12.3, punto 3) que manda
/// sobre la heurística automática de detección de meses parciales del bloque de consumo:
/// <c>null</c> = sin declaración del consultor, se aplica la heurística automática; lista vacía =
/// el consultor declaró explícitamente "ningún mes parcial", la heurística NO se aplica aunque
/// hubiera marcado algo; lista con elementos = exactamente esos meses son parciales, tampoco se
/// aplica la heurística. Las claves de mes son <c>"aaaa-MM"</c> (p. ej. <c>"2026-01"</c>), la
/// misma forma que usa toda serie mensual de este módulo.</para>
/// </summary>
public sealed record ContextoInformeValor(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly Corte,
    IReadOnlyList<string>? MesesParcialesForzados);
