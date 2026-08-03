using System.Text.Json;
using System.Text.Json.Serialization;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>Tipo de recurso presente en el inventario Azure de un cliente (fila resumida de
/// <see cref="BoletinQueries.TiposDeRecurso"/>): es el único contexto que recibe la IA — no puede
/// razonar sobre nada que no esté en esta lista.</summary>
public sealed record TipoRecurso(string Type, int Cantidad);

/// <summary>Resultado de evaluar una novedad del feed contra el inventario de un cliente.</summary>
public sealed record EvaluacionNovedad(string FeedGuid, bool Aplica, string? PorQue);

public interface IBoletinNovedadEvaluator
{
    /// <summary>true si Azure OpenAI tiene endpoint+key+deployment+apiVersion (mismo criterio que
    /// BoletinTranslationService/WAF).</summary>
    bool IsConfigured { get; }

    /// <summary>Evalúa un lote de novedades contra el inventario del cliente. Chunks de 10 novedades
    /// por llamada; 3 intentos con backoff; longitud/guids deben calzar exacto o se reintenta.</summary>
    Task<IReadOnlyList<EvaluacionNovedad>> EvaluarAsync(
        IReadOnlyList<TipoRecurso> inventario, IReadOnlyList<NovedadRow> novedades, CancellationToken ct = default);
}

/// <summary>Espejo estructural de BoletinTranslationService (mismo IChatCompletionClient síncrono vía
/// Task.Run, mismos 3 intentos con backoff 2^(n+1), mismo patrón de logging por intento): a diferencia
/// de la traducción (fidelidad léxica), acá la IA razona sobre el inventario REAL del cliente para
/// decidir si cada novedad le aplica — por eso el contrato de respuesta es más estricto: debe traer
/// EXACTAMENTE el conjunto de guids del lote (no solo la misma longitud), y el prompt prohíbe
/// explícitamente inventar recursos que el cliente no tiene.</summary>
public sealed class BoletinNovedadEvaluator(
    IChatCompletionClient chat, AppConfig config, ILogger<BoletinNovedadEvaluator> logger) : IBoletinNovedadEvaluator
{
    private const int ChunkSize = 10;
    private const int MaxAttempts = 3;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiKey)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiDeployment)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiVersion);

    public async Task<IReadOnlyList<EvaluacionNovedad>> EvaluarAsync(
        IReadOnlyList<TipoRecurso> inventario, IReadOnlyList<NovedadRow> novedades, CancellationToken ct = default)
    {
        if (novedades.Count == 0) return [];

        // El inventario es el mismo para todos los chunks (contexto fijo); solo el lote de
        // novedades varía por llamada.
        var inventarioDto = inventario.Select(t => new InventarioItemDto(t.Type, t.Cantidad)).ToList();

        var result = new List<EvaluacionNovedad>(novedades.Count);
        for (var i = 0; i < novedades.Count; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = novedades.Skip(i).Take(ChunkSize).ToList();
            result.AddRange(await EvaluarChunkAsync(inventarioDto, chunk, ct));
        }
        return result;
    }

    private async Task<IReadOnlyList<EvaluacionNovedad>> EvaluarChunkAsync(
        List<InventarioItemDto> inventarioDto, List<NovedadRow> chunk, CancellationToken ct)
    {
        var expectedGuids = chunk.Select(n => n.FeedGuid).ToList();
        // Prefiere el texto ya curado/traducido (TituloEs/DescripcionEs) cuando existe; cae al
        // original EN si la traducción aún no corrió o la IA de traducción está apagada.
        var novedadesDto = chunk.Select(n => new NovedadItemDto(
            n.FeedGuid,
            n.TituloEs ?? n.Titulo,
            n.DescripcionEs ?? n.Descripcion,
            ParseCategorias(n.CategoriasFeedJson))).ToList();
        var userJson = JsonSerializer.Serialize(new RequestDto(inventarioDto, novedadesDto));
        var maxTokens = Math.Clamp(chunk.Count * 700, 1000, 8000);

        // Mismo diagnóstico que BoletinTranslationService.TranslateBatchAsync: `last` guarda la
        // excepción real (queda null si el fallo fue de parse/guids, no de una excepción de
        // red/auth), y cada intento fallido se loguea de inmediato.
        Exception? last = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Seam síncrono IChatCompletionClient (motor de costos); Task.Run como E1/WAF.
                var raw = await Task.Run(() => chat.Complete(BoletinPrompts.EvaluarNovedadesSystem, userJson, maxTokens), ct);
                var parsed = BoletinEvaluatorParsers.ParseRespuesta(raw, expectedGuids);
                if (parsed is not null) return parsed;
                logger.LogWarning(
                    "evaluacion boletin: respuesta invalida en intento {Attempt} de {Max} (guids incompletos/desconocidos)",
                    attempt + 1, MaxAttempts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                logger.LogWarning(ex, "evaluacion boletin: intento {Attempt} de {Max} fallo", attempt + 1, MaxAttempts);
            }
            if (attempt < MaxAttempts - 1)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct);
        }
        throw new InvalidOperationException("No se pudo obtener la evaluacion de Azure OpenAI.", last);
    }

    private static IReadOnlyList<string> ParseCategorias(string categoriasFeedJson)
    {
        if (string.IsNullOrWhiteSpace(categoriasFeedJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(categoriasFeedJson) ?? []; }
        catch (JsonException) { return []; }
    }

    private sealed record InventarioItemDto(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("cantidad")] int Cantidad);

    private sealed record NovedadItemDto(
        [property: JsonPropertyName("guid")] string Guid,
        [property: JsonPropertyName("titulo")] string Titulo,
        [property: JsonPropertyName("descripcion")] string Descripcion,
        [property: JsonPropertyName("categorias")] IReadOnlyList<string> Categorias);

    private sealed record RequestDto(
        [property: JsonPropertyName("inventario")] List<InventarioItemDto> Inventario,
        [property: JsonPropertyName("novedades")] List<NovedadItemDto> Novedades);
}

