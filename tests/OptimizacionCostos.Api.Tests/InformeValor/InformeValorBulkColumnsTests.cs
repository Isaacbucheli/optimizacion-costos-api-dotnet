using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Verifica la proyección a DataTable sin base de datos. Un null de C# en un SqlParameter
/// lanza SqlException 8178; en SqlBulkCopy el equivalente es que la celda tiene que ser
/// DBNull.Value y la columna admitir nulos. También cubre, en el mismo espíritu de "sin base
/// de datos", que MarkRunFailedAsync no tape una excepción real si ni la bitácora se puede
/// escribir.
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

    /// <summary>
    /// informe_valor_rbac es el contrato de destino (tabla completa, no se toca el esquema): las
    /// columnas de la proyección tienen que ser EXACTAMENTE las de esa tabla, ni una más ni una
    /// menos. En particular, RoleClass/IsCustomRole de RbacRow NO aparecen acá a propósito: la
    /// tabla no tiene columnas para ellos (ver el comentario de clase de RbacRow) y agregarlas
    /// hubiera exigido tocar el esquema, que esta tarea no habilita.
    /// </summary>
    [Fact]
    public void Las_columnas_de_rbac_cubren_el_esquema_y_no_incluyen_RoleClass_ni_IsCustomRole()
    {
        var nombres = SqlInformeValorStore.RbacColumns.Select(c => c.Column).ToList();
        Assert.Equal(
            ["client_id", "ingesta_id", "natural_key_hash", "sheet_name", "suscripcion", "scope",
             "nivel", "rol", "tipo", "nombre", "login", "cuenta_activa", "ultimo_login"],
            nombres);
    }

    [Fact]
    public void Los_nulos_de_rbac_se_mapean_a_DBNull()
    {
        var fila = new RbacRow("h", null, null, null, null, null, null, null, null, null, null, null, false);
        foreach (var (column, _, value) in SqlInformeValorStore.RbacColumns)
            Assert.True(value(fila) is not null, $"La columna {column} devolvió null de C#");
    }

    [Fact]
    public void El_hash_viaja_como_texto_de_64_caracteres()
    {
        var col = SqlInformeValorStore.FacturacionColumns.Single(c => c.Column == "natural_key_hash");
        Assert.Equal(typeof(string), col.Type);
    }

    /// <summary>
    /// MarkRunFailedAsync es mejor esfuerzo: si ni la bitácora se puede escribir (acá, porque la
    /// conexión nunca se abrió, sin necesidad de una base de datos real) no tiene que lanzar. La
    /// excepción que le importa al consultor es la original de la carga, que ReplaceAsync
    /// relanza con throw justo después de este llamado, no una nueva sobre este intento.
    /// </summary>
    [Fact]
    public async Task MarkRunFailedAsync_no_lanza_si_la_bitacora_tampoco_se_puede_escribir()
    {
        using var conn = new SqlConnection();
        await SqlInformeValorStore.MarkRunFailedAsync(
            conn, ingestaId: 1, new InvalidOperationException("boom"), CancellationToken.None);
    }
}
