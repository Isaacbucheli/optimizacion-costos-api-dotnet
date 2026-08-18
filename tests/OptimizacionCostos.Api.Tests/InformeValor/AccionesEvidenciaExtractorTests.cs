using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Entrega 8, pieza B: la extracción de acciones ejecutadas desde evidencia textual con Azure
/// OpenAI. El seam es <see cref="IChatCompletionClient"/> (el mismo de curación WAF, informe
/// mensual y traducción): acá se falsifica y se prueba el contrato del extractor — parseo
/// tolerante, degradación a null, y nada más.
/// </summary>
public class AccionesEvidenciaExtractorTests
{
    private sealed class FakeChat(string? respuesta) : IChatCompletionClient
    {
        public string? UltimoSystemPrompt { get; private set; }
        public string? Complete(string systemPrompt, string userJson, int maxCompletionTokens = 500)
        {
            UltimoSystemPrompt = systemPrompt;
            return respuesta;
        }
    }

    [Fact]
    public async Task Extrae_candidatas_del_json_del_modelo()
    {
        var chat = new FakeChat("""{"acciones":[{"oportunidad":"Apagado de VMs de desarrollo","mes":"2026-07","monto":450.0,"recurso":null,"cita":"se apagaron las 3 VMs... ahorro estimado de $450 mensuales"}]}""");
        var candidatas = await new AccionesEvidenciaExtractor(chat).ExtraerAsync("correo del cliente...", CancellationToken.None);

        var c = Assert.Single(candidatas!);
        Assert.Equal("Apagado de VMs de desarrollo", c.Oportunidad);
        Assert.Equal("2026-07", c.MesEjecucion);
        Assert.Equal(450m, c.MontoMensual);
        Assert.Null(c.Recurso);
        Assert.NotNull(c.Cita);
    }

    [Fact]
    public async Task Tolera_fences_de_markdown_alrededor_del_json()
    {
        var chat = new FakeChat("```json\n{\"acciones\":[]}\n```");
        var candidatas = await new AccionesEvidenciaExtractor(chat).ExtraerAsync("x", CancellationToken.None);
        Assert.NotNull(candidatas);
        Assert.Empty(candidatas!);
    }

    [Fact]
    public async Task Devuelve_null_cuando_el_modelo_no_responde_o_no_es_json()
    {
        Assert.Null(await new AccionesEvidenciaExtractor(new FakeChat(null)).ExtraerAsync("x", CancellationToken.None));
        Assert.Null(await new AccionesEvidenciaExtractor(new FakeChat("no es json")).ExtraerAsync("x", CancellationToken.None));
        Assert.Null(await new AccionesEvidenciaExtractor(new FakeChat("{\"otra_clave\": 1}")).ExtraerAsync("x", CancellationToken.None));
    }

    /// <summary>Una candidata sin oportunidad no afirma nada: se descarta en el parseo, sin tumbar
    /// a las demás.</summary>
    [Fact]
    public async Task Una_candidata_sin_oportunidad_se_descarta()
    {
        var chat = new FakeChat("""{"acciones":[{"oportunidad":"","mes":null,"monto":null,"recurso":null,"cita":null},{"oportunidad":"Reducción de plan","mes":null,"monto":null,"recurso":null,"cita":"se completó la reducción"}]}""");
        var candidatas = await new AccionesEvidenciaExtractor(chat).ExtraerAsync("x", CancellationToken.None);
        Assert.Equal("Reducción de plan", Assert.Single(candidatas!).Oportunidad);
    }

    /// <summary>Las tres reglas duras de la decisión 2026-08-18 viven en el prompt: si alguien las
    /// borra, este candado lo dice.</summary>
    [Fact]
    public async Task El_prompt_fija_las_reglas_de_no_inventar()
    {
        var chat = new FakeChat("""{"acciones":[]}""");
        await new AccionesEvidenciaExtractor(chat).ExtraerAsync("x", CancellationToken.None);

        Assert.Contains("PROHIBIDO estimar", chat.UltimoSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("ya realizadas", chat.UltimoSystemPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cita", chat.UltimoSystemPrompt!, StringComparison.OrdinalIgnoreCase);
    }
}
