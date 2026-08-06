using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace OptimizacionCostos.Api.Auth;

/// <summary>
/// Respuesta compartida para el enfriamiento por operacion. Vive junto a RateLimitSetup porque es la
/// misma familia (control de tasa) y en un solo lugar para que los endpoints no dupliquen el formato
/// del mensaje ni se olviden del Retry-After.
/// </summary>
public static class CooldownResults
{
    /// <summary>
    /// 429 con <c>Retry-After</c> y el mismo cuerpo <c>{ detail }</c> del resto de la API. Se usa 429 y
    /// no 409 porque es una regla de tasa: asi el cliente sabe cuanto esperar sin interpretar el texto.
    /// </summary>
    /// <param name="operacion">Sujeto de la frase, ej. "El barrido de optimización".</param>
    public static IActionResult EnCooldown(this ControllerBase controller, TimeSpan falta, string operacion)
    {
        var segundos = Math.Max(1, (int)Math.Ceiling(falta.TotalSeconds));
        controller.Response.Headers.RetryAfter = segundos.ToString(CultureInfo.InvariantCulture);
        return controller.StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            detail = $"{operacion} se ejecutó hace poco para este cliente. "
                   + $"Intenta de nuevo en {Describir(segundos)}.",
            retry_after_seconds = segundos,
        });
    }

    /// <summary>Minutos cuando la espera es larga: "en 9 minutos" se lee mejor que "en 540 segundos".</summary>
    private static string Describir(int segundos) =>
        segundos >= 120 ? $"{(int)Math.Ceiling(segundos / 60.0)} minutos" : $"{segundos} segundos";
}
