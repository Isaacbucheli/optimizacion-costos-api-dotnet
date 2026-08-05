using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.AlertCatalog;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.PolicyCatalog;

namespace OptimizacionCostos.Api.Tests.Data;

/// <summary>
/// Los mapas de ancho máximo son una copia a mano del DDL. Si alguien agrega una columna
/// `NVARCHAR(n)` al esquema y a la whitelist pero olvida el mapa, la validación de longitud
/// desaparece **en silencio** para ese campo y vuelve el error 8152 de SQL Server, que fue lo que el
/// ZAP del 2026-08-03 reportó como Format String. `ColumnLimitsTests` verifica que cada entrada del
/// mapa exista en la whitelist; acá se verifica la dirección contraria, que es la que importa.
///
/// El DDL se lee del archivo fuente, no de la base: la prueba no necesita SQL y falla en build.
/// </summary>
public sealed class ColumnLimitsSchemaSyncTests
{
    public static TheoryData<string, string, string, IReadOnlyDictionary<string, int>> Tablas() => new()
    {
        { "policy_catalog",    "Features/PolicyCatalog/PolicyCatalogSchema.cs", "PolicyColumns.MaxLengths",        PolicyColumns.MaxLengths },
        { "alert_catalog",     "Features/AlertCatalog/AlertCatalogSchema.cs",   "AlertColumns.AlertMaxLengths",    AlertColumns.AlertMaxLengths },
        { "alert_kql_library", "Features/AlertCatalog/AlertCatalogSchema.cs",   "AlertColumns.KqlMaxLengths",      AlertColumns.KqlMaxLengths },
        { "boletin_lifecycle", "Features/Boletin/BoletinLifecycleStore.cs",     "LifecycleColumns.MaxLengths",     LifecycleColumns.MaxLengths },
        { "boletin_migracion", "Features/Boletin/BoletinMigracionStore.cs",     "MigracionColumns.MaxLengths",     MigracionColumns.MaxLengths },
    };

    [Theory]
    [MemberData(nameof(Tablas))]
    public void Toda_columna_acotada_del_DDL_tiene_su_limite_declarado(
        string tabla, string archivoRelativo, string nombreDelMapa, IReadOnlyDictionary<string, int> mapa)
    {
        var ddl = ColumnasAcotadas(tabla, archivoRelativo);
        Assert.NotEmpty(ddl); // si el parseo falla, el resto pasaría sola y fingiría cobertura

        foreach (var (columna, ancho) in ddl)
        {
            Assert.True(mapa.ContainsKey(columna),
                $"dbo.{tabla}.{columna} es NVARCHAR({ancho}) pero no está en {nombreDelMapa}: " +
                "un valor más largo llegaría a SQL Server y el UPDATE moriría con el error 8152.");
            Assert.Equal(ancho, mapa[columna]);
        }
    }

    [Theory]
    [MemberData(nameof(Tablas))]
    public void El_mapa_no_declara_columnas_que_el_DDL_no_acota(
        string tabla, string archivoRelativo, string nombreDelMapa, IReadOnlyDictionary<string, int> mapa)
    {
        var ddl = ColumnasAcotadas(tabla, archivoRelativo);
        foreach (var columna in mapa.Keys)
            Assert.True(ddl.ContainsKey(columna),
                $"{nombreDelMapa} declara '{columna}', que no es una NVARCHAR(n) de dbo.{tabla}. " +
                "O sobra la entrada, o la columna se renombró y el límite dejó de aplicarse.");
    }

    // -------------------- lectura del DDL --------------------

    /// <summary>
    /// Columnas NVARCHAR(n) del CREATE TABLE indicado. Toma la ventana desde el CREATE TABLE hasta el
    /// fin del literal (`"""`) o el siguiente CREATE TABLE, lo que llegue primero: el bloque de
    /// Boletín cierra con `UNIQUE (clave))` y no con `);` como el de políticas, así que delimitar por
    /// el paréntesis no sirve para todos. Las NVARCHAR(MAX) no matchean el `\d+` y quedan fuera, que
    /// es justo lo que se quiere.
    /// </summary>
    private static Dictionary<string, int> ColumnasAcotadas(string tabla, string archivoRelativo)
    {
        var sql = File.ReadAllText(Path.Combine(DirectorioDeFuentes(), archivoRelativo));

        var inicio = sql.IndexOf($"CREATE TABLE dbo.{tabla}", StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"no se encontró 'CREATE TABLE dbo.{tabla}' en {archivoRelativo}");

        var fin = sql.Length;
        foreach (var terminador in new[] { "\"\"\"", "CREATE TABLE dbo." })
        {
            var i = sql.IndexOf(terminador, inicio + $"CREATE TABLE dbo.{tabla}".Length, StringComparison.Ordinal);
            if (i >= 0 && i < fin) fin = i;
        }

        return Regex.Matches(sql[inicio..fin], @"(\w+)\s+NVARCHAR\((\d+)\)", RegexOptions.IgnoreCase)
            .ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value), StringComparer.Ordinal);
    }

    /// <summary>
    /// Ruta de src/OptimizacionCostos.Api, resuelta desde la ubicación de ESTE archivo en tiempo de
    /// compilación. No se usa el directorio de ejecución porque el binario de tests vive en
    /// bin/Debug/net8.0 y la profundidad hasta la raíz cambia según la configuración.
    /// </summary>
    private static string DirectorioDeFuentes([CallerFilePath] string archivoDeEstaPrueba = "")
    {
        // <raiz>/tests/OptimizacionCostos.Api.Tests/Data/<este archivo>
        var raiz = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(archivoDeEstaPrueba)!, "..", "..", ".."));
        var src = Path.Combine(raiz, "src", "OptimizacionCostos.Api");
        Assert.True(Directory.Exists(src),
            $"no se encontró el árbol de fuentes en '{src}'. Si la estructura del repo cambió, " +
            "hay que ajustar esta prueba: sin el DDL no se puede verificar que los mapas estén completos.");
        return src;
    }
}
