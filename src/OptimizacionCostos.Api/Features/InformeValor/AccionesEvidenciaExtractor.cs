using System.Text.Json;
using System.Text.Json.Serialization;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>Una acción ejecutada propuesta por la IA desde la evidencia pegada (entrega 8,
/// pieza B). Es una PROPUESTA: nada se persiste hasta que el consultor la confirme por el alta
/// normal del CRUD. <see cref="Cita"/> es el fragmento textual del que salió cada dato — la
/// defensa de la fila frente al cliente.</summary>
public sealed record AccionCandidata(
    [property: JsonPropertyName("oportunidad")] string Oportunidad,
    [property: JsonPropertyName("mes")] string? MesEjecucion,
    [property: JsonPropertyName("monto")] decimal? MontoMensual,
    [property: JsonPropertyName("recurso")] string? Recurso,
    [property: JsonPropertyName("cita")] string? Cita);

/// <summary>
/// Extrae acciones de optimización YA EJECUTADAS desde evidencia textual (correo, chat, minuta)
/// con el MISMO despliegue de Azure OpenAI que ya usan la curación WAF, el informe mensual y la
/// traducción en vivo (<see cref="IChatCompletionClient"/>): ningún dato del cliente sale a un
/// servicio nuevo.
///
/// <para><b>Las tres reglas duras de la decisión 2026-08-18 viven en el prompt</b> (y en el
/// candado <c>El_prompt_fija_las_reglas_de_no_inventar</c>): la IA propone y el consultor
/// confirma (este servicio no persiste NADA); los montos solo se extraen cuando están textuales
/// en la evidencia (estimar es de las otras fuentes del registro); y cada dato lleva su cita.</para>
///
/// <para>El seam es síncrono a propósito (motor de costos); <c>Task.Run</c> como el patrón del
/// WAF y del boletín. Cualquier respuesta no parseable degrada a <c>null</c> — el endpoint lo
/// traduce a un 502 con instrucciones, nunca a una lista vacía que se lea como "la evidencia no
/// tenía acciones".</para>
/// </summary>
public sealed class AccionesEvidenciaExtractor(IChatCompletionClient chat)
{
    internal const string SystemPrompt =
        "Eres un asistente que extrae ACCIONES DE OPTIMIZACIÓN YA REALIZADAS desde evidencia " +
        "textual (correos, chats, minutas) para el registro de un informe de servicio administrado " +
        "de Azure. Reglas estrictas:\n" +
        "1. Extrae SOLO acciones que la evidencia afirme como ya realizadas o ejecutadas. Nada " +
        "planificado, propuesto o pendiente.\n" +
        "2. \"monto\": SOLO si la evidencia menciona una cifra de ahorro MENSUAL textual. " +
        "PROHIBIDO estimar, calcular o inferir montos. Sin cifra textual: null.\n" +
        "3. \"mes\": formato \"aaaa-MM\", SOLO si la evidencia da la fecha con claridad. Si no: null.\n" +
        "4. \"cita\": copia textual del fragmento del que sale cada acción (máximo 200 caracteres).\n" +
        "5. \"recurso\": el nombre del recurso de Azure si la evidencia lo menciona; si no: null.\n" +
        "6. Si no hay ninguna acción ejecutada, devuelve la lista vacía.\n" +
        "Responde ÚNICAMENTE este JSON, sin explicación ni markdown: " +
        "{\"acciones\":[{\"oportunidad\":\"...\",\"mes\":\"aaaa-MM\"|null,\"monto\":numero|null," +
        "\"recurso\":\"...\"|null,\"cita\":\"...\"|null}]}";

    private static readonly JsonSerializerOptions Opciones = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record Respuesta([property: JsonPropertyName("acciones")] List<AccionCandidata>? Acciones);

    public Task<IReadOnlyList<AccionCandidata>?> ExtraerAsync(string evidencia, CancellationToken ct) =>
        Task.Run(() => Extraer(evidencia), ct);

    internal IReadOnlyList<AccionCandidata>? Extraer(string evidencia)
    {
        string? contenido;
        try
        {
            contenido = chat.Complete(
                SystemPrompt, JsonSerializer.Serialize(new { evidencia }), maxCompletionTokens: 1500);
        }
        catch
        {
            // Mismo criterio que WafTranslationService: una falla del servicio de IA degrada,
            // nunca revienta el endpoint con un 500 opaco.
            return null;
        }
        if (string.IsNullOrWhiteSpace(contenido)) return null;

        // El modelo a veces envuelve el JSON en fences de markdown aunque se le pida que no:
        // se recorta al primer '{' y al último '}' en vez de reconocer secuencias.
        var inicio = contenido.IndexOf('{');
        var fin = contenido.LastIndexOf('}');
        if (inicio < 0 || fin <= inicio) return null;

        try
        {
            var respuesta = JsonSerializer.Deserialize<Respuesta>(contenido[inicio..(fin + 1)], Opciones);
            if (respuesta?.Acciones is null) return null;
            // Una candidata sin oportunidad no afirma nada: fuera, sin tumbar a las demás.
            return respuesta.Acciones
                .Where(a => !string.IsNullOrWhiteSpace(a.Oportunidad))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
