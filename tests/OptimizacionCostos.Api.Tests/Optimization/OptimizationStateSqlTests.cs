namespace OptimizacionCostos.Api.Tests.Optimization;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.Optimization;

public class OptimizationStateSqlTests
{
    /// <summary>Un consultor que marca 'resuelto' es autoría declarada; reabrir limpia la marca.
    /// Sin esto, el registro del informe de valor no puede separar lo nuestro de lo del cliente.
    /// Las tres ramas del CASE están fijadas: (1) resuelto→'manual', (2) abierto/en_progreso→NULL, (3) else→preservar.</summary>
    [Fact]
    public void El_update_manual_estampa_la_autoria_y_reabrir_la_limpia()
    {
        // Rama 1: cuando se marca resuelto, la autoría es 'manual' (consultor)
        Assert.Contains("WHEN @state = 'resuelto' THEN 'manual'", OptimizationService.UpdateStateSql);

        // Rama 2: cuando se reabre (abierto o en_progreso), la autoría se limpia (NULL)
        Assert.Contains("WHEN @state IN ('abierto', 'en_progreso') THEN NULL", OptimizationService.UpdateStateSql);

        // Rama 3: en cualquier otro estado, se preserva la autoría existente
        Assert.Contains("ELSE resolved_by_kind END", OptimizationService.UpdateStateSql);
    }

    /// <summary>El auto-resuelto del reconcile queda marcado como tal, nunca como declarado.</summary>
    [Fact]
    public void El_auto_resuelto_estampa_auto()
    {
        Assert.Contains("resolved_by_kind = 'auto'", OptimizationService.AutoResolveSql);
    }

    /// <summary>La columna llega por soft-migration: BD existentes no se recrean.</summary>
    [Fact]
    public void El_schema_agrega_la_columna_con_guarda()
    {
        Assert.Contains("COL_LENGTH('dbo.optimization_finding_state', 'resolved_by_kind')",
            OptimizationService.SchemaSql);
    }

    /// <summary>
    /// Que la columna llegue por soft-migration no sirve de nada si el método que la usa no corre
    /// esa migración. Pasó de verdad: el UPDATE de estado ganó <c>resolved_by_kind</c> y el método
    /// abría su conexión sin llamar al ensure, así que en una BD donde
    /// <c>optimization_finding_state</c> YA existía sin la columna —o sea, producción— el primer
    /// consultor que marcara un hallazgo se llevaba un "Invalid column name". Los tres tests de
    /// arriba fijan el TEXTO del SQL y son ciegos a esto.
    ///
    /// <para>La regla que vigila: si un método abre su propia conexión y corre SQL que menciona una
    /// columna de soft-migration, tiene que asegurar el esquema en esa conexión. Los métodos que
    /// RECIBEN <c>SqlConnection conn</c> quedan fuera a propósito — ahí el responsable es el
    /// llamador, que es el contrato documentado de <c>BarridoResueltoRecolector</c>.</para>
    ///
    /// <para>Las constantes de SQL a vigilar no van escritas a mano: salen por reflexión de las que
    /// mencionan la columna, así que una constante nueva entra a la vigilancia sola.</para>
    /// </summary>
    [Fact]
    public void Todo_metodo_que_abre_su_conexion_y_usa_la_columna_asegura_el_esquema()
    {
        const string columna = "resolved_by_kind";
        var fuente = File.ReadAllText(ArchivoDelServicio());

        var constantesConLaColumna = typeof(OptimizationService)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(string))
            .Where(f => (f.GetRawConstantValue() as string ?? f.GetValue(null) as string ?? "").Contains(columna, StringComparison.Ordinal))
            .Select(f => f.Name)
            .ToArray();
        Assert.NotEmpty(constantesConLaColumna); // si esto falla, la prueba no está mirando nada

        // Un "trozo" es el texto desde una declaración a 4 espacios hasta la siguiente. Alcanza para
        // esto y evita contar llaves dentro de los literales de SQL.
        var trozos = Regex.Split(fuente, @"(?=^\x20{4}(?:public|private|internal|protected)\s)", RegexOptions.Multiline);

        var sinAsegurar = new List<string>();
        foreach (var trozo in trozos)
        {
            var abreSuConexion = trozo.Contains("factory.OpenAsync", StringComparison.Ordinal);
            var usaLaColumna = trozo.Contains(columna, StringComparison.Ordinal)
                || constantesConLaColumna.Any(c => trozo.Contains(c, StringComparison.Ordinal));
            if (!abreSuConexion || !usaLaColumna) continue;
            if (trozo.Contains("EnsureSchemaAsync", StringComparison.Ordinal)) continue;

            var firma = trozo.TrimStart().Split('\n')[0].Trim();
            sinAsegurar.Add(firma);
        }

        Assert.True(sinAsegurar.Count == 0,
            "Estos métodos abren su propia conexión y corren SQL con una columna de soft-migration " +
            $"sin asegurar el esquema, así que fallan contra una BD que ya existía sin la columna:{Environment.NewLine}" +
            string.Join(Environment.NewLine, sinAsegurar.Select(f => "  - " + f)));
    }

    private static string ArchivoDelServicio([CallerFilePath] string archivoDeEstaPrueba = "")
    {
        // <raiz>/tests/OptimizacionCostos.Api.Tests/Optimization/<este archivo>
        var raiz = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(archivoDeEstaPrueba)!, "..", "..", ".."));
        var archivo = Path.Combine(raiz, "src", "OptimizacionCostos.Api", "Features", "Optimization", "OptimizationService.cs");
        Assert.True(File.Exists(archivo),
            $"no se encontró '{archivo}'. Si la estructura del repo cambió, hay que ajustar esta prueba.");
        return archivo;
    }
}
