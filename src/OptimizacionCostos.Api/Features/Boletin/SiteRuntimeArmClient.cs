using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace OptimizacionCostos.Api.Features.Boletin;

public sealed record WindowsSiteRef(string SubscriptionId, string SiteId, string SiteName);

/// <summary><c>Truncated</c>: el lote de sitios Windows excedía <see cref="SiteRuntimeArmClient.MaxSites"/>
/// y se recortó — señal explícita de cobertura incompleta (junto con <c>FailedCount &gt; 0</c>), para
/// que el caller decida si las filas derivadas de esta credencial son confiables este sync.</summary>
public sealed record SiteRuntimeArmResult(IReadOnlyList<SiteRuntime> Sites, IReadOnlyList<string> Warnings, int FailedCount, bool Truncated);

public interface ISiteRuntimeArmClient
{
    /// <summary>GET {site}/config/web por cada app Windows (el runtime no está en Resource Graph).
    /// Cap MaxSites=300 por lote con advertencia. Fallo por sitio = warning, jamás aborta el lote.</summary>
    Task<SiteRuntimeArmResult> FetchAsync(TokenCredential credential, IReadOnlyList<WindowsSiteRef> sites, CancellationToken ct = default);
}

/// <summary>Runtimes de apps Windows vía ARM (Resource Graph no los expone; decisión del usuario
/// 2026-08-03: cobertura completa). Doctrina de StorageFilesEnricher: token una vez por lote,
/// cancelación real siempre propaga, fallo por sitio = warning contado, nunca cero silencioso.</summary>
public sealed class SiteRuntimeArmClient(IHttpClientFactory httpFactory, ILogger<SiteRuntimeArmClient> logger) : ISiteRuntimeArmClient
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmBase = "https://management.azure.com";
    private const string ApiVersion = "2023-12-01";
    /// <summary>Tope de sitios por lote (evita barrer tenants gigantes; se advierte si se excede).</summary>
    internal const int MaxSites = 300;

    public async Task<SiteRuntimeArmResult> FetchAsync(TokenCredential credential, IReadOnlyList<WindowsSiteRef> sites, CancellationToken ct = default)
    {
        if (sites.Count == 0) return new([], [], 0, false);

        var warnings = new List<string>();
        var lote = sites;
        var truncated = sites.Count > MaxSites;
        if (truncated)
        {
            warnings.Add($"Apps Windows: {sites.Count} sitios, se consultan solo {MaxSites} (tope por sincronización).");
            lote = sites.Take(MaxSites).ToList();
        }

        var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        var result = new List<SiteRuntime>();
        var failed = 0;
        foreach (var site in lote)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{ArmBase}{site.SiteId}/config/web?api-version={ApiVersion}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                using var res = await http.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                result.AddRange(ParseSiteConfig(site, doc.RootElement));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "config/web fallo site={Site}", site.SiteName);
            }
        }
        if (failed > 0)
            warnings.Add($"Apps Windows: {failed} de {lote.Count} sitios no respondieron config/web (runtime desconocido para esos sitios).");
        return new(result, warnings, failed, truncated);
    }

    /// <summary>Puro y testeable: mapea properties de config/web a tokens de runtime normalizados.</summary>
    internal static IReadOnlyList<SiteRuntime> ParseSiteConfig(WindowsSiteRef site, JsonElement root)
    {
        var runtimes = new List<SiteRuntime>();
        if (!root.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) return runtimes;

        void Add(string family, string? raw, bool skipClassicNet = false)
        {
            var v = (raw ?? "").Trim().TrimStart('~', 'v', 'V');
            if (v.Length == 0) return;
            // netFrameworkVersion v4.x es .NET Framework clásico, no .NET moderno: se omite
            // para no generar falsos positivos con los retiros de ".NET N".
            if (skipClassicNet && v.StartsWith("4", StringComparison.Ordinal)) return;
            runtimes.Add(new SiteRuntime(site.SubscriptionId, site.SiteId, site.SiteName, $"{family}|{v}"));
        }

        static string? Str(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

        Add("DOTNET", Str(p, "netFrameworkVersion"), skipClassicNet: true);
        Add("NODE", Str(p, "nodeVersion"));
        Add("PHP", Str(p, "phpVersion"));
        Add("PYTHON", Str(p, "pythonVersion"));
        Add("POWERSHELL", Str(p, "powerShellVersion"));
        Add("JAVA", Str(p, "javaVersion"));
        return runtimes;
    }
}
