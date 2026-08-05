using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Auth;

/// <summary>
/// Limite de tasa global. La API no tenia ninguno: cualquier usuario autenticado podia pedir en bucle
/// los 26 endpoints que disparan trabajo externo (ARM, Graph, Advisor) o IA facturable. Las dos
/// guardas de concurrencia que existian solo evitan corridas SIMULTANEAS, no repetidas, y por eso el
/// escaneo del 2026-08-03 ejecuto seis revisiones de accesos reales contra el tenant de un cliente:
/// cada una terminaba antes de que llegara el siguiente request.
///
/// Es la capa ancha, no la fina. Frena la rafaga en toda la superficie, incluidos los endpoints que
/// no enumeramos y los que se agreguen despues. El cooldown por operacion y cliente, que expresa la
/// regla de negocio ("nadie necesita re-sincronizar Advisor seis veces en cuarenta minutos"), va
/// aparte y encima de esto.
/// </summary>
public static class RateLimitSetup
{
    public const string PolicyName = "bit-global";

    /// <summary>El probe de arranque de App Service pega acá; un 429 lo marcaria como no sano.</summary>
    private const string RutaExenta = "/health";

    private static readonly TimeSpan Ventana = TimeSpan.FromMinutes(1);
    private const int SegmentosPorVentana = 6;

    public static IServiceCollection AddBitRateLimiter(this IServiceCollection services, AppConfig config)
    {
        var porMinuto = config.RateLimitPerMinute;

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // 0 apaga el limitador sin sacar el middleware del pipeline.
                if (porMinuto <= 0 || EsRutaExenta(context))
                    return RateLimitPartition.GetNoLimiter("sin-limite");

                return RateLimitPartition.GetSlidingWindowLimiter(Particion(context), _ =>
                    new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = porMinuto,
                        Window = Ventana,
                        // Ventana deslizante y no fija: con ventana fija se pueden meter 2x el limite
                        // a caballo del borde (100 al final de un minuto y 100 al principio del
                        // siguiente), que es justo el patron de una rafaga de fuzzing.
                        SegmentsPerWindow = SegmentosPorVentana,
                        QueueLimit = 0, // rechazar de una; encolar solo alargaria la rafaga
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    });
            });

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                // El limitador de ventana deslizante NO publica RetryAfter cuando QueueLimit es 0:
                // rechaza de inmediato sin calcular cuando se libera un permiso. Se calcula acá con
                // respaldo, porque un 429 sin Retry-After deja al cliente adivinando y un escaner lo
                // reporta como respuesta incompleta.
                var espera = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var m)
                    ? m
                    : Ventana; // la ventana completa: nunca promete menos espera de la real
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(espera.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
                // Mismo formato { detail } que el resto de la API, para que el front lo muestre igual.
                await context.HttpContext.Response.WriteAsync(
                    """{"detail":"Demasiadas peticiones. Espera un momento y vuelve a intentar."}""", ct);
            };
        });

        return services;
    }

    private static bool EsRutaExenta(HttpContext context) =>
        context.Request.Path.StartsWithSegments(RutaExenta, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clave de partición: el usuario autenticado si hay token válido, la IP del cliente si no.
    /// Separar ambos importa porque el tráfico anónimo (login incluido) no debe poder consumir la
    /// cuota de nadie más.
    /// </summary>
    private static string Particion(HttpContext context)
    {
        var email = context.User.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(email))
            return $"u:{email.ToLowerInvariant()}";
        return $"ip:{IpDelCliente(context)}";
    }

    /// <summary>
    /// IP real detrás del proxy de App Service. Se toma la ÚLTIMA entrada de X-Forwarded-For, no la
    /// primera: el cliente puede mandar su propia cabecera y el front end de App Service le agrega la
    /// IP que ve al final, así que quedarse con la primera entrada permitiría elegir la partición a
    /// voluntad y evadir el límite cambiando un header. Sin la cabecera, Kestrel ve la IP del
    /// balanceador y todo el tráfico anónimo comparte cubeta, que es el peor caso aceptable.
    /// </summary>
    private static string IpDelCliente(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var ultima = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .LastOrDefault();
            if (!string.IsNullOrWhiteSpace(ultima))
                return SinPuerto(ultima);
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "desconocida";
    }

    /// <summary>App Service escribe "ip:puerto"; el puerto cambia en cada conexión y partiría la cuota.</summary>
    private static string SinPuerto(string entrada)
    {
        // IPv6 viene entre corchetes ("[::1]:443"); IPv4 como "1.2.3.4:5678".
        if (entrada.StartsWith('['))
        {
            var cierre = entrada.IndexOf(']');
            return cierre > 0 ? entrada[..(cierre + 1)] : entrada;
        }
        var i = entrada.LastIndexOf(':');
        // Un solo ':' es puerto; varios son una IPv6 sin corchetes, que se deja intacta.
        return i > 0 && entrada.IndexOf(':') == i ? entrada[..i] : entrada;
    }
}
