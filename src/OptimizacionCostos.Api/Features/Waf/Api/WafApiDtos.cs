using System.ComponentModel.DataAnnotations;

namespace OptimizacionCostos.Api.Features.Waf.Api;

/// <summary>
/// FASE 4 — Records de request de los endpoints WAF (port de los BaseModel de app/routes/waf.py).
/// El binder de System.Text.Json mapea snake_case del JSON a estas propiedades. Las validaciones
/// de rango/longitud replican los Field(...) de Pydantic; las cruzadas las hace el controller.
/// (WafTrackingUpdate ya vive en WafTrackingDtos.cs — se reutiliza tal cual.)
///
/// IMPORTANTE (.NET 8): en records, los atributos de validación DataAnnotations van sobre el
/// PARÁMETRO del constructor primario (sin target), NUNCA con target de propiedad. Con el target
/// de propiedad, ASP.NET Core lanza InvalidOperationException en el model-binding ("validation
/// metadata ... will be ignored ... must be associated with the constructor parameter") en CADA
/// request con body → el navegador ve "Failed to fetch". (Bug de 2026-07-02, ver commit de fix.)
/// </summary>

/// <summary>Crear comentario. Port de WafCommentCreate (min 1, max 4000).</summary>
public sealed record WafCommentCreate(
    [Required, StringLength(4000, MinimumLength = 1)] string CommentText);

/// <summary>Descartar recomendación. Port de WafRecommendationDismiss (reason opcional, max 1000).</summary>
public sealed record WafRecommendationDismiss(
    [StringLength(1000)] string? Reason = null);

/// <summary>
/// Edición directa del catálogo (admin). Port de WafCanonicalUpdate. Todos opcionales: solo los
/// presentes en el JSON participan del UPDATE (semántica exclude_unset). El controller distingue
/// "no enviado" con flags *Set como en WafTrackingUpdate.
/// </summary>
public sealed class WafCanonicalUpdateRequest
{
    private int? _pillarNumber;
    private string? _reviewScopeEs;
    private string? _benefitEs;
    private string? _clientActionEs;
    private string? _bitActionEs;
    private bool? _isExcluded;
    private string? _exclusionReason;
    private string? _aiReviewStatus;

    [Range(1, 5)] public int? PillarNumber { get => _pillarNumber; set { _pillarNumber = value; PillarNumberSet = true; } }
    [StringLength(1000, MinimumLength = 3)] public string? ReviewScopeEs { get => _reviewScopeEs; set { _reviewScopeEs = value; ReviewScopeEsSet = true; } }
    [StringLength(2000, MinimumLength = 3)] public string? BenefitEs { get => _benefitEs; set { _benefitEs = value; BenefitEsSet = true; } }
    [StringLength(2000, MinimumLength = 3)] public string? ClientActionEs { get => _clientActionEs; set { _clientActionEs = value; ClientActionEsSet = true; } }
    [StringLength(2000, MinimumLength = 3)] public string? BitActionEs { get => _bitActionEs; set { _bitActionEs = value; BitActionEsSet = true; } }
    public bool? IsExcluded { get => _isExcluded; set { _isExcluded = value; IsExcludedSet = true; } }
    [StringLength(1000)] public string? ExclusionReason { get => _exclusionReason; set { _exclusionReason = value; ExclusionReasonSet = true; } }
    [StringLength(30)] public string? AiReviewStatus { get => _aiReviewStatus; set { _aiReviewStatus = value; AiReviewStatusSet = true; } }

    [System.Text.Json.Serialization.JsonIgnore] public bool PillarNumberSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ReviewScopeEsSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool BenefitEsSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ClientActionEsSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool BitActionEsSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsExcludedSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ExclusionReasonSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool AiReviewStatusSet { get; private set; }

    /// <summary>True si llegó al menos un campo (paridad con "if not values").</summary>
    public bool AnySet => PillarNumberSet || ReviewScopeEsSet || BenefitEsSet || ClientActionEsSet
        || BitActionEsSet || IsExcludedSet || ExclusionReasonSet || AiReviewStatusSet;
}

/// <summary>Análisis IA por lotes (admin). Port de WafAiBatchRequest (limit 1-200 default 50).</summary>
public sealed record WafAiBatchRequest(
    [Range(1, 200)] int Limit = 50,
    bool Apply = false);

