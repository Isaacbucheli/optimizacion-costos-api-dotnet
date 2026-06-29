namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Importación asistida de matrices WAF históricas en Excel. Port de
/// app/waf/excel_importer.py (parse/score/match/preview) + la lógica de apply de la ruta
/// app/routes/waf.py::apply_waf_excel_import (create_recommendation_from_matrix +
/// refresh_manual_canonical_text + apply_tracking_update + renumber_matrix_codes).
/// </summary>
public interface IWafExcelImporter
{
    /// <summary>
    /// Parsea los bytes del .xlsx cargado y extrae las filas de la matriz WAF, tolerando formatos
    /// variados (detección flexible de encabezado, 4 formatos de fecha, alias de % completitud).
    /// Port de parse_waf_matrix_excel. Lanza <see cref="InvalidOperationException"/> si falta la hoja
    /// "Resultados" o se supera el límite de filas.
    /// </summary>
    (IReadOnlyList<WafExcelRow> Rows, WafExcelParseMetadata Metadata) Parse(byte[] content);

    /// <summary>
    /// Genera la vista previa de matching + sugerencias por fila ANTES de aplicar. Combina scoring
    /// determinista (WafText) y, opcionalmente, confirmación con Azure OpenAI (presupuesto
    /// <paramref name="maxAiCalls"/>). Port de preview_excel_rows + _fetch_excel_match_candidates.
    /// </summary>
    Task<IReadOnlyList<WafExcelPreviewItem>> PreviewAsync(
        int clientId, IReadOnlyList<WafExcelRow> rows, bool useAi, int maxAiCalls, CancellationToken ct = default);

    /// <summary>
    /// Aplica los cambios aprobados del preview (crear recomendación nueva o actualizar seguimiento)
    /// en una sola transacción y renumera matrix_codes al final. Port 1:1 de apply_waf_excel_import.
    /// </summary>
    Task<WafExcelApplyResult> ApplyAsync(
        int clientId, IReadOnlyList<WafExcelApplyItem> items, string? actor, CancellationToken ct = default);
}