/// <summary>Parsers puros del evaluador IA de novedades: sin I/O, testeables sin Azure ni red.</summary>
public static class BoletinEvaluatorParsers
{
    /// <summary>Parsea una fila de <see cref="BoletinQueries.TiposDeRecurso"/>. Descarta filas sin
    /// `type` (defensivo: no debería ocurrir con esa KQL, pero honestidad ante datos inesperados en
    /// vez de fabricar un tipo vacío).</summary>
    public static TipoRecurso? FromTipoRow(RgRow row)
    {
        var type = (row.Str("type") ?? "").Trim();
        if (type.Length == 0) return null;
        return new TipoRecurso(type, row.Int("cantidad") ?? 0);
    }

    /// <summary>Valida y parsea la respuesta cruda de la IA. Tolera fences ```json ...``` (recorte
    /// entre el primer '[' y el último ']', igual que BoletinTranslationService.ParseStringArray).
    /// Rechaza (devuelve null, dispara reintento) si: no es JSON válido, la longitud no calza con
    /// <paramref name="expectedGuids"/>, algún guid no pertenece al lote, o hay guids repetidos que
    /// esconderían un guid faltante detrás de un duplicado (longitud correcta pero conjunto
    /// incompleto). El orden es libre: se re-empareja por guid, no por posición.
    /// Defensivo con la regla dura del prompt "ante la duda aplica=false, por_que=null": si la IA no
    /// la respeta, `por_que` se fuerza a null igual — nunca se expone texto colgado de un
    /// aplica=false mal formado.</summary>
    public static IReadOnlyList<EvaluacionNovedad>? ParseRespuesta(string? raw, IReadOnlyList<string> expectedGuids)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return null;

        List<EvaluacionDto>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<EvaluacionDto>>(raw[start..(end + 1)]);
        }
        catch (JsonException) { return null; }
        if (parsed is null || parsed.Count != expectedGuids.Count) return null;

        var expectedSet = expectedGuids.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<EvaluacionNovedad>(parsed.Count);
        foreach (var item in parsed)
        {
            if (string.IsNullOrWhiteSpace(item.Guid)) return null;
            if (!expectedSet.Contains(item.Guid)) return null;
            if (!seen.Add(item.Guid)) return null; // duplicado: esconde un guid faltante
            result.Add(new EvaluacionNovedad(item.Guid, item.Aplica, item.Aplica ? item.PorQue : null));
        }
        return result;
    }

    private sealed record EvaluacionDto(
        [property: JsonPropertyName("guid")] string? Guid,
        [property: JsonPropertyName("aplica")] bool Aplica,
        [property: JsonPropertyName("por_que")] string? PorQue);
}
