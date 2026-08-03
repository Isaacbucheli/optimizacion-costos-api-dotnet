using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

/// <summary>
/// Pruebas puras (sin BD) del store de novedades: la whitelist anti-inyección del PUT y la lista de
/// categoria_bit válidas. El flujo SQL/HTTP real (dedupe por feed_guid, traducción, schema) lo cubre
/// el E2E manual — igual que BoletinLifecycleStoreTests con el catálogo de lifecycle.
/// </summary>
public class BoletinNovedadStoreTests
{
    [Fact]
    public void LaWhitelistNoIncluyeColumnasDeSistemaNiTextosCurados()
    {
        Assert.DoesNotContain("id", NovedadColumns.Editable);
        Assert.DoesNotContain("feed_guid", NovedadColumns.Editable);
        Assert.DoesNotContain("created_at", NovedadColumns.Editable);
        Assert.DoesNotContain("updated_at", NovedadColumns.Editable);
        // titulo_es/descripcion_es/titulo/descripcion: NUNCA editables vía PUT — solo la ingesta
        // (primera escritura) y la traducción IA los tocan; un PUT no debe poder pisarlos.
        Assert.DoesNotContain("titulo", NovedadColumns.Editable);
        Assert.DoesNotContain("titulo_es", NovedadColumns.Editable);
        Assert.DoesNotContain("descripcion", NovedadColumns.Editable);
        Assert.DoesNotContain("descripcion_es", NovedadColumns.Editable);
        Assert.Contains("categoria_bit", NovedadColumns.Editable);
        Assert.Contains("is_active", NovedadColumns.Editable);
        Assert.Equal(2, NovedadColumns.Editable.Length);
    }

    [Fact]
    public void LasCategoriasBitValidasSonLasCuatroDelMapeoDelFeed()
    {
        // Mismas 4 que AzureUpdatesFeed.MapCategoriaBit puede producir (3 mapeadas + el default
        // "resiliencia_plataforma"): si el feed algún día produce una 5ta, este test debe fallar
        // primero (documentado) en vez de que el consultor descubra un 400 misterioso en el PUT.
        Assert.Equal(4, NovedadColumns.CategoriasBitValidas.Length);
        Assert.Contains("productividad_ia", NovedadColumns.CategoriasBitValidas);
        Assert.Contains("seguridad_identidad", NovedadColumns.CategoriasBitValidas);
        Assert.Contains("costo_operacion", NovedadColumns.CategoriasBitValidas);
        Assert.Contains("resiliencia_plataforma", NovedadColumns.CategoriasBitValidas);

        Assert.Contains(AzureUpdatesFeed.MapCategoriaBit(["Security"]), NovedadColumns.CategoriasBitValidas);
        Assert.Contains(AzureUpdatesFeed.MapCategoriaBit(["Categoria Desconocida"]), NovedadColumns.CategoriasBitValidas);
    }
}
