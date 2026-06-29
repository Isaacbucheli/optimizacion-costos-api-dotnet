namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Exportación de la Matriz Optimización y Mejoras a XLSX. Port de
/// app/waf/excel_exporter.py::export_waf_excel. Reutiliza la plantilla
/// BIT_Matriz_Template_V1_2.xlsx (preserva imágenes, comentarios enriquecidos, pageSetup y
/// extensiones) y devuelve los bytes del .xlsx generado.
/// </summary>
public interface IWafExcelExporter
{
    /// <summary>
    /// Genera el .xlsx de la matriz para un cliente. <paramref name="clientId"/> es solo
    /// contextual (el Python no lo usa dentro del exporter; el nombre de archivo lo decide el
    /// controller). <paramref name="recommendations"/> ya viene resuelto (canónica + tracking +
    /// findings). Devuelve el contenido binario del archivo.
    /// </summary>
    Task<byte[]> ExportAsync(
        int clientId, IReadOnlyList<WafExportRow> recommendations, CancellationToken ct = default);
}
