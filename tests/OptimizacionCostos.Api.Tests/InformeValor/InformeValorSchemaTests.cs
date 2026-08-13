using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

public sealed class InformeValorSchemaTests
{
    [Theory]
    [InlineData("dbo.informe_valor_ingesta")]
    [InlineData("dbo.informe_valor_facturacion")]
    [InlineData("dbo.informe_valor_caso")]
    [InlineData("dbo.informe_valor_rbac")]
    [InlineData("dbo.informe_valor_entrega")]
    [InlineData("dbo.informe_valor_evolucion")]
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
    [InlineData("dbo.informe_valor_evolucion")]
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

    /// <summary>
    /// Mismo patrón que rows_merged (ver el test de arriba): role_class/is_custom_role tienen que
    /// estar en el CREATE inline de informe_valor_rbac (instalación nueva) Y en un soft-migration
    /// guardado por COL_LENGTH (tabla ya creada por una entrega anterior, sin las columnas) -- sin
    /// el segundo, esa base se queda para siempre sin poder persistir la clase de rol, aunque el
    /// código nuevo ya la calcule y la clasificación del bloque de seguridad ya dependa de ella.
    /// </summary>
    [Fact]
    public void RoleClass_e_IsCustomRole_estan_en_el_create_inline_de_rbac()
    {
        var crea = InformeValorSchema.Statements
            .Single(s => s.Contains("CREATE TABLE dbo.informe_valor_rbac", StringComparison.Ordinal));
        Assert.Contains("role_class NVARCHAR(30) NULL", crea, StringComparison.Ordinal);
        Assert.Contains("is_custom_role BIT NOT NULL", crea, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleClass_e_IsCustomRole_tienen_soft_migration_para_bases_preexistentes()
    {
        var todo = string.Join("\n", InformeValorSchema.Statements);
        Assert.Contains(
            "IF COL_LENGTH('dbo.informe_valor_rbac', 'role_class') IS NULL", todo, StringComparison.Ordinal);
        Assert.Contains(
            "IF COL_LENGTH('dbo.informe_valor_rbac', 'is_custom_role') IS NULL", todo, StringComparison.Ordinal);
    }

    // ===================================================================================
    // informe_valor_entrega (F4 de la entrega 3)
    // ===================================================================================

    /// <summary>
    /// Reemitir el mismo período es legítimo y el historial importa, así que la tabla de entregas
    /// es la única de este módulo <b>sin</b> unicidad. Con un índice único por período, la segunda
    /// emisión de un informe fallaría con un 500 en vez de archivarse.
    /// </summary>
    [Fact]
    public void La_tabla_de_entregas_no_lleva_unicidad_por_periodo()
    {
        var todo = string.Join("\n", InformeValorSchema.Statements);
        var lineas = todo.Split('\n')
            .Where(l => l.Contains("informe_valor_entrega", StringComparison.Ordinal)
                     && l.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(lineas);
    }

    [Fact]
    public void La_tabla_de_entregas_valida_el_rango_y_se_indexa_por_fecha()
    {
        var crea = InformeValorSchema.Statements
            .Single(s => s.Contains("CREATE TABLE dbo.informe_valor_entrega", StringComparison.Ordinal));

        Assert.Contains("CHECK (period_end >= period_start)", crea, StringComparison.Ordinal);
        Assert.Contains("ON dbo.informe_valor_entrega (client_id, generated_at DESC)", crea, StringComparison.Ordinal);
    }

    /// <summary>
    /// El criterio de F4: si un dato entra al cálculo y viene de una fuente que cambia sola, o está
    /// en esta tabla o la entrega archivada miente al reemitirse. La foto de reservas es la que pide
    /// el plan; las otras salieron de revisar el modelo con el mismo criterio (la corrida de
    /// Revisión de accesos, la bandera de seguridad gestionada por fuera, y qué carga de cada insumo
    /// alimentó la entrega).
    /// </summary>
    [Theory]
    [InlineData("period_start")]
    [InlineData("period_end")]
    [InlineData("corte")]
    [InlineData("meses_parciales")]
    [InlineData("variante")]
    [InlineData("bloques_publicados")]
    [InlineData("rbac_origen")]
    [InlineData("rbac_corrida_fecha")]
    [InlineData("seguridad_gestionada_externamente")]
    [InlineData("facturacion_ingesta_id")]
    [InlineData("casos_ingesta_id")]
    [InlineData("rbac_ingesta_id")]
    [InlineData("foto_reservas_json")]
    [InlineData("plantilla_version")]
    [InlineData("blob_container")]
    [InlineData("blob_name")]
    [InlineData("blob_size_bytes")]
    [InlineData("file_name")]
    [InlineData("summary_json")]
    [InlineData("generated_by")]
    [InlineData("generated_at")]
    public void La_entrega_archiva_todo_lo_que_hace_falta_para_reproducirla(string columna)
    {
        var crea = InformeValorSchema.Statements
            .Single(s => s.Contains("CREATE TABLE dbo.informe_valor_entrega", StringComparison.Ordinal));

        Assert.Contains(columna, crea, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mismo patrón que rows_merged y role_class: las columnas que se agregaron después de que la
    /// tabla existiera necesitan soft-migration, o una base que corrió una versión anterior de este
    /// archivo se queda sin ellas y devuelve otras cifras al reemitir, sin fallar.
    /// </summary>
    [Theory]
    [InlineData("foto_reservas_json")]
    [InlineData("rbac_corrida_fecha")]
    [InlineData("seguridad_gestionada_externamente")]
    [InlineData("facturacion_ingesta_id")]
    [InlineData("casos_ingesta_id")]
    [InlineData("rbac_ingesta_id")]
    [InlineData("plantilla_version")]
    [InlineData("blob_container")]
    public void Las_columnas_de_reproducibilidad_tienen_soft_migration(string columna)
    {
        var todo = string.Join("\n", InformeValorSchema.Statements);

        Assert.Contains(
            $"IF COL_LENGTH('dbo.informe_valor_entrega', '{columna}') IS NULL", todo, StringComparison.Ordinal);
    }
}
