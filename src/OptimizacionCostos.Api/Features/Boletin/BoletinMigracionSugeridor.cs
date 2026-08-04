using System.Text.Json;
using System.Text.Json.Serialization;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>Borrador de ruta de migración sugerido por la IA para UN anuncio sin ruta
/// (<see cref="AnuncioSinRuta"/>). Efímero: nada se persiste hasta que el consultor lo guarde vía el
/// CRUD de <see cref="IBoletinMigracionStore"/>.</summary>
public sealed record MigracionSugerencia(
    string Clave, string Desde, string Hacia, string Notas,
    string MatchPattern, string? LearnMoreUrl, string AnnouncementTitle);

public interface IBoletinMigracionSugeridor
{
    /// <summary>true si Azure OpenAI tiene endpoint+key+deployment+apiVersion (mismo criterio que
    /// BoletinNovedadEvaluator/BoletinTranslationService/WAF).</summary>
    bool IsConfigured { get; }

    /// <summary>Sugiere rutas desde→hacia para un lote de anuncios sin ruta. Chunks de 10 anuncios
    /// por llamada; 3 intentos con backoff por chunk. A diferencia de BoletinNovedadEvaluator, el
    /// modelo puede omitir anuncios legítimamente (van a <c>SinSugerencia</c>) sin que eso invalide
    /// el resto del chunk — solo una respuesta ilegible (no JSON) agota los 3 intentos y lanza.</summary>
    Task<(IReadOnlyList<MigracionSugerencia> Sugerencias, IReadOnlyList<string> SinSugerencia)> SugerirAsync(
        IReadOnlyList<AnuncioSinRuta> anuncios, CancellationToken ct = default);
}

/// <summary>Espejo estructural de BoletinNovedadEvaluator (mismo IChatCompletionClient síncrono vía
/// Task.Run, mismos 3 intentos con backoff 2^(n+1), mismo patrón de logging por intento). Difiere en
/// la severidad del contrato de respuesta: el evaluador exige el conjunto EXACTO de guids del lote
/// (longitud + pertenencia), porque ahí "faltar uno" sería indistinguible de un guid inventado; acá
/// el prompt INSTRUYE explícitamente al modelo a omitir anuncios sin destino claro en vez de
/// inventar uno — omitir es la respuesta correcta, no un fallo — así que el parser solo rechaza (y
/// dispara reintento) ante una respuesta verdaderamente ilegible (no JSON / sin corchetes).</summary>
public sealed class BoletinMigracionSugeridor(
    IChatCompletionClient chat, AppConfig config, ILogger<BoletinMigracionSugeridor> logger) : IBoletinMigracionSugeridor
{
    private const int ChunkSize = 10;
    private const int MaxAttempts = 3;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiKey)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiDeployment)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiVersion);

    public async Task<(IReadOnlyList<MigracionSugerencia> Sugerencias, IReadOnlyList<string> SinSugerencia)> SugerirAsync(
        IReadOnlyList<AnuncioSinRuta> anuncios, CancellationToken ct = default)
    {
        if (anuncios.Count == 0) return ([], []);

        var sugerencias = new List<MigracionSugerencia>();
        var sinSugerencia = new List<string>();
        for (var i = 0; i < anuncios.Count; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = anuncios.Skip(i).Take(ChunkSize).ToList();
            var (chunkSugerencias, chunkSinSugerencia) = await SugerirChunkAsync(chunk, ct);
            // Omitir anuncios sin destino es comportamiento LEGÍTIMO del prompt, pero un chunk
            // entero en cero también es la firma de un modelo perezoso devolviendo "[]": queda
            // la señal en el log para poder distinguirlos al diagnosticar (hallazgo del review T4).
            if (chunkSugerencias.Count == 0)
                logger.LogWarning(
                    "sugeridor migracion boletin: chunk de {Count} anuncio(s) sin ninguna sugerencia del modelo",
                    chunk.Count);
            sugerencias.AddRange(chunkSugerencias);
            sinSugerencia.AddRange(chunkSinSugerencia);
        }
        return (sugerencias, sinSugerencia);
    }

    private async Task<(IReadOnlyList<MigracionSugerencia> Sugerencias, IReadOnlyList<string> SinSugerencia)> SugerirChunkAsync(
        List<AnuncioSinRuta> chunk, CancellationToken ct)
    {
        var expectedTitles = chunk.Select(a => a.Title).ToList();
        var anunciosDto = chunk.Select(a => new AnuncioItemDto(a.Title, a.Summary, a.RecommendedAction)).ToList();
        var userJson = JsonSerializer.Serialize(new RequestDto(anunciosDto));
        var maxTokens = Math.Clamp(chunk.Count * 700, 1000, 8000);

        // Mismo diagnóstico que BoletinNovedadEvaluator.EvaluarChunkAsync: `last` guarda la excepción
        // real (queda null si el fallo fue de parse, no de una excepción de red/auth).
        Exception? last = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Seam síncrono IChatCompletionClient (motor de costos); Task.Run como el evaluador/WAF.
                var raw = await Task.Run(() => chat.Complete(BoletinPrompts.SugerirMigracionSystem, userJson, maxTokens), ct);
                var parsed = BoletinMigracionParsers.ParseSugerencias(raw, expectedTitles);
                if (parsed is not null)
                {
                    var foundTitles = parsed.Select(s => s.AnnouncementTitle).ToHashSet(StringComparer.Ordinal);
                    var sinSugerencia = expectedTitles.Where(t => !foundTitles.Contains(t)).ToList();
                    return (parsed, sinSugerencia);
                }
                logger.LogWarning(
                    "sugeridor migracion boletin: respuesta ilegible en intento {Attempt} de {Max}",
                    attempt + 1, MaxAttempts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                logger.LogWarning(ex, "sugeridor migracion boletin: intento {Attempt} de {Max} fallo", attempt + 1, MaxAttempts);
            }
            if (attempt < MaxAttempts - 1)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct);
        }
        throw new InvalidOperationException("No se pudo obtener la sugerencia de Azure OpenAI.", last);
    }

    private sealed record AnuncioItemDto(
        [property: JsonPropertyName("titulo")] string Titulo,
        [property: JsonPropertyName("resumen")] string? Resumen,
        [property: JsonPropertyName("accion_recomendada")] string? AccionRecomendada);

    private sealed record RequestDto([property: JsonPropertyName("anuncios")] List<AnuncioItemDto> Anuncios);
}

