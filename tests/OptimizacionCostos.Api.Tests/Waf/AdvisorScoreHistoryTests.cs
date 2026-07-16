using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AzureIntegration;
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

    [Fact]
    public void Aggregate_pondera_por_consumo_entre_suscripciones()
    {
        // Dos subs, misma fecha, misma serie 3 (Security): sub A score 40 peso 10, sub B score 80 peso 30.
        // Ponderado = (40*10 + 80*30) / (10+30) = 2800/40 = 70.
        var date = new DateOnly(2026, 6, 1);
        AdvisorHistoryPoint P(decimal s, decimal w) => new(date, s, w);
        SubscriptionScoreHistory Sub(decimal s, decimal w) => new('M',
            new Dictionary<int, IReadOnlyList<AdvisorHistoryPoint>> { [3] = new[] { P(s, w) } });

        var result = ScoreHistory.Aggregate(new[] { Sub(40, 10), Sub(80, 30) });

        var month = result.Single(h => h.Granularity == 'M');
        var point = month.Points.Single();
        Assert.Equal(date, point.Date);
        Assert.Equal(70m, point.Series[3]);
    }

    [Fact]
    public void Aggregate_sin_peso_usa_media_simple_y_ordena_por_fecha()
    {
        var early = new DateOnly(2026, 5, 1);
        var late = new DateOnly(2026, 6, 1);
        var sub = new SubscriptionScoreHistory('M', new Dictionary<int, IReadOnlyList<AdvisorHistoryPoint>>
        {
            [0] = new[] { new AdvisorHistoryPoint(late, 60, 0), new AdvisorHistoryPoint(early, 40, 0) },
        });

        var month = ScoreHistory.Aggregate(new[] { sub }).Single(h => h.Granularity == 'M');

        Assert.Equal(new[] { early, late }, month.Points.Select(p => p.Date).ToArray()); // orden asc
        Assert.Equal(40m, month.Points[0].Series[0]);
    }

    [Fact]
    public void GranularityChar_mapea_y_default_month()
    {
        Assert.Equal('D', ScoreHistory.GranularityChar("day"));
        Assert.Equal('W', ScoreHistory.GranularityChar("week"));
        Assert.Equal('M', ScoreHistory.GranularityChar("month"));
        Assert.Equal('M', ScoreHistory.GranularityChar("otra"));
        Assert.Equal('M', ScoreHistory.GranularityChar(null));
    }

    [Fact]
    public void BuildResponse_expone_global_y_pilares_1a5_con_null_donde_falta()
    {
        var points = new List<ClientScoreHistoryPoint>
        {
            new(new DateOnly(2026, 6, 1), new Dictionary<int, decimal> { [0] = 76m, [3] = 45m }),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(ScoreHistory.BuildResponse("month", points));

        Assert.Contains("\"granularity\":\"month\"", json);
        Assert.Contains("\"date\":\"2026-06-01\"", json);
        Assert.Contains("\"global\":76", json);
        Assert.Contains("\"3\":45", json);
        Assert.Contains("\"1\":null", json); // pilar sin dato → null explícito
    }

    private sealed class FakeApi : IAdvisorApiClient
    {
        private readonly Dictionary<string, IReadOnlyList<SubscriptionScoreHistory>> _bySub;
        public FakeApi(Dictionary<string, IReadOnlyList<SubscriptionScoreHistory>> bySub) => _bySub = bySub;
        public Task<IReadOnlyList<SubscriptionScoreHistory>> FetchSubscriptionScoreHistoryAsync(
            int credentialId, string subscriptionId, CancellationToken ct = default)
            => _bySub.TryGetValue(subscriptionId, out var h)
                ? Task.FromResult(h)
                : throw new AdvisorApiException("boom"); // sub sin datos → error, best-effort la salta
        public Task<IReadOnlyDictionary<int, AdvisorScoreData>> FetchSubscriptionScoreAsync(
            int credentialId, string subscriptionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<AdvisorRow> Rows, WafIngestionMetrics Metrics)> GenerateAndListRecommendationsAsync(
            int credentialId, string subscriptionId, string subscriptionName,
            int timeoutSeconds = 600, int pageSize = 200, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CaptureStore : IAdvisorScoreStore
    {
        private readonly IReadOnlyDictionary<int, WafSubscriptionGroup> _groups;
        public IReadOnlyList<ClientScoreHistory>? Persisted { get; private set; }
        public CaptureStore(IReadOnlyDictionary<int, WafSubscriptionGroup> groups) => _groups = groups;
        public Task<IReadOnlyDictionary<int, WafSubscriptionGroup>> LoadClientSubscriptionGroupsAsync(int clientId, CancellationToken ct = default)
            => Task.FromResult(_groups);
        public Task PersistHistoryAsync(int clientId, IReadOnlyList<ClientScoreHistory> histories, CancellationToken ct = default)
        { Persisted = histories; return Task.CompletedTask; }
        public Task<IReadOnlyList<ClientScoreHistoryPoint>> LoadHistoryAsync(int clientId, char granularity, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> ListActiveClientIdsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WafAdvisorScoreSnapshot?> PersistSnapshotAsync(int clientId, WafAdvisorScoreResult score, DateOnly? snapshotDate, string source, bool includeInReports, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WafAdvisorScoreSnapshot?> LoadLatestSnapshotAsync(int clientId, bool includeBreakdown = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TryAcquireJobLockAsync(string jobName, int leaseSeconds = 21600, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<System.Text.Json.Nodes.JsonObject> BuildScoreHistoryAsync(int clientId, int year, int month, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingCreds : IAzureCredentialFactory
    {
        public Task<TokenCredential> GetClientSecretCredentialAsync(int credentialId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialAuthResult> TestCredentialAuthAsync(int credentialId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateValidationStatusAsync(int credentialId, bool success, string? errorMessage = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task WriteAuditLogAsync(int credentialId, string action, string? actor = null, string? details = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class ThrowingFactory : ISqlConnectionFactory
    {
        public Task<Microsoft.Data.SqlClient.SqlConnection> OpenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RefreshClientScoreHistoryAsync_agrega_dos_subs_y_persiste()
    {
        var group = new WafSubscriptionGroup(1, "cred", new Dictionary<string, string> { ["sA"] = "A", ["sB"] = "B" });
        var groups = new Dictionary<int, WafSubscriptionGroup> { [1] = group };

        SubscriptionScoreHistory Hist(decimal score, decimal weight) => new('M',
            new Dictionary<int, IReadOnlyList<AdvisorHistoryPoint>>
            { [0] = new[] { new AdvisorHistoryPoint(new DateOnly(2026, 6, 1), score, weight) } });

        var api = new FakeApi(new()
        {
            ["sA"] = new[] { Hist(40, 10) },
            ["sB"] = new[] { Hist(80, 30) },
        });
        var store = new CaptureStore(groups);
        var svc = new AdvisorScoreService(api, store, new ThrowingCreds(), new ThrowingFactory(), NullLogger<AdvisorScoreService>.Instance);

        await svc.RefreshClientScoreHistoryAsync(clientId: 7, CancellationToken.None);

        var month = store.Persisted!.Single(h => h.Granularity == 'M');
        Assert.Equal(70m, month.Points.Single().Series[0]); // (40*10+80*30)/40
    }
}
