using System.Globalization;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AzureIntegration;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Servicio de Advisor Score. Port de la orquestación de app/waf/advisor_score.py.
/// Reutiliza IAdvisorApiClient (fetch ARM), IAdvisorScoreStore (BD) y IAzureCredentialFactory
/// (validación de credencial para distinguir errores credencial vs suscripción).
/// </summary>
public sealed class AdvisorScoreService(
    IAdvisorApiClient apiClient,
    IAdvisorScoreStore store,
    IAzureCredentialFactory credentials,
    ISqlConnectionFactory factory,
    ILogger<AdvisorScoreService> logger) : IAdvisorScoreService
{
    private static readonly int[] Pillars = [1, 2, 3, 4, 5];

    public Task<IReadOnlyDictionary<int, AdvisorScoreData>> FetchSubscriptionScoreAsync(
        int credentialId, string subscriptionId, CancellationToken ct = default) =>
        apiClient.FetchSubscriptionScoreAsync(credentialId, subscriptionId, ct);

    public Task<(IReadOnlyList<AdvisorRow> Rows, WafIngestionMetrics Metrics)> GenerateAndListRecommendationsAsync(
        int credentialId, string subscriptionId, string subscriptionName, CancellationToken ct = default) =>
        apiClient.GenerateAndListRecommendationsAsync(credentialId, subscriptionId, subscriptionName, ct: ct);

    // ---------------------------------------------------------------------
    // compute_advisor_score
    // ---------------------------------------------------------------------
    public async Task<WafAdvisorScoreResult> ComputeClientScoreAsync(
        int clientId, bool includeBreakdown, CancellationToken ct = default)
    {
        var groups = await store.LoadClientSubscriptionGroupsAsync(clientId, ct);
        return await ComputeScoreAsync(groups, includeBreakdown, ct);
    }

    /// <summary>Port directo de compute_advisor_score (groups -> resultado agregado).</summary>
    private async Task<WafAdvisorScoreResult> ComputeScoreAsync(
        IReadOnlyDictionary<int, WafSubscriptionGroup> groups, bool detail, CancellationToken ct)
    {
        if (groups.Count == 0)
        {
            return new WafAdvisorScoreResult(
                HasConnection: false,
                SubscriptionsScored: 0,
                Pillars: new Dictionary<string, decimal>(),
                Warnings: [],
                Breakdown: [],
                Status: "no_connection");
        }

        var weightedSum = new Dictionary<int, double>();
        var weightSum = new Dictionary<int, double>();
        var simpleSum = new Dictionary<int, double>();
        var simpleCount = new Dictionary<int, int>();
        var scored = 0;
        var warnings = new List<WafScoreWarning>();
        var breakdown = new List<WafScoreBreakdown>();

        foreach (var (credentialId, group) in groups)
        {
            // credential_loader(credential_id): un fallo aquí descarta TODAS las subs de la credencial.
            try
            {
                await credentials.GetClientSecretCredentialAsync(credentialId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo cargar credencial Azure credential_id={Cred}", credentialId);
                warnings.Add(new WafScoreWarning(
                    CredentialId: credentialId,
                    CredentialName: group.CredentialName,
                    SubscriptionId: null,
                    SubscriptionName: null,
                    Error: ErrorText(ex)));
                continue;
            }

            foreach (var (subscriptionId, subscriptionName) in group.Subscriptions)
            {
                try
                {
                    var scores = await apiClient.FetchSubscriptionScoreAsync(credentialId, subscriptionId, ct);
                    scored++;
                    foreach (var (pillar, data) in scores)
                    {
                        var score = (double)data.Score;
                        var weight = (double)data.Weight;
                        weightedSum[pillar] = weightedSum.GetValueOrDefault(pillar) + score * weight;
                        weightSum[pillar] = weightSum.GetValueOrDefault(pillar) + weight;
                        simpleSum[pillar] = simpleSum.GetValueOrDefault(pillar) + score;
                        simpleCount[pillar] = simpleCount.GetValueOrDefault(pillar) + 1;
                    }
                    if (detail)
                    {
                        var scoresByKey = scores.ToDictionary(
                            kvp => kvp.Key.ToString(CultureInfo.InvariantCulture),
                            kvp => kvp.Value);
                        breakdown.Add(new WafScoreBreakdown(subscriptionId, subscriptionName, scoresByKey));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Advisor score failed subscription_id={Sub}", subscriptionId);
                    warnings.Add(new WafScoreWarning(
                        CredentialId: null,
                        CredentialName: null,
                        SubscriptionId: subscriptionId,
                        SubscriptionName: subscriptionName,
                        Error: ErrorText(ex)));
                }
            }
        }

        var pillars = new Dictionary<string, decimal>();
        foreach (var pillar in simpleCount.Keys)
        {
            double value = weightSum.GetValueOrDefault(pillar) > 0
                ? weightedSum[pillar] / weightSum[pillar]
                : simpleSum[pillar] / simpleCount[pillar];
            pillars[pillar.ToString(CultureInfo.InvariantCulture)] = (decimal)Math.Round(value, 2);
        }

        string status;
        if (scored > 0 && warnings.Count > 0) status = "partial";
        else if (scored > 0) status = "completed";
        else if (warnings.Count > 0) status = "failed";
        else status = "completed";

        return new WafAdvisorScoreResult(
            HasConnection: true,
            SubscriptionsScored: scored,
            Pillars: pillars,
            Warnings: warnings,
            Breakdown: breakdown,
            Status: status);
    }

    // ---------------------------------------------------------------------
    // refresh_client_advisor_score
    // ---------------------------------------------------------------------
    public async Task<WafAdvisorScoreSnapshot?> RefreshClientAsync(
        int clientId, DateOnly? snapshotDate, string source, bool includeInReports, CancellationToken ct = default)
    {
        // Fase 1: validar cliente + cargar grupos.
        await EnsureClientExistsAsync(clientId, ct);
        var groups = await store.LoadClientSubscriptionGroupsAsync(clientId, ct);

        // Cálculo (llamadas vivas a ARM).
        var score = await ComputeScoreAsync(groups, detail: true, ct);

        // Fase 2: persistencia.
        return await store.PersistSnapshotAsync(clientId, score, snapshotDate, source, includeInReports, ct);
    }

    private async Task EnsureClientExistsAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.clients WHERE client_id = @id";
        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@id", clientId));
        var found = await cmd.ExecuteScalarAsync(ct);
        if (found is null or DBNull)
            throw new InvalidOperationException($"Client {clientId} not found");
    }

    // ---------------------------------------------------------------------
    // refresh_all_advisor_scores
    // ---------------------------------------------------------------------
    public async Task<WafRefreshAllResult> RefreshAllAsync(
        IReadOnlyList<int>? clientIds, string source, bool includeInReports, CancellationToken ct = default)
    {
        var ids = clientIds ?? await store.ListActiveClientIdsAsync(ct);

        var results = new List<WafRefreshClientResult>(ids.Count);
        var ok = 0;
        var failed = 0;
        foreach (var clientId in ids)
        {
            try
            {
                var snapshot = await RefreshClientAsync(clientId, snapshotDate: null, source, includeInReports, ct);
                ok++;
                results.Add(new WafRefreshClientResult(
                    ClientId: clientId,
                    Status: snapshot?.Status ?? "unknown",
                    SnapshotDate: snapshot is null ? null : snapshot.SnapshotDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CapturedAt: snapshot is null ? null : DateTimeText(snapshot.CapturedAt),
                    SubscriptionsScored: snapshot?.SubscriptionsScored,
                    Error: null));
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Advisor score refresh failed client_id={Cid}", clientId);
                results.Add(new WafRefreshClientResult(
                    ClientId: clientId,
                    Status: "failed",
                    SnapshotDate: null,
                    CapturedAt: null,
                    SubscriptionsScored: null,
                    Error: ErrorText(ex)));
            }
        }

        return new WafRefreshAllResult(
            ClientsTotal: ids.Count,
            ClientsRefreshed: ok,
            ClientsFailed: failed,
            Results: results);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    /// <summary>Port de f"{type(exc).__name__}: {str(exc)[:300]}".</summary>
    private static string ErrorText(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 300) msg = msg[..300];
        return $"{ex.GetType().Name}: {msg}";
    }

    /// <summary>Port de _datetime_text: DATETIME2 (UTC asumido) -> "yyyy-MM-ddTHH:mm:ssZ".</summary>
    private static string DateTimeText(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        return utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
