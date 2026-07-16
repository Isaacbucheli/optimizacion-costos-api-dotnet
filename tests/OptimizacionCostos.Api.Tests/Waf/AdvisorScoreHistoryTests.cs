using System.Text.Json;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

public sealed class AdvisorScoreHistoryTests
{
    // Respuesta advisorScore recortada: categoría Security con día+mes, y global "Advisor" con día.
    private const string Response = """
    {"value":[
      {"name":"Security","properties":{
        "lastRefreshedScore":{"date":"2026-06-25T00:00:00Z","score":45,"consumptionUnits":10},
        "timeSeries":[
          {"aggregationLevel":"day","scoreHistory":[
            {"date":"2026-06-25T00:00:00Z","score":45,"consumptionUnits":10},
            {"date":"2026-06-24T00:00:00Z","score":44,"consumptionUnits":10}]},
          {"aggregationLevel":"month","scoreHistory":[
            {"date":"2026-06-25T00:00:00Z","score":45,"consumptionUnits":10}]}
        ]}},
      {"name":"Advisor","properties":{
        "timeSeries":[
          {"aggregationLevel":"day","scoreHistory":[
            {"date":"2026-06-25T00:00:00Z","score":76,"consumptionUnits":10}]}
        ]}}
    ]}
    """;

    [Fact]
    public void ParseScoreHistory_mapea_pilar_global_y_granularidades()
    {
        using var doc = JsonDocument.Parse(Response);
        var result = AdvisorApiClient.ParseScoreHistory(doc.RootElement);

        var day = result.Single(h => h.Granularity == 'D');
        // Serie 3 = Security (2 puntos), serie 0 = global (1 punto).
        Assert.Equal(2, day.Series[3].Count);
        Assert.Single(day.Series[0]);
        Assert.Equal(45m, day.Series[3][0].Score);
        Assert.Equal(new DateOnly(2026, 6, 25), day.Series[3][0].Date);
        Assert.Equal(76m, day.Series[0][0].Score);

        var month = result.Single(h => h.Granularity == 'M');
        Assert.True(month.Series.ContainsKey(3));
        Assert.False(month.Series.ContainsKey(0)); // global no trajo serie mensual
    }

    [Fact]
    public void ParseScoreHistory_ignora_categorias_no_mapeables_y_niveles_desconocidos()
    {
        const string body = """
        {"value":[
          {"name":"Foo","properties":{"timeSeries":[{"aggregationLevel":"day","scoreHistory":[{"date":"2026-06-25T00:00:00Z","score":1,"consumptionUnits":1}]}]}},
          {"name":"Cost","properties":{"timeSeries":[{"aggregationLevel":"year","scoreHistory":[{"date":"2026-06-25T00:00:00Z","score":1,"consumptionUnits":1}]}]}}
        ]}
        """;
        using var doc = JsonDocument.Parse(body);
        var result = AdvisorApiClient.ParseScoreHistory(doc.RootElement);
        Assert.Empty(result); // Foo no mapea; "year" no es D/W/M → nada
    }
}
