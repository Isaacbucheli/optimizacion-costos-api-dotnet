namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>
/// Ambiente de una suscripción, inferido de su nombre. Es una INFERENCIA, no un dato: lo que no
/// matcha queda `desconocido` y no participa del análisis de segregación — no se adivina.
/// Persistirlo y hacerlo editable por cliente queda como seguimiento; con la convención de nombres
/// real de los clientes (SAPPRD, AnaliticaDEV, Analítica Avanzada - PRD) la inferencia alcanza.
/// </summary>
public static class AccessReviewEnvironment
{
    public const string Produccion = "produccion";
    public const string Preproduccion = "preproduccion";
    public const string Desarrollo = "desarrollo";
    public const string Desconocido = "desconocido";

    // Se compara por token delimitado, no por substring: "PRODUCTOS" contiene "prod" y no es
    // producción, "DESARROLLADORES" contiene "des".
    private static readonly char[] Separators = [' ', '-', '_', '.', '/', '\\', '(', ')', ',', '|', '+'];

    // El orden importa: preproducción va ANTES que producción porque "PRE-PROD" contiene "prod".
    // Sin esa precedencia el hallazgo de segregación compararía producción contra producción.
    //
    // Dos listas por ambiente:
    //  - Words: cuenta como palabra completa del nombre.
    //  - Glued: cuenta además pegado al final de una palabra ("SAPPRD", "AnaliticaDEV").
    // Solo los marcadores inequívocos van en Glued. "des" quedó fuera a propósito: "Ambiente de
    // Redes" terminaría clasificado como desarrollo, y con un ambiente mal clasificado el hallazgo de
    // segregación afirma algo falso. Lo mismo con "pre", "pro" y "qa".
    private static readonly (string Env, string[] Words, string[] Glued)[] Rules =
    [
        (Preproduccion,
            ["qas", "qa", "pre", "preprod", "staging", "stg", "uat", "test", "pruebas", "calidad"],
            ["qas", "uat"]),
        (Desarrollo,
            ["dev", "des", "desa", "desarrollo", "sbx", "sandbox", "lab", "laboratorio", "experimentacion"],
            ["dev", "sbx"]),
        (Produccion,
            ["prd", "prod", "produccion", "pro"],
            ["prd", "prod"]),
    ];

    public static string Classify(string? subscriptionName)
    {
        if (string.IsNullOrWhiteSpace(subscriptionName)) return Desconocido;

        var words = subscriptionName
            .ToLowerInvariant()
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        foreach (var (env, exact, glued) in Rules)
        {
            if (words.Any(w => exact.Contains(w, StringComparer.Ordinal))) return env;
            // Pegado al final y con algo delante: "sapprd" sí, "prd" solo ya lo tomó la lista de palabras.
            if (words.Any(w => glued.Any(g => w.Length > g.Length && w.EndsWith(g, StringComparison.Ordinal))))
                return env;
        }

        return Desconocido;
    }

    public static bool IsProduccion(string environment) => environment == Produccion;

    /// <summary>Solo produccion, preproduccion y desarrollo cuentan como ambiente conocido.</summary>
    public static bool IsKnown(string environment) => environment != Desconocido;

    public static string Label(string environment) => environment switch
    {
        Produccion => "Producción",
        Preproduccion => "Preproducción",
        Desarrollo => "Desarrollo",
        _ => "Sin identificar",
    };
}
