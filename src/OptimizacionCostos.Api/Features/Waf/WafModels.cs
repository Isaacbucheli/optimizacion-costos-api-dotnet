namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Records "forma de tabla" del módulo WAF (port de las filas de app/waf/schema.py). Inmutables.
/// Los DTOs de request/response de la API van en Api/WafApiDtos.cs.
/// </summary>

// Catálogo central (1 fila por advisor_name+advisor_category)
public sealed record WafCanonical(
    int CanonicalId, string AdvisorName, string AdvisorCategory, byte PillarNumber,
    string ReviewScopeEs, string BenefitEs, string ClientActionEs, string BitActionEs,
    bool IsExcluded, string? ExclusionReason, int? ConsolidatesToId,
    string? AiReviewStatus, string? AiDecision, decimal? AiConfidence,
    bool? AiPossibleAdditionalCost, string? AiCostReason, string? AiExclusionReason,
    string? AiDuplicateGroupKey, string? AiRawModelText, string? AiReviewedBy, DateTime? AiReviewedAt,
    DateTime CreatedAt, DateTime UpdatedAt,
    // Título tal cual lo devolvió Azure Advisor (NULL = no hay original conocido). Lo siembra solo la
    // ingesta con source='advisor'; advisor_name NO sirve para esto porque es la firma de dedup y en
    // las canónicas de Excel/legacy trae el título en español de la matriz BIT.
    string? AdvisorNameEn = null);

// Vínculo cliente↔canónica
public sealed record WafRecommendation(
    int RecommendationId, int ClientId, int CanonicalId, string MatrixCode,
    string BusinessImpact, byte ImpactNumber, int ResourceCount,
    DateTime FirstSeenAt, DateTime LastSeenAt, bool IsActive, bool IsDismissed,
    DateTime? DismissedAt, string? DismissedBy, string? DismissalReason);

// Hallazgo de recurso Azure
public sealed record WafResourceFinding(
    long FindingId, int RecommendationId, string ResourceName, string ResourceType,
    string ResourceGroup, string SubscriptionId, string SubscriptionName, string? AzureResourceId,
    string BusinessImpact, string? AdditionalInfo, DateTime FirstSeenAt, DateTime LastSeenAt,
    string Status, DateTime? ResolvedAt);

// Tracking (PK compuesta client_id+canonical_id)
public sealed record WafTracking(
    int CompletionPct, DateOnly? RemediationStartDate, DateOnly? RemediationEndDate, string? ProjectedBitEffort,
    string? ExecutionLog, byte? PriorityOverride, string? InternalNotes,
    DateTime UpdatedAt, string? UpdatedBy);

public sealed record WafTrackingHistory(
    long HistoryId, int ClientId, int CanonicalId, string FieldChanged,
    string? OldValue, string? NewValue, string? ChangedBy, DateTime ChangedAt);

public sealed record WafComment(
    long CommentId, int ClientId, int CanonicalId, string UserDisplay,
    string CommentText, DateTime CreatedAt);

public sealed record WafIngestionRun(
    int RunId, int ClientId, string SourceFileName, DateOnly? CsvExportDate,
    int? RowsTotal, int? RowsProcessed, int? NewRecommendations, int? NewFindings, int? ResolvedFindings,
    string? UnmappedRecommendations, string Status, string? ErrorMessage,
    DateTime StartedAt, DateTime? CompletedAt, string? CreatedBy);

public sealed record WafAdvisorScoreSnapshot(
    int SnapshotId, int ClientId, DateOnly SnapshotDate, DateTime CapturedAt, string Status,
    bool HasConnection, int SubscriptionsScored,
    decimal? ScoreP1, decimal? ScoreP2, decimal? ScoreP3, decimal? ScoreP4, decimal? ScoreP5,
    string? WarningsJson, string? BreakdownJson, string Source, bool IncludeInReports);

public sealed record WafCanonicalAlias(
    int AliasId, string AdvisorName, string AdvisorCategory, int CanonicalId,
    string? MatchSource, decimal? Confidence, string? CreatedBy, DateTime CreatedAt);

// Lock distribuido (para el scheduler de advisor-score)
public sealed record AppJobLock(string JobName, DateTime LockedUntil, string? Owner, DateTime UpdatedAt);

// -------------------- tipos de proceso (no son tablas) --------------------

/// <summary>Fila de Advisor (CSV o API) lista para ingesta.</summary>
public sealed record AdvisorRow(
    string AdvisorName, string AdvisorCategory, string BusinessImpact, string ResourceName,
    string ResourceType, string ResourceGroup, string SubscriptionId, string SubscriptionName,
    string AzureResourceId, string? AdditionalInfo,
    // GUID (lower) del tipo de recomendación ARM. Solo sync Advisor; el CSV/Excel lo deja null.
    string? RecommendationTypeId = null);

/// <summary>Candidato unificado para dedup (ingesta) y consolidación (post-curación).</summary>
public sealed record WafCandidate(
    int CanonicalId, int? RecommendationId, string? MatrixCode, byte PillarNumber,
    string AdvisorName, string AdvisorCategory, string ReviewScopeEs,
    string? BenefitEs, string? ClientActionEs, string? BitActionEs,
    string? ResourcesText, int? CompletionPct, DateOnly? RemediationStartDate, string? ExecutionLog,
    // recommendationTypeIds ARM (lower) presentes en los findings. Vacío/null = origen sin typeId
    // (CSV/manual): la guarda anti-fusión no aplica y el comportamiento es el histórico.
    IReadOnlyList<string>? TypeIds = null);

public sealed record WafCurationResult(
    string Decision, bool PossibleAdditionalCost, string CostReason, string DuplicateGroupKey,
    byte PillarNumber, string ReviewScopeEs, string BenefitEs, string ClientActionEs,
    string BitActionEs, string ExclusionReason, decimal Confidence, string RawModelText);

public sealed record WafDupConfirmation(int CanonicalId, decimal Confidence, string Reason);

public sealed record WafMergePlan(WafCandidate Primary, WafCandidate Duplicate, double Ratio, string Source);

public sealed record AdvisorScoreData(decimal Score, decimal Weight); // por pilar