/// <summary>Sync con Azure Advisor. Port de WafAdvisorSyncRequest (timeout 60-1800 default 600).</summary>
public sealed record WafAdvisorSyncRequest(
    List<string>? Subscriptions = null,
    [Range(60, 1800)] int TimeoutSecondsPerSubscription = 600);

/// <summary>Refresh manual de Advisor Score (admin). Port de WafAdvisorScoreRefreshRequest.</summary>
public sealed record WafAdvisorScoreRefreshRequest(
    [Range(1, int.MaxValue)] int? ClientId = null,
    bool IncludeInReports = false);

/// <summary>
/// Aplicar sugerencia IA (admin PATCH). Port de WafAiSuggestion. La validación cruzada
/// (decision=exclude ⇒ exclusion_reason) la replica el controller.
/// </summary>
public sealed record WafAiSuggestionRequest(
    [Required, RegularExpression("^(include|exclude|review)$")] string Decision,
    bool PossibleAdditionalCost,
    [StringLength(1000)] string CostReason,
    [StringLength(200)] string DuplicateGroupKey,
    [Range(1, 5)] int PillarNumber,
    [Required, StringLength(1000, MinimumLength = 3)] string ReviewScopeEs,
    [Required, StringLength(2000, MinimumLength = 3)] string BenefitEs,
    [Required, StringLength(2000, MinimumLength = 3)] string ClientActionEs,
    [Required, StringLength(2000, MinimumLength = 3)] string BitActionEs,
    [StringLength(1000)] string ExclusionReason,
    [Range(0.0, 1.0)] double Confidence,
    [StringLength(4000)] string? RawModelText = null)
{
    public WafAiSuggestionRequest() : this(
        Decision: "review", PossibleAdditionalCost: false, CostReason: "", DuplicateGroupKey: "",
        PillarNumber: 2, ReviewScopeEs: "", BenefitEs: "", ClientActionEs: "", BitActionEs: "",
        ExclusionReason: "", Confidence: 0.0, RawModelText: null)
    { }

    /// <summary>Convierte el request a WafCurationResult para persistir con IWafCatalogStore.</summary>
    public WafCurationResult ToCurationResult() => new(
        Decision: Decision,
        PossibleAdditionalCost: PossibleAdditionalCost,
        CostReason: CostReason ?? "",
        DuplicateGroupKey: DuplicateGroupKey ?? "",
        PillarNumber: (byte)PillarNumber,
        ReviewScopeEs: ReviewScopeEs,
        BenefitEs: BenefitEs,
        ClientActionEs: ClientActionEs,
        BitActionEs: BitActionEs,
        ExclusionReason: ExclusionReason ?? "",
        Confidence: (decimal)Confidence,
        RawModelText: RawModelText ?? "");
}

/// <summary>
/// Item de aplicación de import Excel (request). Port del Pydantic WafExcelApplyItem de waf.py.
/// Se distingue del WafExcelApplyItem de dominio (WafExcelDtos.cs) que usa DateOnly; aquí la fecha
/// llega como ISO string del JSON. El controller convierte a WafExcelApplyItem de dominio.
/// </summary>
public sealed record WafExcelApplyItemRequest(
    [Range(1, int.MaxValue)] int RowNumber,
    [RegularExpression("^(update|create)$")] string Action = "update",
    [Range(1, int.MaxValue)] int? CanonicalId = null,
    bool Approved = true,
    [Range(0, 100)] int? CompletionPct = null,
    DateOnly? RemediationStartDate = null,
    string? ExecutionLog = null,
    [Range(1, 5)] int? PillarNumber = null,
    [StringLength(500)] string? Title = null,
    [StringLength(2000)] string? ReviewScope = null,
    [StringLength(2000)] string? Benefit = null,
    [StringLength(2000)] string? Actions = null,
    [StringLength(200)] string? Impact = null,
    [StringLength(500)] string? ProjectedBitEffort = null,
    List<string>? Resources = null);

/// <summary>Body de apply Excel. Port de WafExcelApplyRequest (rows).</summary>
public sealed record WafExcelApplyRequest(
    List<WafExcelApplyItemRequest> Rows);

/// <summary>Request de POST /waf/translate. Sin DataAnnotations (regresión 2026-07-02).</summary>
public sealed record WafTranslateItemDto(string Key, string Text);
public sealed record WafTranslateRequest(string Target, List<WafTranslateItemDto> Items);

/// <summary>Request de PUT /waf/clients/{id}/security-management. Sin DataAnnotations (regresión 2026-07-02).</summary>
public sealed record WafSecurityManagementRequest(bool ManagedExternally, string? Note);
