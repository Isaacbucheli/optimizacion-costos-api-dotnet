using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

public sealed class InformeValorSchemaTests
{
    [Theory]
    [InlineData("dbo.informe_valor_ingesta")]
    [InlineData("dbo.informe_valor_facturacion")]
    [InlineData("dbo.informe_valor_caso")]
    [InlineData("dbo.informe_valor_rbac")]
    public void Cada_tabla_se_crea_con_guarda_de_existencia(string tabla)
    {
        var crea = InformeValorSchema.Statements
            .Where(s => s.Contains($"CREATE TABLE {tabla}", StringComparison.Ordinal)).ToList();
        Assert.Single(crea);
        Assert.Contains($"IF OBJECT_ID('{tabla}', 'U') IS NULL", crea[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dbo.informe_valor_facturacion")]
    [InlineData("dbo.informe_valor_caso")]
    [InlineData("dbo.informe_valor_rbac")]
    public void Las_tablas_de_datos_tienen_indice_unico_por_cliente_y_hash(string tabla)
    {
        var todo = string.Join("\n", InformeValorSchema.Statements);
        Assert.Contains($"ON {tabla} (client_id, natural_key_hash)", todo, StringComparison.Ordinal);
    }

    [Fact]
    public void Ningun_CREATE_TABLE_queda_sin_guarda()
    {
        foreach (var s in InformeValorSchema.Statements.Where(x => x.Contains("CREATE TABLE", StringComparison.Ordinal)))
            Assert.Contains("IF OBJECT_ID(", s, StringComparison.Ordinal);
    }
}
