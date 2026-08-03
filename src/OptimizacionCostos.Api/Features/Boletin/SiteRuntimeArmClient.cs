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

    /// <summary>Tope de concurrencia para los GETs config/web: acota la presión sobre ARM y evita
    /// 429 (throttling), y baja ~300 llamadas secuenciales (5-6 min, excede el timeout de ~230s del
    /// App Service en producción) a decenas de segundos.</summary>
    internal const int MaxConcurrency = 10;

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

        // Concurrencia acotada (SemaphoreSlim) en vez del foreach secuencial: cada task devuelve su
        // propio resultado (nunca escribe sobre una List compartida) y se agrega recién al final de
        // Task.WhenAll, así se evita cualquier lock/carrera sobre el acumulador.
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var tasks = lote.Select(site => FetchOneAsync(http, token.Token, site, gate, ct)).ToList();
        var perSite = await Task.WhenAll(tasks);

        var result = new List<SiteRuntime>();
        var failed = 0;
        foreach (var (runtimes, siteFailed) in perSite)
        {
            result.AddRange(runtimes);
            if (siteFailed) failed++;
        }

        if (failed > 0)
            warnings.Add($"Apps Windows: {failed} de {lote.Count} sitios no respondieron config/web (runtime desconocido para esos sitios).");
        return new(result, warnings, failed, truncated);
    }

    /// <summary>Un sitio: adquiere el cupo del semáforo, hace el GET config/web y libera el cupo.
    /// Fallo por sitio = warning contado (Failed=true), jamás aborta el lote — la cancelación real
    /// sí se propaga (catch de OperationCanceledException primero, re-lanza; Task.WhenAll la agrega
    /// al AggregateException y el `await` la vuelve a lanzar tal cual al caller).</summary>
    private async Task<(IReadOnlyList<SiteRuntime> Runtimes, bool Failed)> FetchOneAsync(
        HttpClient http, string token, WindowsSiteRef site, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{ArmBase}{site.SiteId}/config/web?api-version={ApiVersion}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return (ParseSiteConfig(site, doc.RootElement), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "config/web fallo site={Site}", site.SiteName);
            return ([], true);
        }
        finally
        {
            gate.Release();
        }
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
