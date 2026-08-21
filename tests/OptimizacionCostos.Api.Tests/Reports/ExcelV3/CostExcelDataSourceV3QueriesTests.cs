using OptimizacionCostos.Api.Features.Reports.ExcelV3;

namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;

/// <summary>
/// Guarda sobre el texto de <see cref="CostExcelDataSourceV3.Queries"/>.
///
/// Contexto (bug reportado el 2026-08-21): en la hoja "Optimización VMs" cada VM con SQL Server
/// salía repetida y el SUBTOTAL de la fila TOTAL quedaba inflado en la misma proporción. La causa
/// era el JOIN a sql_vm_details por vm_azure_resource_id (el id ARM de la VM, que se repite en TODOS
/// los análisis del cliente) sin acotar por análisis: con dos análisis duplicaba, con tres
/// triplicaba. El motor de costos nunca estuvo mal — SqlResourceLoader.LoadSqlVmInfo sí filtra por
/// analysis_id — así que el Resumen Ejecutivo, los escenarios y la interfaz web siempre mostraron el
/// conteo y los montos correctos.
///
/// Estas pruebas son de forma, no de comportamiento: el SQL solo se ejecuta de verdad contra Azure
/// SQL, así que la verificación de fondo es el export E2E contra la BD. Sirven para que el JOIN sin
/// acotar no vuelva a entrar sin que nadie se dé cuenta.
/// </summary>
public class CostExcelDataSourceV3QueriesTests
{
    private static string Sql(string key) =>
        CostExcelDataSourceV3.Queries.Single(q => q.Key == key).Sql;

    [Fact]
    public void Query_de_VMs_acota_sql_vm_details_al_analisis_en_curso()
    {
        var vms = Sql("vms");

        // El cruce vive en un OUTER APPLY correlacionado por analysis_id...
        Assert.Contains("dbo.sql_vm_details", vms);
        Assert.Contains("sr.analysis_id = cr.analysis_id", vms);
        Assert.Contains("SELECT TOP 1", vms);

        // ...y NO en el LEFT JOIN suelto que duplicaba las filas.
        Assert.DoesNotContain("LEFT JOIN dbo.sql_vm_details", vms);
    }

    [Fact]
    public void Ninguna_query_cruza_vm_azure_resource_id_sin_acotar_el_analisis()
    {
        // vm_azure_resource_id es un id ARM: no es único por análisis, así que cualquier query que
        // lo use como llave de cruce tiene que traer analysis_id en el mismo cruce.
        foreach (var (key, sql) in CostExcelDataSourceV3.Queries)
        {
            if (!sql.Contains("vm_azure_resource_id", StringComparison.OrdinalIgnoreCase)) continue;
            Assert.True(
                sql.Contains("analysis_id = cr.analysis_id", StringComparison.OrdinalIgnoreCase),
                $"la query \"{key}\" cruza por vm_azure_resource_id sin acotar el análisis: va a devolver una fila por análisis histórico");
        }
    }
}
