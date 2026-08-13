using System.Text.Json.Nodes;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// <see cref="OpexRecolector"/>: el score del pilar de costos (5) de Advisor, hoy + su serie
/// mensual, para la tarjeta "Opex" del resumen (entrega 6/7).
/// </summary>
public class OpexRecolectorTests
{
    /// <summary>Solo implementa los dos miembros que usa <see cref="OpexRecolector"/>; el resto de
    /// <see cref="IAdvisorScoreStore"/> revienta a propósito -- ningún test de esta clase debería
    /// llegar a llamarlos.</summary>
    private sealed class FakeAdvisorScoreStore(WafAdvisorScoreSnapshot? snap, IReadOnlyList<ClientScoreHistoryPoint> hist)
        : IAdvisorScoreStore
    {
        public Task<WafAdvisorScoreSnapshot?> LoadLatestSnapshotAsync(int clientId, bool includeBreakdown = false, CancellationToken ct = default)
            => Task.FromResult(snap);

        public Task<IReadOnlyList<ClientScoreHistoryPoint>> LoadHistoryAsync(int clientId, char granularity, CancellationToken ct = default)
            => Task.FromResult(hist);

        public Task<IReadOnlyList<int>> ListActiveClientIdsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<int, WafSubscriptionGroup>> LoadClientSubscriptionGroupsAsync(
            int clientId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<WafAdvisorScoreSnapshot?> PersistSnapshotAsync(
            int clientId, WafAdvisorScoreResult score, DateOnly? snapshotDate,
            string source, bool includeInReports, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> TryAcquireJobLockAsync(
            string jobName, int leaseSeconds = 21600, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<JsonObject> BuildScoreHistoryAsync(int clientId, int year, int month, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task PersistHistoryAsync(int clientId, IReadOnlyList<ClientScoreHistory> histories, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private static WafAdvisorScoreSnapshot Snap(decimal? p5) => new(
        SnapshotId: 1, ClientId: 7, SnapshotDate: new DateOnly(2026, 8, 1),
        CapturedAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), Status: "ok",
        HasConnection: true, SubscriptionsScored: 3,
        ScoreP1: 90, ScoreP2: 80, ScoreP3: 50, ScoreP4: 70, ScoreP5: p5,
        WarningsJson: null, BreakdownJson: null, Source: "sync", IncludeInReports: true);

    /// <summary>El caso normal: score actual + la serie mensual del pilar 5.</summary>
    [Fact]
    public async Task Con_snapshot_y_historia_el_score_queda_medido()
    {
        var hist = new List<ClientScoreHistoryPoint>
        {
            new(new DateOnly(2025, 9, 1), new Dictionary<int, decimal> { [0] = 70, [5] = 59 }),
            new(new DateOnly(2026, 8, 1), new Dictionary<int, decimal> { [0] = 84, [5] = 92 }),
        };
        var opex = await OpexRecolector.LeerAsync(new FakeAdvisorScoreStore(Snap(92), hist), 7);

        Assert.True(opex.Medido);
        Assert.Equal(92m, opex.Actual);
        Assert.Equal(2, opex.Serie.Count);
        Assert.Equal(59m, opex.Serie[0].Score);
    }

    /// <summary>Un punto de historia sin pilar 5 no entra: no se inventa un cero (D9).</summary>
    [Fact]
    public async Task Los_puntos_sin_pilar_de_costos_quedan_fuera_de_la_serie()
    {
        var hist = new List<ClientScoreHistoryPoint>
        {
            new(new DateOnly(2026, 1, 1), new Dictionary<int, decimal> { [0] = 70 }),   // sin clave 5
            new(new DateOnly(2026, 2, 1), new Dictionary<int, decimal> { [5] = 80 }),
        };
        var opex = await OpexRecolector.LeerAsync(new FakeAdvisorScoreStore(Snap(80), hist), 7);
        Assert.Single(opex.Serie);
    }

    /// <summary>Sin snapshot: la tarjeta dice "sin medición", nunca 0% (spec, tarjeta Opex).</summary>
    [Fact]
    public async Task Sin_snapshot_no_esta_medido_y_declara_motivo()
    {
        var opex = await OpexRecolector.LeerAsync(new FakeAdvisorScoreStore(null, []), 7);
        Assert.False(opex.Medido);
        Assert.Null(opex.Actual);
        Assert.NotEmpty(opex.Motivo!);
    }

    /// <summary>Snapshot sin pilar de costos (score_p5 NULL): distinto de no tener snapshot.</summary>
    [Fact]
    public async Task Snapshot_sin_pilar_de_costos_no_esta_medido()
    {
        var opex = await OpexRecolector.LeerAsync(new FakeAdvisorScoreStore(Snap(null), []), 7);
        Assert.False(opex.Medido);
        Assert.Contains("pilar", opex.Motivo!, StringComparison.OrdinalIgnoreCase);
    }
}
