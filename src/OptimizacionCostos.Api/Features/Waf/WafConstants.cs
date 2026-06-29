namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Constantes únicas del módulo WAF (port de los valores dispersos en app/waf/*.py). Una sola
/// fuente para thresholds de dedup/consolidación, stopwords, mapeos categoría→pilar e impacto,
/// y api-versions. NO duplicar estos valores en otros archivos.
/// </summary>
public static class WafConstants
{
    // Dedup en ingesta (dedup.py)
    public const double MinDeterministicScore = 0.08; // umbral bajo a propósito (multiidioma)
    public const double MinAiConfidence = 0.80;        // auto-merge IA solo con confianza alta
    public const int MaxCandidatesToAi = 5;

    // Consolidación post-curación (consolidate.py)
    public const double AutoMergeRatio = 0.93;     // fusión determinista mismo pilar
    public const double AiTitleRatio = 0.55;       // dispara confirmación IA
    public const double AiResourceOverlap = 0.50;
    public const double AiTokenJaccard = 0.30;

    /// <summary>Subscription_id sentinela de hallazgos cargados a mano (Excel). Nunca se resuelven.</summary>
    public const string ManualSubscriptionId = "importado";

    /// <summary>Stopwords para tokenización de dedup (dedup.py STOPWORDS).</summary>
    public static readonly IReadOnlySet<string> Stopwords = new HashSet<string>(StringComparer.Ordinal)
    {
        "a", "al", "and", "azure", "con", "de", "del", "el", "en", "for", "la", "las", "los",
        "of", "para", "por", "recomendacion", "recommendation", "recursos", "resource", "the", "to", "y",
    };

    /// <summary>
    /// categoría normalizada (NFKD ascii, lower, sin espacios) → pilar WAF. Default 2 (Operacional).
    /// Port de CATEGORY_TO_PILLAR de ingestion.py.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, byte> CategoryToPillar = new Dictionary<string, byte>(StringComparer.Ordinal)
    {
        ["performance"] = 1,
        ["operationalexcellence"] = 2,
        ["security"] = 3,
        ["reliability"] = 4,
        ["highavailability"] = 4,
        ["cost"] = 5,
    };
    public const byte DefaultPillar = 2;

    /// <summary>Etiqueta legible por pilar (para secciones/UI).</summary>
    public static readonly IReadOnlyDictionary<byte, string> PillarLabels = new Dictionary<byte, string>
    {
        [1] = "Eficiencia de rendimiento",
        [2] = "Excelencia operativa",
        [3] = "Seguridad",
        [4] = "Confiabilidad",
        [5] = "Optimización de costos",
    };

    /// <summary>business_impact (lower) → impact_number (para orden). Default 2 (Medium).</summary>
    public static byte ImpactNumber(string? businessImpact) =>
        (businessImpact ?? "").Trim().ToLowerInvariant() switch { "high" => 1, "medium" => 2, "low" => 3, _ => 2 };

    /// <summary>Columnas del CSV de Advisor que NO van a additional_info (ya tienen columna propia).</summary>
    public static readonly IReadOnlySet<string> IgnoredCsvColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Recommendation", "Category", "Business Impact", "Resource Name", "Type",
        "Resource Group", "Subscription ID", "Subscription Name",
    };

    // API versions (ARM / Azure OpenAI). Port de los valores del runbook/advisor.
    public const string AdvisorScoreApiVersion = "2023-01-01";
    public const string AdvisorGenerateApiVersion = "2025-01-01";
}
