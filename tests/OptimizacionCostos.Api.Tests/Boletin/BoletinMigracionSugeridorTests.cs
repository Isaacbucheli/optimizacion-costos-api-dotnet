using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Tests.Boletin;

/// <summary>Espejo del harness de BoletinNovedadEvaluatorTests (mismo FakeChat), pero para el
/// sugeridor de rutas de migración (E4, Task 4): a diferencia del evaluador (que exige el conjunto
/// EXACTO de guids), acá el contrato es deliberadamente laxo — el modelo puede omitir anuncios sin
/// invalidar el resto del chunk (van a SinSugerencia), y solo una respuesta verdaderamente ilegible
/// (no JSON / sin corchetes) dispara el reintento.</summary>
public class BoletinMigracionSugeridorTests
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

    private static ILogger<BoletinMigracionSugeridor> Logger() => NullLogger<BoletinMigracionSugeridor>.Instance;

    private static AnuncioSinRuta Anuncio(string title, string? summary = "resumen", string? action = "accion") =>
        new(title, summary, action);

    // ---------------- BoletinPrompts.SugerirMigracionSystem ----------------

    [Fact]
    public void SystemPromptTranscribeLasReglasDuras()
    {
        var p = BoletinPrompts.SugerirMigracionSystem;
        Assert.Contains("SOLO", p);
        Assert.Contains("NO inventes", p);
        Assert.Contains("titulo_anuncio", p);
        Assert.Contains("match_pattern", p);
        Assert.Contains("learn_more_url", p);
        Assert.Contains("DATOS", p);
    }

    // ---------------- BoletinMigracionParsers.ParseSugerencias ----------------

    [Fact]
    public void ParseSugerencias_AceptaRespuestaValida()
    {
        var raw = """
            [{"titulo_anuncio":"Retiro A","clave":"servicio-x","desde":"Servicio X v1","hacia":"Servicio X v2",
              "notas":"Migrar antes de la fecha.","match_pattern":"retiro a","learn_more_url":"https://aka.ms/x"}]
            """;
        var result = BoletinMigracionParsers.ParseSugerencias(raw, ["Retiro A"]);
        Assert.NotNull(result);
        var s = Assert.Single(result!);
        Assert.Equal("Retiro A", s.AnnouncementTitle);
        Assert.Equal("servicio-x", s.Clave);
        Assert.Equal("Servicio X v1", s.Desde);
        Assert.Equal("Servicio X v2", s.Hacia);
        Assert.Equal("Migrar antes de la fecha.", s.Notas);
        Assert.Equal("retiro a", s.MatchPattern);
        Assert.Equal("https://aka.ms/x", s.LearnMoreUrl);
    }

    [Fact]
    public void ParseSugerencias_ToleraFences()
    {
        var raw = "```json\n[{\"titulo_anuncio\":\"Retiro A\",\"clave\":\"c\",\"desde\":\"D\",\"hacia\":\"H\"," +
                  "\"notas\":\"N\",\"match_pattern\":\"p\",\"learn_more_url\":null}]\n```";
        var result = BoletinMigracionParsers.ParseSugerencias(raw, ["Retiro A"]);
        Assert.NotNull(result);
        Assert.Single(result!);
    }

    [Fact]
    public void ParseSugerencias_DescartaTituloAnuncioDesconocido()
    {
        var raw = """
            [{"titulo_anuncio":"Retiro FANTASMA","clave":"c","desde":"D","hacia":"H",
              "notas":"N","match_pattern":"p","learn_more_url":null}]
            """;
        var result = BoletinMigracionParsers.ParseSugerencias(raw, ["Retiro A"]);
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void ParseSugerencias_DescartaTituloAnuncioRepetido()
    {
        var raw = """
            [{"titulo_anuncio":"Retiro A","clave":"primero","desde":"D1","hacia":"H1",
              "notas":"N1","match_pattern":"p1","learn_more_url":null},
             {"titulo_anuncio":"Retiro A","clave":"segundo","desde":"D2","hacia":"H2",
              "notas":"N2","match_pattern":"p2","learn_more_url":null}]
            """;
        var result = BoletinMigracionParsers.ParseSugerencias(raw, ["Retiro A"]);
        Assert.NotNull(result);
        var s = Assert.Single(result!);
        Assert.Equal("primero", s.Clave); // gana el primero, el repetido se descarta
    }

    [Theory]
    [InlineData("clave")]
    [InlineData("desde")]
    [InlineData("hacia")]
    [InlineData("notas")]
    [InlineData("match_pattern")]
    public void ParseSugerencias_DescartaSugerenciaConCampoObligatorioVacio(string campoVacio)
    {
        var campos = new Dictionary<string, string>
        {
            ["clave"] = "c", ["desde"] = "D", ["hacia"] = "H", ["notas"] = "N", ["match_pattern"] = "p",
        };
        campos[campoVacio] = "";
        var raw = JsonSerializer.Serialize(new[]
        {
            new
            {
                titulo_anuncio = "Retiro A",
                clave = campos["clave"],
                desde = campos["desde"],
                hacia = campos["hacia"],
                notas = campos["notas"],
                match_pattern = campos["match_pattern"],
                learn_more_url = (string?)null,
            },
        });
        var result = BoletinMigracionParsers.ParseSugerencias(raw, ["Retiro A"]);
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void ParseSugerencias_NormalizaMatchPatternAMinusculas()
    {
        var raw = """
            [{"titulo_anuncio":"Retiro A","clave":"c","desde":"D","hacia":"H",
              "notas":"N","match_pattern":"  Application Gateway V1  ","learn_more_url":null}]
            """;
        var result = BoletinMigracionParsers.ParseSugerencias(raw, ["Retiro A"]);
        Assert.NotNull(result);
        Assert.Equal("application gateway v1", result!.Single().MatchPattern);
    }

    [Theory]
    [InlineData("ftp://example.com/doc")]
    [InlineData("no es una url")]
    [InlineData("/relativo/a/algo")]
    public void ParseSugerencias_LearnMoreUrlNoHttpSeVuelveNulo(string urlInvalida)
    {
        var raw = JsonSerializer.Serialize(new[]
        {
            new
            {
                titulo_anuncio = "Retiro A", clave = "c", desde = "D", hacia = "H",
                notas = "N", match_pattern = "p", learn_more_url = urlInvalida,
            },
        });
        var result = BoletinMigracionParsers.ParseSugerencias(raw, ["Retiro A"]);
        Assert.NotNull(result);
        Assert.Null(result!.Single().LearnMoreUrl);
    }

    [Fact]
    public void ParseSugerencias_LearnMoreUrlHttpsSePreserva()
    {
        var raw = """
            [{"titulo_anuncio":"Retiro A","clave":"c","desde":"D","hacia":"H",
              "notas":"N","match_pattern":"p","learn_more_url":"https://aka.ms/algo"}]
            """;
        var result = BoletinMigracionParsers.ParseSugerencias(raw, ["Retiro A"]);
        Assert.NotNull(result);
        Assert.Equal("https://aka.ms/algo", result!.Single().LearnMoreUrl);
    }

    [Fact]
    public void ParseSugerencias_NuloVacioONoJsonEsInvalido()
    {
        Assert.Null(BoletinMigracionParsers.ParseSugerencias(null, ["Retiro A"]));
        Assert.Null(BoletinMigracionParsers.ParseSugerencias("", ["Retiro A"]));
        Assert.Null(BoletinMigracionParsers.ParseSugerencias("esto no es json", ["Retiro A"]));
    }

    [Fact]
    public void ParseSugerencias_ArrayVacioEsValidoNoDisparaReintento()
    {
        // El modelo puede legitimamente omitir TODOS los anuncios del lote (ninguno trae destino
        // claro): eso es una respuesta valida, no una respuesta ilegible.
        var result = BoletinMigracionParsers.ParseSugerencias("[]", ["Retiro A", "Retiro B"]);
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    // ---------------- BoletinMigracionSugeridor.SugerirAsync ----------------

    [Fact]
    public void SinConfiguracionNoEstaConfigurado()
    {
        var svc = new BoletinMigracionSugeridor(new FakeChat(), new AppConfig(), Logger());
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public async Task ListaVaciaNoLlamaALaIa()
    {
        var chat = new FakeChat();
        var svc = new BoletinMigracionSugeridor(chat, Configured(), Logger());
        var (sugerencias, sinSugerencia) = await svc.SugerirAsync([]);
        Assert.Empty(sugerencias);
        Assert.Empty(sinSugerencia);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task AnunciosOmitidosPorElModeloTerminanEnSinSugerencia()
    {
        var chat = new FakeChat();
        chat.Responses.Enqueue("""
            [{"titulo_anuncio":"Retiro A","clave":"c","desde":"D","hacia":"H",
              "notas":"N","match_pattern":"p","learn_more_url":null}]
            """); // omite "Retiro B" a propósito
        var svc = new BoletinMigracionSugeridor(chat, Configured(), Logger());

        var (sugerencias, sinSugerencia) = await svc.SugerirAsync(
            [Anuncio("Retiro A"), Anuncio("Retiro B")]);

        Assert.Single(sugerencias);
        Assert.Equal("Retiro A", sugerencias[0].AnnouncementTitle);
        Assert.Equal(["Retiro B"], sinSugerencia);
    }

    [Fact]
    public async Task DocePorTanda_HaceDosLlamadasDeAChunkDe10()
    {
        var chat = new FakeChat();
        var titulosChunk1 = Enumerable.Range(1, 10).Select(i => $"Retiro {i}").ToArray();
        var titulosChunk2 = new[] { "Retiro 11", "Retiro 12" };
        chat.Responses.Enqueue(JsonSerializer.Serialize(titulosChunk1.Select(t => new
        {
            titulo_anuncio = t, clave = "c", desde = "D", hacia = "H", notas = "N",
            match_pattern = "p", learn_more_url = (string?)null,
        })));
        chat.Responses.Enqueue(JsonSerializer.Serialize(titulosChunk2.Select(t => new
        {
            titulo_anuncio = t, clave = "c2", desde = "D2", hacia = "H2", notas = "N2",
            match_pattern = "p2", learn_more_url = (string?)null,
        })));

        var anuncios = Enumerable.Range(1, 12).Select(i => Anuncio($"Retiro {i}")).ToList();
        var svc = new BoletinMigracionSugeridor(chat, Configured(), Logger());

        var (sugerencias, sinSugerencia) = await svc.SugerirAsync(anuncios);

        Assert.Equal(2, chat.Calls.Count);
        Assert.Equal(12, sugerencias.Count);
        Assert.Empty(sinSugerencia);
    }

    [Fact]
    public async Task ReintentaCuandoLaRespuestaEsIlegible()
    {
        var chat = new FakeChat();
        chat.Responses.Enqueue("esto no es json"); // ilegible: dispara reintento
        chat.Responses.Enqueue("""
            [{"titulo_anuncio":"Retiro A","clave":"c","desde":"D","hacia":"H",
              "notas":"N","match_pattern":"p","learn_more_url":null}]
            """);
        var svc = new BoletinMigracionSugeridor(chat, Configured(), Logger());

        var (sugerencias, sinSugerencia) = await svc.SugerirAsync([Anuncio("Retiro A")]);

        Assert.Equal(2, chat.Calls.Count);
        Assert.Single(sugerencias);
        Assert.Empty(sinSugerencia);
    }

    [Fact]
    public async Task LanzaExcepcionTrasAgotarLosTresIntentos()
    {
        var chat = new FakeChat();
        chat.Responses.Enqueue("esto no es json");
        chat.Responses.Enqueue(null);
        chat.Responses.Enqueue("tampoco esto");
        var svc = new BoletinMigracionSugeridor(chat, Configured(), Logger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SugerirAsync([Anuncio("Retiro A")]));
        Assert.Equal(3, chat.Calls.Count);
    }
}
