using System.Linq;
using OptimizacionCostos.Api.Features.Catalog;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.Inventory;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Catalog;

/// <summary>
/// Guardas de <see cref="CatalogSeed"/>: un typo en cualquier clave de
/// <see cref="CatalogSeed.Entries"/> (calculator_key, inserter_key, detail_table_name,
/// azure_resource_type) sería un no-op silencioso en runtime — el servicio quedaría en el
/// catálogo pero import/cálculo nunca lo tocarían. Estos tests fallan en build si eso pasa.
/// </summary>
public sealed class CatalogSeedTests
{
    [Fact]
    public void CadaEntrada_TieneCalculadoraRegistrada()
    {
        Assert.All(CatalogSeed.Entries, e =>
            Assert.True(
                CalculatorRegistry.SupportedKeys.Contains(e.CalculatorKey),
                $"service_key '{e.ServiceKey}': calculator_key '{e.CalculatorKey}' no está registrado en CalculatorRegistry.SupportedKeys"));
    }

    [Fact]
    public void CadaEntrada_TieneInserterRegistrado()
    {
        Assert.All(CatalogSeed.Entries, e =>
            Assert.True(
                InventoryInserter.InserterKeys.Contains(e.InserterKey),
                $"service_key '{e.ServiceKey}': inserter_key '{e.InserterKey}' no está registrado en InventoryInserter.InserterKeys"));
    }

    [Fact]
    public void CadaEntrada_TieneTablaDetalleConocida()
    {
        Assert.All(CatalogSeed.Entries, e =>
        {
            Assert.False(
                string.IsNullOrWhiteSpace(e.DetailTableName),
                $"service_key '{e.ServiceKey}': detail_table_name está vacío");
            Assert.True(
                InventoryInserter.KnownDetailTables.Contains(e.DetailTableName),
                $"service_key '{e.ServiceKey}': detail_table_name '{e.DetailTableName}' no está en InventoryInserter.KnownDetailTables (el registro de cascade-delete); un re-import dejaría filas huérfanas");
        });
    }

    [Fact]
    public void CadaEntrada_UsaTipoDeRecursoEnMinusculas()
    {
        Assert.All(CatalogSeed.Entries, e =>
            Assert.True(
                e.AzureResourceType == e.AzureResourceType.ToLowerInvariant(),
                $"service_key '{e.ServiceKey}': azure_resource_type '{e.AzureResourceType}' no está en minúsculas — Resource Graph devuelve 'type' en minúsculas y SqlResourceLoader compara con match exacto, así que una mayúscula aquí da cero resultados en silencio"));
    }

    [Fact]
    public void LasDosEntradasEsperadas_EstanPresentes()
    {
        var keys = CatalogSeed.Entries.Select(e => e.ServiceKey).ToHashSet();
        Assert.Equal(new HashSet<string> { "snapshots", "storage_files" }, keys);
    }
}
