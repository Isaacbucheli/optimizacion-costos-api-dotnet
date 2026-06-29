using System.Text.Json;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Exportador Word del informe mensual de gestión. Port fiel 1:1 de
/// app/reports/word_export.py (que usa python-docx). Toma el JSON del informe (el mismo que arma
/// builder.py y consume el frontend) y rellena la plantilla corporativa
/// Features/Reports/Templates/PLANTILLA-INF-CDC-BIT-v1.docx con DocumentFormat.OpenXml
/// (equivalente a python-docx):
///
/// - Portada: cliente, período y fecha de elaboración.
/// - Tablas de datos: servidores, plantillas, estado de recursos, App Services, conectividad,
///   respaldos, performance, SQL MI, alertas del período, disponibilidad y caídas.
/// - Conclusiones / recomendaciones desde la narrativa (si existe).
/// - Limpieza: página interna de instrucciones, recuadros "Guía:" y secciones opcionales sin datos
///   se eliminan del documento final.
/// </summary>
public interface IReportWordExporter
{
    /// <summary>
    /// Genera el .docx del informe a partir del JSON almacenado (port de build_word_report).
    /// Devuelve los bytes del documento.
    /// </summary>
    byte[] BuildWordReport(JsonElement report);

    /// <summary>
    /// [CLIENTE]-INF-CDC-[MES]-[AAAA]-v1.docx (convención de la plantilla; port de word_filename).
    /// </summary>
    string WordFilename(JsonElement report);
}
