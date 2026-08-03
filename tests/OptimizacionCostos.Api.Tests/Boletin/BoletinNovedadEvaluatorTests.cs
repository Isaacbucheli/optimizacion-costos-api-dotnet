using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Tests.Boletin;

/// <summary>Espejo del harness de BoletinTranslationServiceTests (mismo FakeChat), pero para el
/// evaluador de "aplica/por_que": acá el contrato es más estricto (guids exactos, no solo longitud),
/// así que hay tests dedicados de ParseRespuesta ademas de los de EvaluarAsync end-to-end.</summary>
public class BoletinNovedadEvaluatorTests
{
    private sealed class FakeChat : IChatCompletionClient
    {
        public List<(string System, string User)> Calls { get; } = [];
        public Queue<string?> Responses { get; } = new();
        public string? Complete(string systemPrompt, string userJson, int maxCompletionTokens = 500)
        {
            Calls.Add((systemPrompt, userJson));
            return Responses.Count > 0 ? Responses.Dequeue() : null;
        }
    }

    private static AppConfig Configured() => new()
    {
        AzureOpenAiEndpoint = "https://x.openai.azure.com",
        AzureOpenAiApiKey = "k",
        AzureOpenAiDeployment = "d",
        AzureOpenAiApiVersion = "2025-04-01-preview",
    };

    private static ILogger<BoletinNovedadEvaluator> Logger() => NullLogger<BoletinNovedadEvaluator>.Instance;

    private static NovedadRow Novedad(string guid) => new(
        1, guid, "Titulo " + guid, null, "Descripcion " + guid, null,
        "https://azure.microsoft.com/updates?id=" + guid, "launched", "resiliencia_plataforma", "[]",
        DateTime.UtcNow, true);

    // ---------------- BoletinPrompts.EvaluarNovedadesSystem ----------------

    [Fact]
    public void SystemPromptTranscribeLasReglasDuras()
    {
        var p = BoletinPrompts.EvaluarNovedadesSystem;
        Assert.Contains("SOLO", p);
        Assert.Contains("PROHIBIDO", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aplica", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("por_que", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATOS", p);
    }

    // ---------------- BoletinEvaluatorParsers.FromTipoRow ----------------

    [Fact]
    public void FromTipoRow_ParseaTypeYCantidad()
    {
        var row = new RgRow(JsonNode.Parse("""{"type":"microsoft.sql/servers/databases","cantidad":12}"""));
        var tipo = BoletinEvaluatorParsers.FromTipoRow(row);
        Assert.NotNull(tipo);
        Assert.Equal("microsoft.sql/servers/databases", tipo!.Type);
        Assert.Equal(12, tipo.Cantidad);
    }

    [Fact]
    public void FromTipoRow_DescartaFilaSinType()
    {
        var row = new RgRow(JsonNode.Parse("""{"cantidad":5}"""));
        Assert.Null(BoletinEvaluatorParsers.FromTipoRow(row));
    }

    // ---------------- BoletinEvaluatorParsers.ParseRespuesta ----------------

    [Fact]
    public void ParseRespuesta_AceptaRespuestaValidaConFences()
    {
        var raw = "```json\n[{\"guid\":\"g1\",\"aplica\":true,\"por_que\":\"usas 3 maquinas virtuales\"}," +
                   "{\"guid\":\"g2\",\"aplica\":false,\"por_que\":null}]\n```";
        var result = BoletinEvaluatorParsers.ParseRespuesta(raw, ["g1", "g2"]);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.True(result.Single(e => e.FeedGuid == "g1").Aplica);
        Assert.Equal("usas 3 maquinas virtuales", result.Single(e => e.FeedGuid == "g1").PorQue);
        Assert.False(result.Single(e => e.FeedGuid == "g2").Aplica);
        Assert.Null(result.Single(e => e.FeedGuid == "g2").PorQue);
    }

    [Fact]
    public void ParseRespuesta_AceptaOrdenDistintoAlEsperado()
    {
        var raw = """[{"guid":"g2","aplica":false,"por_que":null},{"guid":"g1","aplica":true,"por_que":"x"}]""";
        var result = BoletinEvaluatorParsers.ParseRespuesta(raw, ["g1", "g2"]);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains(result, e => e.FeedGuid == "g1" && e.Aplica);
        Assert.Contains(result, e => e.FeedGuid == "g2" && !e.Aplica);
    }

    [Fact]
    public void ParseRespuesta_RechazaLongitudIncorrecta()
    {
        var raw = """[{"guid":"g1","aplica":true,"por_que":"x"}]"""; // esperaba 2
        Assert.Null(BoletinEvaluatorParsers.ParseRespuesta(raw, ["g1", "g2"]));
    }

    [Fact]
    public void ParseRespuesta_RechazaGuidDesconocido()
    {
        var raw = """[{"guid":"g1","aplica":true,"por_que":"x"},{"guid":"g-no-existe","aplica":false,"por_que":null}]""";
        Assert.Null(BoletinEvaluatorParsers.ParseRespuesta(raw, ["g1", "g2"]));
    }

