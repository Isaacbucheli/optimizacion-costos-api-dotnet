using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OptimizacionCostos.Api.Tests.Security;

/// <summary>
/// Cierre de Swagger en producción (hallazgo A1 de la revisión de seguridad): publicaba toda la
/// superficie de la API —155 KB de contrato— a cualquiera sin token.
///
/// Los dos casos viven en la MISMA clase a propósito: el flag se lee de una variable de entorno
/// (AppConfig la consulta antes de que el arnés pueda inyectar configuración), y las variables de
/// entorno son globales al proceso. xUnit no paraleliza dentro de una clase, así que así no se
/// pisan entre sí.
/// </summary>
public sealed class SwaggerGatingTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        // Sin esto el entorno del arnés decidiría por nosotros y el gate quedaría sin probar:
        // en Development Swagger se habilita solo, sin flag.
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Production");
    }

    private static Factory FactoryWith(bool swaggerEnabled)
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", TestAppFactory.Secret);
        Environment.SetEnvironmentVariable("SWAGGER_ENABLED", swaggerEnabled ? "true" : "false");
        return new Factory();
    }

    [Fact]
    public async Task En_produccion_y_sin_el_flag_Swagger_no_existe()
    {
        using var factory = FactoryWith(swaggerEnabled: false);
        var client = factory.CreateClient();

        var ui = await client.GetAsync("/swagger/index.html");
        var contrato = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.NotFound, ui.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, contrato.StatusCode);
        // Sin Swagger no hay HTML que proteger, así que la ruta recibe la CSP estricta como todo
        // lo demás: la excepción no se queda colgada cuando el flag está apagado.
        Assert.Contains("default-src 'none'", ui.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Con_el_flag_prendido_Swagger_responde_y_queda_exceptuado_de_la_CSP()
    {
        using var factory = FactoryWith(swaggerEnabled: true);
        var client = factory.CreateClient();

        var ui = await client.GetAsync("/swagger/index.html");
        var contrato = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal(HttpStatusCode.OK, contrato.StatusCode);
        // Con `default-src 'none'` la interfaz de Swagger se vería en blanco.
        Assert.False(ui.Headers.Contains("Content-Security-Policy"));
        // El resto de cabeceras sí debe aplicar.
        Assert.Equal("nosniff", ui.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
