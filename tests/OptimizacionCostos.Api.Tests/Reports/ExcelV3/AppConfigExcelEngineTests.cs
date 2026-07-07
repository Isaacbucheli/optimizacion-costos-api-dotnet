using OptimizacionCostos.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;

[Collection("UserSessionsEnv")] // serializa clases que mutan env vars de proceso (colección existente)
public class AppConfigExcelEngineTests
{
    private static AppConfig Build(string? engine)
    {
        var original = Environment.GetEnvironmentVariable("EXCEL_EXPORT_ENGINE");
        try
        {
            Environment.SetEnvironmentVariable("EXCEL_EXPORT_ENGINE", engine);
            return AppConfig.FromConfiguration(new ConfigurationBuilder().Build());
        }
        finally { Environment.SetEnvironmentVariable("EXCEL_EXPORT_ENGINE", original); }
    }

    [Fact]
    public void Default_es_template() => Assert.Equal("template", Build(null).ExcelExportEngine);

    [Theory]
    [InlineData("code", "code")]
    [InlineData(" CODE ", "code")]
    [InlineData("template", "template")]
    public void Env_normalizado(string raw, string expected) => Assert.Equal(expected, Build(raw).ExcelExportEngine);
}