    [Fact]
    public void ParseRespuesta_RechazaGuidDuplicadoQueEsconderiaUnFaltante()
    {
        // longitud correcta (2) pero repite g1 y omite g2: no debe colarse como si estuviera completo.
        var raw = """[{"guid":"g1","aplica":true,"por_que":"x"},{"guid":"g1","aplica":false,"por_que":null}]""";
        Assert.Null(BoletinEvaluatorParsers.ParseRespuesta(raw, ["g1", "g2"]));
    }

    [Fact]
    public void ParseRespuesta_NuloVacioONoJsonEsInvalido()
    {
        Assert.Null(BoletinEvaluatorParsers.ParseRespuesta(null, ["g1"]));
        Assert.Null(BoletinEvaluatorParsers.ParseRespuesta("", ["g1"]));
        Assert.Null(BoletinEvaluatorParsers.ParseRespuesta("esto no es json", ["g1"]));
    }

    [Fact]
    public void ParseRespuesta_ForzaPorQueNuloSiAplicaEsFalse()
    {
        // Defensivo: aunque la IA no respete "ante la duda aplica=false, por_que=null", el parser
        // lo fuerza igual — nunca se expone un por_que colgado de un aplica=false.
        var raw = """[{"guid":"g1","aplica":false,"por_que":"texto que no deberia sobrevivir"}]""";
        var result = BoletinEvaluatorParsers.ParseRespuesta(raw, ["g1"]);
        Assert.NotNull(result);
        Assert.Null(result!.Single().PorQue);
    }

    // ---------------- BoletinNovedadEvaluator.EvaluarAsync ----------------

    [Fact]
    public void SinConfiguracionNoEstaConfigurado()
    {
        var svc = new BoletinNovedadEvaluator(new FakeChat(), new AppConfig(), Logger());
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public async Task ListaVaciaNoLlamaALaIa()
    {
        var chat = new FakeChat();
        var svc = new BoletinNovedadEvaluator(chat, Configured(), Logger());
        var result = await svc.EvaluarAsync([], []);
        Assert.Empty(result);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task DocePorTanda_HaceDosLlamadasDeAChunkDe10()
    {
        var chat = new FakeChat();
        var guidsChunk1 = Enumerable.Range(1, 10).Select(i => $"g{i}").ToArray();
        var guidsChunk2 = new[] { "g11", "g12" };
        chat.Responses.Enqueue(JsonSerializer.Serialize(
            guidsChunk1.Select(g => new { guid = g, aplica = false, por_que = (string?)null })));
        chat.Responses.Enqueue(JsonSerializer.Serialize(
            guidsChunk2.Select(g => new { guid = g, aplica = true, por_que = "usas recursos de computo" })));

        var novedades = Enumerable.Range(1, 12).Select(i => Novedad($"g{i}")).ToList();
        var svc = new BoletinNovedadEvaluator(chat, Configured(), Logger());
        var inventario = new List<TipoRecurso> { new("microsoft.compute/virtualmachines", 5) };

        var result = await svc.EvaluarAsync(inventario, novedades);

        Assert.Equal(2, chat.Calls.Count);
        Assert.Equal(12, result.Count);
        Assert.All(result.Where(r => guidsChunk1.Contains(r.FeedGuid)), r => Assert.False(r.Aplica));
        Assert.All(result.Where(r => guidsChunk2.Contains(r.FeedGuid)), r => Assert.True(r.Aplica));
    }

    [Fact]
    public async Task ReintentaCuandoLaRespuestaEsInvalida()
    {
        var chat = new FakeChat();
        chat.Responses.Enqueue("""[{"guid":"g-no-existe","aplica":true,"por_que":"x"}]"""); // guid ajeno al lote
        chat.Responses.Enqueue("""[{"guid":"g1","aplica":true,"por_que":"usas 1 maquina virtual"}]""");
        var svc = new BoletinNovedadEvaluator(chat, Configured(), Logger());

        var result = await svc.EvaluarAsync([], [Novedad("g1")]);

        Assert.Equal(2, chat.Calls.Count);
        Assert.Single(result);
        Assert.True(result[0].Aplica);
        Assert.Equal("usas 1 maquina virtual", result[0].PorQue);
    }

    [Fact]
    public async Task LanzaExcepcionTrasAgotarLosTresIntentos()
    {
        var chat = new FakeChat();
        chat.Responses.Enqueue("""[{"guid":"g-otro","aplica":true,"por_que":"x"}]""");
        chat.Responses.Enqueue("""[{"guid":"g-otro","aplica":true,"por_que":"x"}]""");
        chat.Responses.Enqueue("""[{"guid":"g-otro","aplica":true,"por_que":"x"}]""");
        var svc = new BoletinNovedadEvaluator(chat, Configured(), Logger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EvaluarAsync([], [Novedad("g1")]));
        Assert.Equal(3, chat.Calls.Count);
    }

    [Fact]
    public async Task AplicaFalseConservaPorQueNulo()
    {
        var chat = new FakeChat();
        chat.Responses.Enqueue("""[{"guid":"g1","aplica":false,"por_que":null}]""");
        var svc = new BoletinNovedadEvaluator(chat, Configured(), Logger());

        var result = await svc.EvaluarAsync([], [Novedad("g1")]);

        Assert.False(result[0].Aplica);
        Assert.Null(result[0].PorQue);
    }
}
