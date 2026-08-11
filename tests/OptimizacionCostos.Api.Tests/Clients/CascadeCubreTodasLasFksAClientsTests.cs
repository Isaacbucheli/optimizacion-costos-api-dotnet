using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace OptimizacionCostos.Api.Tests.Clients;

/// <summary>
/// Este guardia existe porque el mismo defecto ya apareció dos veces: alguien agrega una tabla con
/// FK a <c>dbo.clients</c>, nadie se acuerda de sumarla a <c>DeleteClientCascadeAsync</c>, y borrar
/// un cliente empieza a fallar por clave foránea. La primera vez fue
/// <c>FK_waf_canonical_consolidates</c>, y la limpieza fallida de un cliente de prueba dejó una fila
/// colgada en la base de producción. La segunda fueron cuatro tablas a la vez: el barrido de
/// optimización (<c>optimization_scan</c>, <c>optimization_finding_state</c>) y el histórico y los
/// trabajos de sincronización de Advisor score.
///
/// Agregar las tablas que faltan arregla el pasado y no el futuro. Esto último es lo que hace este
/// test: escanea las declaraciones de FK contra <c>clients</c> en el código de esquema (mismo patrón
/// que <c>ColumnLimitsSchemaSyncTests</c>: lee el archivo, no la reflexión) y exige que el cascade
/// nombre cada tabla. Así la próxima falla acá, en el CI, y no en producción cuando alguien intenta
/// borrar un cliente.
///
/// Se exceptúan las FK declaradas con <c>ON DELETE CASCADE</c>: esas las limpia el motor.
/// </summary>
public sealed class CascadeCubreTodasLasFksAClientsTests
{
    [Fact]
    public void El_cascade_borra_toda_tabla_con_fk_a_clients()
    {
        var src = CarpetaSrc();
        var cascade = File.ReadAllText(Path.Combine(src, "Features", "Clients", "SqlClientStore.cs"));

        var conFk = TablasConFkAClients(src);
        Assert.NotEmpty(conFk); // si esto falla, el test no está mirando nada

        var faltantes = conFk
            .Where(t => !cascade.Contains($"dbo.{t}", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(faltantes.Count == 0,
            "Estas tablas tienen FK a dbo.clients y DeleteClientCascadeAsync no las borra, así que " +
            "borrar un cliente con filas en ellas va a fallar por clave foránea:\n  " +
            string.Join("\n  ", faltantes) +
            "\n\nAgregalas a PurgeCoreAsync (hijos antes que padres) con la guarda " +
            "IF OBJECT_ID(...) IS NOT NULL si la tabla se crea de forma diferida. Si la FK se " +
            "declaró con ON DELETE CASCADE, este test ya la ignora.");
    }

    /// <summary>
    /// Recorre los .cs de <paramref name="src"/> buscando las cadenas de <c>CREATE TABLE</c> del
    /// esquema. Por cada <c>REFERENCES dbo.clients</c> devuelve la tabla del <c>CREATE TABLE</c> más
    /// cercano hacia arriba, que es cómo están escritas todas las declaraciones del repo.
    /// </summary>
    private static IReadOnlyList<string> TablasConFkAClients(string src)
    {
        var creaTabla = new Regex(@"CREATE TABLE\s+(?:dbo\.)?(\w+)", RegexOptions.IgnoreCase);
        var fkAClients = new Regex(@"REFERENCES\s+dbo\.clients\b", RegexOptions.IgnoreCase);
        var tablas = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var archivo in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string? tablaActual = null;
            foreach (var linea in File.ReadLines(archivo))
            {
                var m = creaTabla.Match(linea);
                if (m.Success) tablaActual = m.Groups[1].Value;

                if (tablaActual is null || !fkAClients.IsMatch(linea)) continue;
                // El motor limpia estas solo; nombrarlas en el cascade sería redundante.
                if (linea.Contains("ON DELETE CASCADE", StringComparison.OrdinalIgnoreCase)) continue;
                tablas.Add(tablaActual);
            }
        }

        // La propia clients aparece como destino de las FK, no como tabla con FK a sí misma.
        tablas.Remove("clients");
        return [.. tablas];
    }

    private static string CarpetaSrc([CallerFilePath] string archivoDeEstaPrueba = "")
    {
        // <raiz>/tests/OptimizacionCostos.Api.Tests/Clients/<este archivo>
        var raiz = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(archivoDeEstaPrueba)!, "..", "..", ".."));
        var carpeta = Path.Combine(raiz, "src", "OptimizacionCostos.Api");
        Assert.True(Directory.Exists(carpeta),
            $"no se encontró '{carpeta}'. Si la estructura del repo cambió, hay que ajustar esta prueba.");
        return carpeta;
    }
}
