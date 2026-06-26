namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Interfaz de una calculadora de costo de un servicio. Port de
/// app/calculators/base.py::BaseCalculator.calculate(resources, analysis_id).
///
/// Cada calculadora procesa los recursos de UN servicio y produce un
/// <see cref="CostResult"/> por recurso (o ninguno, si filtra recursos — ej. discos
/// adjuntos a VMs encendidas, IPs no huérfanas, o filas de otro tier).
/// </summary>
public interface ICostCalculator
{
    /// <summary>
    /// Calcula costos para la lista de recursos del análisis dado.
    /// </summary>
    /// <param name="resources">
    /// Filas de recurso (datos JOIN-eados de azure_resources + tabla detalle).
    /// </param>
    /// <param name="analysisId">Id del análisis al que pertenecen los resultados.</param>
    /// <returns>Lista de resultados (no necesariamente 1:1 con los recursos de entrada).</returns>
    IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId);
}
