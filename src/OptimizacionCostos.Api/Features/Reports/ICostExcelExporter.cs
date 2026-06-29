namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Exportación a XLSX de los COSTOS de un análisis. Port fiel de app/excel_exporter.py
/// (función build_analysis_excel). Lee cost_results + azure_resources (y tablas *_details) del
/// análisis y rellena una plantilla .xlsx por servicio (VMs, Discos, SQL, App Service, MySQL,
/// SQL MI, Cosmos, Redis, IP Pública) + Resumen Ejecutivo + Detalle Recursos + Servicios no
/// catalogados. NO recalcula costos: usa lo guardado en cost_results.
///
/// La plantilla se descarga del contenedor de Blob "templates" (archivo
/// AppConfig.TemplateFileName, p.ej. "BANISI-Optimizacion_Costos_V2.xlsx"), igual que en el
/// route Python (app/routes/excel.py descarga la plantilla y se la pasa al exporter).
/// </summary>
public interface ICostExcelExporter
{
    /// <summary>
    /// Genera el .xlsx de costos para <paramref name="analysisId"/> y devuelve sus bytes.
    /// Equivalente a build_analysis_excel(analysis_id, template_bytes); aquí el exporter mismo
    /// obtiene la plantilla del Blob (el Python lo hacía el route antes de llamar).
    /// </summary>
    Task<byte[]> GenerateAsync(int analysisId, CancellationToken ct);
}
