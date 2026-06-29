using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.CostEngine.Engine;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;
using EngineCostEngine = OptimizacionCostos.Api.Features.CostEngine.Engine.CostEngine;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Validación de paridad de fuego de la Fase 2: corre el motor .NET (ComputeResults, SIN
/// persistir → lectura no destructiva) sobre un análisis REAL y compara fila a fila contra
/// los cost_results que dejó el Python. Gateado: solo corre con BIT_DIFF_ANALYSIS_ID + SQL_*
/// en el entorno; si no, es no-op (la suite sigue sin tocar Azure).
///
/// NOTA: usa la capa de precios real (cache azure_retail_prices + Retail API). Para un diff
/// determinista conviene que la cache esté fresca (mismos precios que usó el Python).
/// PricingConstants usa defaults; si prod tiene valores distintos en dbo.pricing_constants,
/// el VM SQL add-on podría diferir (a verificar en la primera corrida).
/// </summary>
public sealed class CostEngineParityDiffTests
{
    private sealed record Stored(double? Payg, double? Ri1y, double? Ri3y, string? Status);

    /// <summary>
    /// Decorador de IPriceCache que NO refresca: reporta todo "fresco" y no escribe nada,
    /// así SqlPriceRepository lee la cache existente (mismos precios que usó el Python) sin
    /// llamar a la Retail API ni tocar dbo.azure_retail_prices. Diff determinista y de solo lectura.
    /// </summary>
    private sealed class NoRefreshPriceCache(IPriceCache inner) : IPriceCache
    {
        public IReadOnlyList<PriceRow> QueryCached(string s, string r, string? a = null, string? k = null)
            => inner.QueryCached(s, r, a, k);
        public bool IsCacheFreshFor(string s, string r, string? a = null, string? k = null) => true;
        public bool IsFetchQueryFresh(string fetchQuery) => true;
        public void Insert(IEnumerable<PriceRow> rows, string fetchQuery) { /* no-op: solo lectura */ }
        public void DeleteStale(string s, string r, string? a = null, string? k = null) { /* no-op */ }
        public void DeleteByFetchQuery(string fetchQuery) { /* no-op */ }
        public int ClearAll() => 0; // no-op: solo lectura
        public IReadOnlyList<OptimizacionCostos.Api.Features.CostEngine.Api.PriceCacheStatusRow> GetStatus()
            => inner.GetStatus();
    }

    [Fact]
    public async Task Diff_ComputeResults_Vs_StoredCostResults()
    {
        var idRaw = Environment.GetEnvironmentVariable("BIT_DIFF_ANALYSIS_ID");
        if (string.IsNullOrEmpty(idRaw)) return; // no-op fuera de la corrida de paridad
        var analysisId = int.Parse(idRaw);

        // Filtro opcional de servicios (BIT_DIFF_SERVICES="vms,disks"); null = todos.
        var svcRaw = Environment.GetEnvironmentVariable("BIT_DIFF_SERVICES");
        var serviceKeys = string.IsNullOrWhiteSpace(svcRaw)
            ? null
            : svcRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var config = AppConfig.FromConfiguration(new ConfigurationBuilder().Build());
        var factory = new SqlConnectionFactory(config);
        var constants = new PricingConstants(factory);
        using var http = new HttpClient();
        // Decorador SIN refresh: lee la cache de precios existente (los mismos precios que
        // usó el Python en su última corrida) y NO escribe ni llama a la Retail API. Así el
        // diff es no destructivo y determinista (compara LÓGICA, no cambios de precio de Azure).
        var repo = new SqlPriceRepository(
            new NoRefreshPriceCache(new SqlPriceCache(factory)), new RetailPriceClient(http), constants);
        var engine = new EngineCostEngine(
            new SqlServiceCatalog(factory), new SqlResourceLoader(factory),
            new SqlCostResultStore(factory), repo, constants, NullLogger<EngineCostEngine>.Instance);

        var computed = engine.ComputeResults(analysisId, serviceKeys);
        var stored = await LoadStored(factory, analysisId, serviceKeys);

        const double tol = 0.02; // tolerancia absoluta en USD/mes
        var mismatches = new List<string>();

        foreach (var c in computed)
        {
            var key = (c.ResourceId, c.ServiceKey);
            if (!stored.TryGetValue(key, out var s))
            {
                mismatches.Add($"[{c.ServiceKey}#{c.ResourceId}] .NET lo calculó pero no está en Python");
                continue;
            }
            if (!Close(c.PaygMonthly, s.Payg, tol))
                mismatches.Add($"[{c.ServiceKey}#{c.ResourceId}] payg .NET={c.PaygMonthly} py={s.Payg}");
            if (!Close(c.Ri1yMonthly, s.Ri1y, tol))
                mismatches.Add($"[{c.ServiceKey}#{c.ResourceId}] ri1y .NET={c.Ri1yMonthly} py={s.Ri1y}");
            if (!Close(c.Ri3yMonthly, s.Ri3y, tol))
                mismatches.Add($"[{c.ServiceKey}#{c.ResourceId}] ri3y .NET={c.Ri3yMonthly} py={s.Ri3y}");
            if (c.CalculationStatus != s.Status)
                mismatches.Add($"[{c.ServiceKey}#{c.ResourceId}] status .NET={c.CalculationStatus} py={s.Status}");
        }

        var computedKeys = computed.Select(c => (c.ResourceId, c.ServiceKey)).ToHashSet();
        foreach (var k in stored.Keys)
        {
            if (!computedKeys.Contains(k))
                mismatches.Add($"[{k.Item2}#{k.Item1}] Python lo tiene pero .NET no lo calculó");
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} discrepancias (de {computed.Count} .NET vs {stored.Count} Python):\n"
            + string.Join("\n", mismatches.Take(60)));
    }

    /// <summary>
    /// Validación del WRITE-PATH para el cutover de escrituras a prod. Sobre la BD AISLADA:
    /// (1) confirma que ComputeResults del .NET == lo que dejó FastAPI; (2) ejecuta
    /// CalculateAnalysis (PERSISTE, replaceExisting=true); (3) relee y verifica que lo
    /// ESCRITO por el .NET == lo que había FastAPI. Transitivamente: escritura .NET ==
    /// escritura FastAPI. Gateado por BIT_WRITE_PARITY_ANALYSIS_ID (+ SQL_DATABASE=aislada).
    /// OJO: SOBRESCRIBE los cost_results de ese análisis en la aislada (no toca prod).
    /// </summary>
    [Fact]
    public async Task WriteParity_CalculateAnalysis_PersisteIgualQueFastApi()
    {
        var idRaw = Environment.GetEnvironmentVariable("BIT_WRITE_PARITY_ANALYSIS_ID");
        if (string.IsNullOrEmpty(idRaw)) return; // no-op fuera de la corrida de write-parity
        var analysisId = int.Parse(idRaw);

        var svcRaw = Environment.GetEnvironmentVariable("BIT_DIFF_SERVICES");
        var serviceKeys = string.IsNullOrWhiteSpace(svcRaw)
            ? null
            : svcRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var config = AppConfig.FromConfiguration(new ConfigurationBuilder().Build());
        var factory = new SqlConnectionFactory(config);
        var constants = new PricingConstants(factory);
        using var http = new HttpClient();
        // BIT_WRITE_PARITY_FETCH=1 → cache REAL: trae precios faltantes de la Retail API (como
        // FastAPI en vivo; necesario para datos copiados de prod, cuya cache no tiene redis ni
        // todos los SKUs de VM). Sin la flag, cache-only (determinista; faltantes → manual_required).
        var fetchEnabled = Environment.GetEnvironmentVariable("BIT_WRITE_PARITY_FETCH") == "1";
        IPriceCache cache = fetchEnabled
            ? new SqlPriceCache(factory)
            : new NoRefreshPriceCache(new SqlPriceCache(factory));
        var repo = new SqlPriceRepository(cache, new RetailPriceClient(http), constants);
        var engine = new EngineCostEngine(
            new SqlServiceCatalog(factory), new SqlResourceLoader(factory),
            new SqlCostResultStore(factory), repo, constants, NullLogger<EngineCostEngine>.Instance);

        // 0. Referencia: lo que dejó FastAPI en la BD aislada.
        var before = await LoadStored(factory, analysisId, serviceKeys);
        Assert.True(before.Count > 0,
            $"No hay cost_results previos (FastAPI) para el análisis {analysisId} en la BD aislada; "
            + "no se puede validar write-parity (revisa SQL_DATABASE y que el análisis tenga datos).");

        const double tol = 0.02;
        var mismatches = new List<string>();

        // 1. Compute (sin persistir) debe coincidir con lo guardado por FastAPI.
        var computed = engine.ComputeResults(analysisId, serviceKeys);
        var computedMap = computed.ToDictionary(
            c => (c.ResourceId, c.ServiceKey),
            c => new Stored(c.PaygMonthly, c.Ri1yMonthly, c.Ri3yMonthly, c.CalculationStatus));
        foreach (var (key, s) in before)
        {
            if (!computedMap.TryGetValue(key, out var c))
            {
                mismatches.Add($"[compute] {key.Item2}#{key.Item1}: FastAPI lo tiene, .NET no lo computó");
                continue;
            }
            if (!Close(c.Payg, s.Payg, tol)) mismatches.Add($"[compute] {key.Item2}#{key.Item1} payg .NET={c.Payg} py={s.Payg}");
            if (!Close(c.Ri1y, s.Ri1y, tol)) mismatches.Add($"[compute] {key.Item2}#{key.Item1} ri1y .NET={c.Ri1y} py={s.Ri1y}");
            if (!Close(c.Ri3y, s.Ri3y, tol)) mismatches.Add($"[compute] {key.Item2}#{key.Item1} ri3y .NET={c.Ri3y} py={s.Ri3y}");
            if (c.Status != s.Status) mismatches.Add($"[compute] {key.Item2}#{key.Item1} status .NET={c.Status} py={s.Status}");
        }

        // 2. Persistir con el motor .NET (sobrescribe cost_results del análisis en la aislada).
        engine.CalculateAnalysis(analysisId, serviceKeys, replaceExisting: true);

        // 3. Releer: lo ESCRITO por el .NET debe ser idéntico a lo que había FastAPI.
        var after = await LoadStored(factory, analysisId, serviceKeys);
        if (after.Count != before.Count)
            mismatches.Add($"[write] conteo distinto: .NET escribió {after.Count} filas vs FastAPI {before.Count}");
        foreach (var (key, s) in before)
        {
            if (!after.TryGetValue(key, out var a))
            {
                mismatches.Add($"[write] {key.Item2}#{key.Item1}: FastAPI lo tenía, el .NET no lo escribió");
                continue;
            }
            if (!Close(a.Payg, s.Payg, tol)) mismatches.Add($"[write] {key.Item2}#{key.Item1} payg .NET={a.Payg} py={s.Payg}");
            if (!Close(a.Ri1y, s.Ri1y, tol)) mismatches.Add($"[write] {key.Item2}#{key.Item1} ri1y .NET={a.Ri1y} py={s.Ri1y}");
            if (!Close(a.Ri3y, s.Ri3y, tol)) mismatches.Add($"[write] {key.Item2}#{key.Item1} ri3y .NET={a.Ri3y} py={s.Ri3y}");
            if (a.Status != s.Status) mismatches.Add($"[write] {key.Item2}#{key.Item1} status .NET={a.Status} py={s.Status}");
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} discrepancias de write-parity (análisis {analysisId}, {before.Count} filas FastAPI):\n"
            + string.Join("\n", mismatches.Take(60)));
    }

    /// <summary>
    /// Validación del fallback IA (Fase B): construye el motor CON el asistente real (Azure
    /// OpenAI) y corre ComputeResults (NO persiste) sobre un análisis real. Verifica que NO
    /// queden price_not_found y que al menos un precio lo haya resuelto la IA (assist_match).
    /// Gateado por BIT_AI_ANALYSIS_ID (+ BIT_DIFF_SERVICES, + AZURE_OPENAI_* en el entorno).
    /// OJO: la cache se llena por fetch → correr contra una COPIA, no prod.
    /// </summary>
    [Fact]
    public void AiFallback_Resuelve_PreciosFaltantes()
    {
        var idRaw = Environment.GetEnvironmentVariable("BIT_AI_ANALYSIS_ID");
        if (string.IsNullOrEmpty(idRaw)) return; // no-op fuera de la corrida de validación IA
        var analysisId = int.Parse(idRaw);

        var svcRaw = Environment.GetEnvironmentVariable("BIT_DIFF_SERVICES");
        var serviceKeys = string.IsNullOrWhiteSpace(svcRaw)
            ? null
            : svcRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var config = AppConfig.FromConfiguration(new ConfigurationBuilder().Build());
        var factory = new SqlConnectionFactory(config);
        var constants = new PricingConstants(factory);
        using var http = new HttpClient();
        var assistant = new SqlPriceAssistant(
            config, new AzureOpenAiChatClient(http, config), new SqlPriceAssistantAudit(factory));
        var repo = new SqlPriceRepository(new SqlPriceCache(factory), new RetailPriceClient(http), constants, assistant);
        var engine = new EngineCostEngine(
            new SqlServiceCatalog(factory), new SqlResourceLoader(factory),
            new SqlCostResultStore(factory), repo, constants, NullLogger<EngineCostEngine>.Instance);

        Assert.True(assistant.IsEnabled, "El asistente IA no está habilitado (revisa AZURE_OPENAI_* en el entorno).");

        var computed = engine.ComputeResults(analysisId, serviceKeys);
        var notFound = computed.Where(c => c.CalculationStatus == "price_not_found").ToList();
        var aiResolved = computed.Where(c => (c.CalculationNotes ?? "").Contains("assist_match")).ToList();

        Assert.True(aiResolved.Count > 0,
            $"El asistente no resolvió ningún precio (se esperaba al menos 1). computados={computed.Count}, "
            + $"price_not_found={notFound.Count}");
        Assert.True(notFound.Count == 0,
            $"Quedan {notFound.Count} sin precio pese al asistente: "
            + string.Join(", ", notFound.Select(c => $"{c.ServiceKey}#{c.ResourceId}")));
    }

    private static bool Close(double? a, double? b, double tol)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return Math.Abs(a.Value - b.Value) <= tol;
    }

    private static async Task<Dictionary<(int, string), Stored>> LoadStored(
        ISqlConnectionFactory factory, int analysisId, string[]? serviceKeys)
    {
        var map = new Dictionary<(int, string), Stored>();
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        var svcFilter = serviceKeys is { Length: > 0 }
            ? " AND service_key IN (" + string.Join(",", serviceKeys.Select((_, i) => $"@s{i}")) + ")"
            : "";
        cmd.CommandText = $"""
            SELECT resource_id, service_key, payg_monthly, ri_1y_monthly, ri_3y_monthly, calculation_status
            FROM dbo.cost_results WHERE analysis_id = @a{svcFilter}
            """;
        cmd.Parameters.Add(new SqlParameter("@a", analysisId));
        if (serviceKeys is { Length: > 0 })
        {
            for (var i = 0; i < serviceKeys.Length; i++)
                cmd.Parameters.Add(new SqlParameter($"@s{i}", serviceKeys[i]));
        }
        await using var reader = await cmd.ExecuteReaderAsync();
        double? D(int i) => reader.IsDBNull(i) ? null : Convert.ToDouble(reader.GetValue(i));
        while (await reader.ReadAsync())
        {
            var key = (reader.GetInt32(0), reader.GetString(1));
            map[key] = new Stored(D(2), D(3), D(4), reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        return map;
    }
}
