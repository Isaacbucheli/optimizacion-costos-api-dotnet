using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;

namespace OptimizacionCostos.Api.Tests.InformeValor.Entrega;

/// <summary>
/// Corre de verdad la capa de dibujo del artefacto exportado y devuelve lo que quedó escrito en
/// cada nodo con id. El motor es <c>render-artefacto.mjs</c>, ejecutado con <c>node</c>; el
/// docstring de ese archivo explica el sustituto de DOM y qué emula.
///
/// <para><b>Por qué hace falta ejecutar el JavaScript.</b> Los otros dos tests de la plantilla
/// (<see cref="PlantillaCapaDeDibujoTests"/>, <see cref="ContratoEntreRenderizadoresTests"/>) son
/// barridos de texto y lo declaran: auditan nombres de campo, no comportamiento. Dos defectos de
/// este módulo no se ven así — <c>render()</c> reventando con un modelo legítimo (el artefacto sale
/// con los contadores del hero en su "0" literal y tres secciones vacías, y la entrega queda
/// archivada como exitosa) y una cifra vacía publicada como "0.00 %" en vez de decir por qué está
/// vacía. Los dos se ven mirando lo que el artefacto imprime.</para>
///
/// <para><b>Cuando no hay node.</b> Igual que <c>ContratoEntreRenderizadoresTests</c> con el repo
/// del front: <c>INFORME_VALOR_NODE=ninguno</c> declara que este entorno no lo tiene y los tests
/// quedan en no-op. Sin esa declaración, no encontrar <c>node</c> es un fallo con instrucciones —
/// un guardia apagado en silencio no protege nada.</para>
/// </summary>
internal static class RenderDeArtefacto
{
    /// <summary>Un nodo del artefacto tal como quedó después del dibujo.</summary>
    internal sealed record Nodo(string Html, string Texto)
    {
        /// <summary>El contenido del nodo sin distinguir texto de marcado: la plantilla escribe
        /// tarjetas con <c>innerHTML</c> y subtítulos con <c>textContent</c>, y a un test le importa
        /// qué dice el nodo, no por cuál de los dos caminos lo dijo.</summary>
        public string Todo => Html + " " + Texto;
    }

    internal sealed record Resultado(
        bool Ok, string? Error, string? Stack, IReadOnlyDictionary<string, Nodo> Elementos)
    {
        public Nodo Nodo(string id)
        {
            Assert.True(Elementos.TryGetValue(id, out var n),
                $"el artefacto nunca escribió #{id}. Nodos escritos: {string.Join(", ", Elementos.Keys.Order(StringComparer.Ordinal))}");
            return n!;
        }

        /// <summary>Todo lo que el artefacto imprimió, para las afirmaciones de "esto no aparece en
        /// ninguna parte del documento".</summary>
        public string TodoElDocumento =>
            string.Join("\n", Elementos.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"[{e.Key}] {e.Value.Todo}"));

        /// <summary>Falla con el error de JavaScript adentro: un <c>render()</c> caído deja el
        /// artefacto a medias y el mensaje tiene que decir dónde se cayó.</summary>
        public Resultado ExigirQueDibujeCompleto()
        {
            Assert.True(Ok,
                $"render() se cayó dibujando el artefacto, así que el archivo entregado queda a medias " +
                $"(hero sin animar, secciones vacías) y la entrega igual se archiva como exitosa. " +
                $"Error: {Error}{Environment.NewLine}{Stack}");
            return this;
        }
    }

    /// <summary>Exporta el artefacto y lo dibuja. <c>null</c> cuando el entorno declara que no tiene
    /// node (ver el comentario de clase): quien llama sale sin afirmar nada.</summary>
    public static Resultado? Correr(
        ModeloInformeValor modelo,
        VarianteInforme variante = VarianteInforme.Interna,
        IReadOnlyCollection<BloqueEconomico>? bloques = null)
    {
        if (SinNodeDeclarado) return null;

        var artefacto = InformeValorHtmlExporter.Exportar(modelo, variante, bloques);
        var archivo = Path.Combine(Path.GetTempPath(), $"informe-valor-{Guid.NewGuid():N}.html");
        File.WriteAllBytes(archivo, artefacto.Contenido);
        try
        {
            return Deserializar(Ejecutar(archivo));
        }
        finally
        {
            File.Delete(archivo);
        }
    }

    private static bool SinNodeDeclarado =>
        string.Equals(Environment.GetEnvironmentVariable("INFORME_VALOR_NODE"), "ninguno",
            StringComparison.OrdinalIgnoreCase);

    private static string Ejecutar(string artefacto)
    {
        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(RutaDelMotor());
        psi.ArgumentList.Add(artefacto);

        Process? proceso;
        try
        {
            proceso = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "No se pudo ejecutar 'node' para dibujar el artefacto del informe de valor. Es el único " +
                "test de este módulo que ejecuta la capa de dibujo de verdad. Opciones: instalar Node " +
                "(los runners de GitHub ya lo traen), o poner INFORME_VALOR_NODE=ninguno para declarar " +
                $"que este entorno no lo tiene y perder esa cobertura. Detalle: {ex.Message}", ex);
        }

        Assert.NotNull(proceso);
        var salida = proceso!.StandardOutput.ReadToEnd();
        var error = proceso.StandardError.ReadToEnd();
        proceso.WaitForExit(60_000);

        Assert.True(salida.Length > 0,
            $"el motor de dibujo no devolvió nada. Código {proceso.ExitCode}. stderr: {error}");
        return salida;
    }

    private static Resultado Deserializar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;
        var elementos = new Dictionary<string, Nodo>(StringComparer.Ordinal);
        foreach (var p in raiz.GetProperty("elementos").EnumerateObject())
            elementos[p.Name] = new Nodo(
                p.Value.GetProperty("html").GetString() ?? "",
                p.Value.GetProperty("texto").GetString() ?? "");

        return new Resultado(
            raiz.GetProperty("ok").GetBoolean(),
            raiz.GetProperty("error").GetString(),
            raiz.TryGetProperty("stack", out var s) ? s.GetString() : null,
            elementos);
    }

    /// <summary>El motor vive al lado de este archivo, no en la carpeta de salida: no hace falta
    /// copiarlo al bin, y así una edición del .mjs se prueba sin recompilar.</summary>
    private static string RutaDelMotor([CallerFilePath] string archivoDeEstaClase = "")
    {
        var ruta = Path.Combine(Path.GetDirectoryName(archivoDeEstaClase)!, "render-artefacto.mjs");
        Assert.True(File.Exists(ruta), $"no se encontró el motor de dibujo en '{ruta}'");
        return ruta;
    }
}