/// <summary>Parsers puros del sugeridor IA de rutas de migración: sin I/O, testeables sin Azure ni red.</summary>
internal static class BoletinMigracionParsers
{
    /// <summary>Valida y parsea la respuesta cruda de la IA. Tolera fences ```json ...``` (recorte
    /// entre el primer '[' y el último ']', igual que BoletinEvaluatorParsers.ParseRespuesta).
    /// Devuelve null (dispara reintento) SOLO si la respuesta es ilegible: vacía, sin corchetes, o
    /// JSON inválido. A diferencia del evaluador de novedades, un array JSON válido con longitud
    /// distinta a <paramref name="expectedTitles"/> NO es un fallo: el prompt instruye a omitir
    /// anuncios sin destino claro, así que un array corto (incluso vacío) es una respuesta legítima —
    /// el caller calcula qué títulos quedaron sin sugerencia por diferencia de conjuntos.
    /// Anti-alucinación por item (nunca invalida el chunk entero, solo descarta ESE item):
    /// - `titulo_anuncio` ausente, fuera del lote, o repetido (el primero gana) se descarta.
    /// - Cualquier campo obligatorio (clave/desde/hacia/notas/match_pattern) vacío o ausente descarta
    ///   la sugerencia completa (preferible omitir a persistir un borrador incompleto).
    /// - `match_pattern` se normaliza a minúsculas (trim + ToLowerInvariant, mismo criterio que
    ///   BuildMigracionFields del CRUD) por si el modelo no respeta la regla del prompt.
    /// - `learn_more_url` que no sea una URL http(s) absoluta se fuerza a null en vez de descartar la
    ///   sugerencia entera (defensivo: nunca se inventa ni se propaga una URL no verificable).</summary>
    internal static IReadOnlyList<MigracionSugerencia>? ParseSugerencias(string? raw, IReadOnlyList<string> expectedTitles)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return null;

        List<SugerenciaDto>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<SugerenciaDto>>(raw[start..(end + 1)]);
        }
        catch (JsonException) { return null; }
        if (parsed is null) return null;

        var expectedSet = expectedTitles.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MigracionSugerencia>();
        foreach (var item in parsed)
        {
            if (string.IsNullOrWhiteSpace(item.TituloAnuncio)) continue;
            if (!expectedSet.Contains(item.TituloAnuncio)) continue; // título ajeno al lote: descarta ESTE item
            if (!seen.Add(item.TituloAnuncio)) continue; // repetido: gana el primero, se descarta el resto
            if (string.IsNullOrWhiteSpace(item.Clave) || string.IsNullOrWhiteSpace(item.Desde) ||
                string.IsNullOrWhiteSpace(item.Hacia) || string.IsNullOrWhiteSpace(item.Notas) ||
                string.IsNullOrWhiteSpace(item.MatchPattern))
                continue; // campo obligatorio vacío: preferible omitir a persistir un borrador incompleto

            var learnMoreUrl = item.LearnMoreUrl;
            if (!string.IsNullOrWhiteSpace(learnMoreUrl) &&
                !(Uri.TryCreate(learnMoreUrl, UriKind.Absolute, out var parsedUrl) &&
                  (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps)))
                learnMoreUrl = null;

            result.Add(new MigracionSugerencia(
                item.Clave!, item.Desde!, item.Hacia!, item.Notas!,
                item.MatchPattern!.Trim().ToLowerInvariant(), learnMoreUrl, item.TituloAnuncio));
        }
        return result;
    }

    private sealed record SugerenciaDto(
        [property: JsonPropertyName("titulo_anuncio")] string? TituloAnuncio,
        [property: JsonPropertyName("clave")] string? Clave,
        [property: JsonPropertyName("desde")] string? Desde,
        [property: JsonPropertyName("hacia")] string? Hacia,
        [property: JsonPropertyName("notas")] string? Notas,
        [property: JsonPropertyName("match_pattern")] string? MatchPattern,
        [property: JsonPropertyName("learn_more_url")] string? LearnMoreUrl);
}
