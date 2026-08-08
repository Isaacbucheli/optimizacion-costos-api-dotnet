using System.Runtime.CompilerServices;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Global Constraint del plan de la entrega 2b: "la calculadora no llama a <c>DateTime.Now</c> ni
/// a <c>DateTime.UtcNow</c> en ninguna parte. La fecha de corte entra como parámetro." Si no, el
/// informe cambia de contenido según cuándo se generó, que es justo lo que el port a C# viene a
/// eliminar.
///
/// Esta prueba escanea el texto fuente de <c>Features/InformeValor/Calculo</c> (mismo patrón que
/// <c>ColumnLimitsSchemaSyncTests</c>: lee el archivo, no la reflexión) y falla si alguna de las
/// cinco tareas de bloque que vienen después de esta, o el ensamblador, reintroduce la lectura de
/// la hora del sistema. Vive en esta entrega (2b) porque el namespace recién se crea acá: sin este
/// guardia, la restricción depende solo de que cada implementador la recuerde leyendo el plan.
/// </summary>
public sealed class SinRelojDelSistemaTests
{
    [Fact]
    public void Ningun_archivo_de_Calculo_llama_DateTime_Now_ni_UtcNow()
    {
        var carpeta = CarpetaCalculo();
        var archivos = Directory.GetFiles(carpeta, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(archivos); // si esto falla, la prueba no está mirando nada

        foreach (var archivo in archivos)
        {
            var texto = File.ReadAllText(archivo);
            Assert.DoesNotContain("DateTime.Now", texto, StringComparison.Ordinal);
            Assert.DoesNotContain("DateTime.UtcNow", texto, StringComparison.Ordinal);
        }
    }

    private static string CarpetaCalculo([CallerFilePath] string archivoDeEstaPrueba = "")
    {
        // <raiz>/tests/OptimizacionCostos.Api.Tests/InformeValor/Calculo/<este archivo>
        var raiz = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(archivoDeEstaPrueba)!, "..", "..", "..", ".."));
        var carpeta = Path.Combine(raiz, "src", "OptimizacionCostos.Api", "Features", "InformeValor", "Calculo");
        Assert.True(Directory.Exists(carpeta),
            $"no se encontró '{carpeta}'. Si la estructura del repo cambió, hay que ajustar esta prueba.");
        return carpeta;
    }
}
