using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Features.Waf;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Regresión del bug de apply de la matriz Excel: los campos de tracking null
/// (execution_log, projected_bit_effort, priority_override, internal_notes) deben
/// mapearse a DBNull.Value. Un SqlParameter con Value = null de C# hace que
/// Microsoft.Data.SqlClient trate el parámetro como "no suministrado" y el servidor
/// lance SqlException 8178 ("expects the parameter '@execution_log', which was not
/// supplied"), abortando toda la importación (transacción rollback → 0 filas cargadas).
/// </summary>
public sealed class WafExcelImporterTrackingParamTests
{
    [Fact]
    public void ToDb_Null_DevuelveDbNull_NoNullDeCSharp()
    {
        var result = ClosedXmlWafImporter.ToDb(null);
        Assert.Equal(DBNull.Value, result);
        Assert.NotNull(result); // nunca null de C#: eso dispararía SqlException 8178
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData("2026-06-08")]
    public void ToDb_ValorPresente_PasaTalCual(object value)
    {
        Assert.Equal(value, ClosedXmlWafImporter.ToDb(value));
    }

    [Fact]
    public void SqlParameter_ConTrackingNull_TieneValueDbNull()
    {
        // Reproduce la construcción del MERGE de ApplyTrackingUpdateAsync para un alta
        // desde Excel donde el campo de tracking viene vacío (caso normal).
        var p = new SqlParameter("@execution_log", ClosedXmlWafImporter.ToDb(null));
        Assert.Equal(DBNull.Value, p.Value);
    }
}
