namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Valores parciales para actualizar el seguimiento de una recomendación WAF
/// (port de los <c>values: dict[str, Any]</c> de <c>apply_tracking_update</c> en app/waf/tracking.py).
///
/// Semántica "exclude_unset" de Pydantic: solo los campos efectivamente enviados por el
/// cliente participan del merge y del registro en <c>waf_tracking_history</c>. Como en C# un
/// <c>null</c> es ambiguo entre "no enviado" y "enviado como NULL", cada campo lleva un flag
/// <c>*Set</c> que el bindeo del controller debe poner en <c>true</c> únicamente cuando el campo
/// vino en el JSON (igual que <c>values.items()</c> en Python: solo itera lo presente).
///
/// Reglas portadas 1:1 del Python:
/// - Solo los campos en TRACKING_FIELDS se consideran (los demás se ignoran).
/// - Un campo presente con valor <c>null</c> SÍ sobreescribe a NULL (paridad con <c>{**current, **clean}</c>).
/// - Solo se registra historia cuando el valor realmente cambió (<c>current[field] != new_value</c>).
/// </summary>
public sealed class WafTrackingUpdate
{
    private int? _completionPct;
    private DateOnly? _remediationStartDate;
    private string? _projectedBitEffort;
    private string? _executionLog;
    private byte? _priorityOverride;
    private string? _internalNotes;

    /// <summary>completion_pct (0-100). Ver <see cref="CompletionPctSet"/>.</summary>
    public int? CompletionPct
    {
        get => _completionPct;
        set { _completionPct = value; CompletionPctSet = true; }
    }

    /// <summary>remediation_start_date (DATE). Ver <see cref="RemediationStartDateSet"/>.</summary>
    public DateOnly? RemediationStartDate
    {
        get => _remediationStartDate;
        set { _remediationStartDate = value; RemediationStartDateSet = true; }
    }

    /// <summary>projected_bit_effort (texto libre). Ver <see cref="ProjectedBitEffortSet"/>.</summary>
    public string? ProjectedBitEffort
    {
        get => _projectedBitEffort;
        set { _projectedBitEffort = value; ProjectedBitEffortSet = true; }
    }

    /// <summary>execution_log (texto libre). Ver <see cref="ExecutionLogSet"/>.</summary>
    public string? ExecutionLog
    {
        get => _executionLog;
        set { _executionLog = value; ExecutionLogSet = true; }
    }

    /// <summary>priority_override (1-3 o NULL). Ver <see cref="PriorityOverrideSet"/>.</summary>
    public byte? PriorityOverride
    {
        get => _priorityOverride;
        set { _priorityOverride = value; PriorityOverrideSet = true; }
    }

    /// <summary>internal_notes (texto libre). Ver <see cref="InternalNotesSet"/>.</summary>
    public string? InternalNotes
    {
        get => _internalNotes;
        set { _internalNotes = value; InternalNotesSet = true; }
    }

    // Flags de presencia (true solo si el campo vino en el JSON). No se serializan.
    [System.Text.Json.Serialization.JsonIgnore] public bool CompletionPctSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool RemediationStartDateSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ProjectedBitEffortSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ExecutionLogSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool PriorityOverrideSet { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool InternalNotesSet { get; private set; }
}
