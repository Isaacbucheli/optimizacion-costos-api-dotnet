namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// DTOs de la PISTA E (Excel) del módulo WAF. Port fiel de los dataclasses/dicts de
/// app/waf/excel_exporter.py y app/waf/excel_importer.py + los Pydantic de la ruta
/// app/routes/waf.py (WafExcelApplyItem / respuesta de apply). Records inmutables.
///
/// Convención de fechas: el Python maneja las fechas del Excel como strings ISO
/// "yyyy-MM-dd" durante el parse/preview (parse_waf_matrix_excel devuelve isoformat()).
/// Aquí se conservan como string? para no perder la tolerancia de formatos del parser.
/// </summary>

/// <summary>
/// Fila de recomendación que alimenta la EXPORTACIÓN (port del dict que arma la ruta
/// export_waf_client_excel + lo que lee export_waf_excel). Todos los campos textuales son los
/// que el Excel rellena; <see cref="LastSeenAt"/>/<see cref="RemediationStartDate"/> se formatean
/// a "yyyy-MM-dd". <see cref="Resources"/> son los nombres de findings activos.
/// </summary>
public sealed record WafExportRow(
    int? CanonicalId,
    int? PillarNumber,
    string? ReviewScopeEs,
    string? BenefitEs,
    string? ClientActionEs,
    string? BitActionEs,
    IReadOnlyList<string>? Resources,
    int? ResourceCount,
    int? CompletionPct,
    DateTime? LastSeenAt,
    string? BusinessImpact,
    int? ImpactNumber,
    int? PriorityOverride,
    DateTime? RemediationStartDate,
    string? ProjectedBitEffort,
    string? ExecutionLog);

/// <summary>
/// Fila parseada desde el Excel cargado (port 1:1 del dataclass WafExcelRow de excel_importer.py).
/// Mismos nombres de campo (snake→PascalCase). Listas no-null por defecto (default_factory=list).
/// </summary>
public sealed record WafExcelRow(
    int RowNumber,
    int? PillarNumber,
    string ExcelCode,
    string Title,
    string RawScope,
    string? ReviewDate,
    string OptimizationStatus,
    string ResourceSummary,
    string Benefit,
    string Actions,
    string Impact,
    string Priority,
    string? RemediationStartDate,
    string ProjectedBitEffort,
    int? CompletionPct,
    string ExecutionLog,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> Warnings);

/// <summary>Metadata del parse (port del 2º elemento de la tupla de parse_waf_matrix_excel).
/// RowsTotal cuenta las filas ACEPTADAS (contrato histórico del preview); RowsSkipped cuenta
/// las filas con contenido descartadas después del encabezado — o toda la hoja si el encabezado
/// nunca apareció (HeaderFound=false). Las filas de título previas al encabezado y las secciones
/// de pilar son parte del formato y no cuentan como descartadas (UPL-01 del DAST).</summary>
public sealed record WafExcelParseMetadata(
    string Sheet,
    int RowsTotal,
    int RowsWithWarnings,
    bool HeaderFound,
    int RowsSkipped,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Candidato puntuado para el match Excel↔catálogo (port del dataclass WafExcelCandidate de
/// excel_importer.py — el to_dict() de cada item del array "candidates" del preview).
/// </summary>
public sealed record WafExcelMatchCandidate(
    int CanonicalId,
    string MatrixCode,
    int PillarNumber,
    string ReviewScopeEs,
    string AdvisorName,
    int CurrentCompletionPct,
    string? CurrentRemediationStartDate,
    string CurrentExecutionLog,
    double Score,
    string Reason);

/// <summary>
/// Sugerencia de match de una fila (port del dict "suggested_match" del preview). Null cuando no
/// hubo match confiable. Se distingue de <see cref="WafExcelMatchCandidate"/> porque sus
/// "current_*" salen del candidato elegido en el catálogo.
/// </summary>
public sealed record WafExcelSuggestedMatch(
    int CanonicalId,
    string MatrixCode,
    int PillarNumber,
    string ReviewScopeEs,
    string AdvisorName,
    int CurrentCompletionPct,
    string? CurrentRemediationStartDate,
    string CurrentExecutionLog);

/// <summary>
/// Item de vista previa por fila (port 1:1 del dict que appendea preview_excel_rows).
/// <see cref="Status"/> ∈ {"matched","needs_review","new"}.
/// <see cref="MatchSource"/> ∈ {"deterministic","azure_openai",null}.
/// <see cref="TrackingUpdates"/> son los cambios sugeridos de seguimiento (solo claves presentes).
/// </summary>
public sealed record WafExcelPreviewItem(
    WafExcelRow Row,
    string Status,
    bool CanCreate,
    string? MatchSource,
    double Confidence,
    string Reason,
    WafExcelSuggestedMatch? SuggestedMatch,
    IReadOnlyList<WafExcelMatchCandidate> Candidates,
    IReadOnlyDictionary<string, object?> TrackingUpdates);

/// <summary>
/// Item de aplicación de la importación (port 1:1 del Pydantic WafExcelApplyItem de waf.py).
/// <see cref="Action"/> ∈ {"update","create"}. La validación cruzada (canonical_id requerido en
/// update; pillar_number+title en create) la replica el controller; aquí se modela tal cual.
/// </summary>
public sealed record WafExcelApplyItem(
    int RowNumber,
    string Action,
    int? CanonicalId,
    bool Approved,
    int? CompletionPct,
    DateOnly? RemediationStartDate,
    string? ExecutionLog,
    // Campos para action="create" (provienen de la fila del Excel).
    int? PillarNumber,
    string? Title,
    string? ReviewScope,
    string? Benefit,
    string? Actions,
    string? Impact,
    string? ProjectedBitEffort,
    IReadOnlyList<string>? Resources)
{
    /// <summary>action por defecto = "update" (paridad con el default del Pydantic).</summary>
    public WafExcelApplyItem() : this(
        RowNumber: 0, Action: "update", CanonicalId: null, Approved: true, CompletionPct: null,
        RemediationStartDate: null, ExecutionLog: null, PillarNumber: null, Title: null,
        ReviewScope: null, Benefit: null, Actions: null, Impact: null,
        ProjectedBitEffort: null, Resources: null)
    { }
}

/// <summary>Error por fila en el apply (port del dict {"row_number","detail"}).</summary>
public sealed record WafExcelApplyError(int RowNumber, string Detail);

/// <summary>
/// Resultado del apply (port 1:1 del dict que devuelve apply_waf_excel_import en waf.py).
/// <see cref="ChangedFields"/> = nombre de campo de tracking → nº de filas que lo cambiaron.
/// </summary>
public sealed record WafExcelApplyResult(
    int RowsApplied,
    int RowsCreated,
    int RowsSkipped,
    IReadOnlyDictionary<string, int> ChangedFields,
    IReadOnlyList<WafExcelApplyError> Errors);
