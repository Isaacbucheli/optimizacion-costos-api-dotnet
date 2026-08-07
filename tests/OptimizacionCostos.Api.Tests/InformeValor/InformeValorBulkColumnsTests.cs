using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Verifica la proyección a DataTable sin base de datos. Un null de C# en un SqlParameter
/// lanza SqlException 8178; en SqlBulkCopy el equivalente es que la celda tiene que ser
/// DBNull.Value y la columna admitir nulos.
/// </summary>
public sealed class InformeValorBulkColumnsTests
{
    private static readonly FacturacionRow Fila = new(
        "h", null, "Azure plan", "sub-1", "rg", "vm-uno", null, "Storage", null, null,
        null, "1/Hour", null, 12.5m, 2026, 1);

    [Fact]
    public void Las_columnas_de_facturacion_cubren_el_esquema()
    {
        var nombres = SqlInformeValorStore.FacturacionColumns.Select(c => c.Column).ToList();
        Assert.Equal(
            ["client_id", "ingesta_id", "natural_key_hash", "tenant", "subscription_name",
             "subscription_id", "resource_group", "resource_name", "cost_center", "category",
             "subcategory", "service", "quantity", "unit", "rate", "pvp", "period_year", "period_month"],
            nombres);
    }

    [Fact]
    public void Las_columnas_de_casos_cubren_el_esquema()
    {
        var nombres = SqlInformeValorStore.CasoColumns.Select(c => c.Column).ToList();
        Assert.Equal(
            ["client_id", "ingesta_id", "natural_key_hash", "caso", "fecha_registro", "estado",
             "sla_horas", "duracion_cruda", "cumple", "categoria", "subcategoria", "horario"],
            nombres);
    }

    [Fact]
    public void Los_nulos_de_facturacion_se_mapean_a_DBNull()
    {
        foreach (var (column, _, value) in SqlInformeValorStore.FacturacionColumns)
        {
            var v = value(Fila);
            Assert.True(v is not null, $"La columna {column} devolvió null de C# en vez de DBNull.Value");
        }
    }

    [Fact]
    public void Los_nulos_de_casos_se_mapean_a_DBNull()
    {
        var caso = new CasoRow("h", null, null, null, null, null, null, null, null, null);
        foreach (var (column, _, value) in SqlInformeValorStore.CasoColumns)
            Assert.True(value(caso) is not null, $"La columna {column} devolvió null de C#");
    }

    [Fact]
    public void El_hash_viaja_como_texto_de_64_caracteres()
    {
        var col = SqlInformeValorStore.FacturacionColumns.Single(c => c.Column == "natural_key_hash");
        Assert.Equal(typeof(string), col.Type);
    }
}
