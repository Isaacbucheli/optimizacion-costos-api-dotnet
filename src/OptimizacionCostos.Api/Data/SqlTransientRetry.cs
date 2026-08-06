namespace OptimizacionCostos.Api.Data;

/// <summary>
/// Reintento de la APERTURA de una conexión a Azure SQL. Azure corta el handshake cada tantos días
/// por reconfiguraciones de la plataforma, y sin reintento cada corte se lleva por delante la
/// petición que le tocó: el 2026-08-06 a las 15:37 UTC el login devolvió 500 con
///
///   SqlException: A connection was successfully established with the server, but then an error
///   occurred during the login process. (provider: TCP Provider, error: 35)
///     ---> SocketException (104): Connection reset by peer
///
/// El mismo error está en el log del 2026-07-29 (5 veces) y del 2026-07-30 (3 veces), con la base
/// al 0% de DTU y 1% de sesiones, así que no es saturación ni carga: es exactamente el caso para el
/// que Microsoft pide reintento del lado del cliente.
///
/// Envuelve SOLO la apertura, no la ejecución de comandos. Abrir es idempotente; repetir un comando
/// que ya llegó al servidor no lo es, y un reintento a ciegas podría duplicar una escritura.
///
/// A propósito NO se usa <c>SqlConfigurableRetryFactory</c> (el reintento nativo del driver): decide
/// con una lista blanca de números de error, y el error de arriba viene de la capa SNI, cuyo número
/// depende de la implementación y no está documentado. Una lista blanca que no lo incluya deja el
/// fallo tal como está, que es justo lo que se quiere arreglar.
/// </summary>
public static class SqlTransientRetry
{
    /// <summary>Intentos totales, no reintentos: 3 = el original y dos repeticiones.</summary>
    public const int Intentos = 3;

    /// <summary>
    /// Esperas entre intentos. Cortas a propósito: el corte de red se detecta de inmediato (no es un
    /// timeout) y del otro lado hay una persona esperando la pantalla de ingreso. En el caso que
    /// motivó esto, el reintento completo agrega un segundo.
    /// </summary>
    private static readonly TimeSpan[] Esperas =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(800),
    ];

    /// <summary>
    /// Errores que NO se reintentan. Es una lista negra y no blanca: al abrir la conexión, todo lo
    /// que puede fallar es transporte, servidor ocupado o configuración, y solo el último grupo es
    /// permanente. Con lista blanca habría que adivinar el número de cada fallo de red posible.
    /// </summary>
    private static readonly HashSet<int> NoReintentables =
    [
        18456, // Login failed for user: la contraseña no va a cambiar por insistir, y cada intento
               // suma un fallo de autenticación en la auditoría del servidor.
        18452, // Login failed, dominio no confiable.
        18470, // Cuenta deshabilitada.
          916, // El principal no tiene acceso a la base.
        40615, // Cannot open server: la IP no está en el firewall.
        40532, // Cannot open server requested by the login: nombre de servidor o ruteo mal armados.
           -2, // Timeout. Se excluye a propósito aunque Azure lo considere transitorio: ConnectTimeout
               // ya son 30 segundos, así que tres intentos serían minuto y medio colgado y el
               // navegador (o la persona) abandona mucho antes.
    ];

    /// <summary>
    /// true si vale la pena repetir la apertura. Un número desconocido cuenta como reintentable: es
    /// el caso de los errores de la capa SNI, que son justamente los transitorios.
    /// </summary>
    public static bool EsReintentable(int numeroDeError) => !NoReintentables.Contains(numeroDeError);

    /// <summary>Cuánto esperar después del intento fallido número <paramref name="intentoFallido"/> (base 1).</summary>
    public static TimeSpan Espera(int intentoFallido) =>
        Esperas[Math.Clamp(intentoFallido, 1, Esperas.Length) - 1];

    /// <summary>
    /// Ejecuta <paramref name="accion"/> hasta <see cref="Intentos"/> veces mientras el error sea
    /// reintentable. La última excepción sube tal cual, con su stack original.
    /// </summary>
    /// <param name="esReintentable">
    /// Decide sobre la excepción concreta. Se inyecta porque <c>SqlException</c> no tiene
    /// constructor público y no se puede fabricar en una prueba.
    /// </param>
    /// <param name="antesDeReintentar">
    /// Se llama entre intentos con (intento fallido, causa, espera). Es quien registra y quien
    /// espera; separarlo deja las pruebas sin relojes.
    /// </param>
    public static async Task<T> EjecutarAsync<T>(
        Func<CancellationToken, Task<T>> accion,
        Func<Exception, bool> esReintentable,
        Func<int, Exception, TimeSpan, CancellationToken, Task> antesDeReintentar,
        CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                return await accion(ct);
            }
            // Filtro y no catch+throw: así la excepción del último intento sube con su stack intacto,
            // que es el que dice en qué consulta se cayó.
            catch (Exception ex) when (intento < Intentos
                                       && !ct.IsCancellationRequested
                                       && esReintentable(ex))
            {
                await antesDeReintentar(intento, ex, Espera(intento), ct);
            }
        }
    }
}
