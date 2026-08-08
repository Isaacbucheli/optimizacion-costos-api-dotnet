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

    /// <summary>
    /// rows_merged tiene que estar en el CREATE inline (instalación nueva) Y en un soft-migration
    /// guardado por COL_LENGTH (base ya creada por la entrega 1, sin la columna): sin el segundo,
    /// una base que corrió el esquema viejo se queda para siempre sin poder persistir la cuenta de
    /// filas fusionadas, aunque el código nuevo ya la calcule.
    /// </summary>
    [Fact]
    public void Rows_merged_esta_en_el_create_inline_de_ingesta()
    {
        var crea = InformeValorSchema.Statements
            .Single(s => s.Contains("CREATE TABLE dbo.informe_valor_ingesta", StringComparison.Ordinal));
        Assert.Contains("rows_merged INT NOT NULL", crea, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_merged_tiene_soft_migration_para_bases_preexistentes()
    {
        var todo = string.Join("\n", InformeValorSchema.Statements);
        Assert.Contains(
            "IF COL_LENGTH('dbo.informe_valor_ingesta', 'rows_merged') IS NULL", todo, StringComparison.Ordinal);
    }
}
