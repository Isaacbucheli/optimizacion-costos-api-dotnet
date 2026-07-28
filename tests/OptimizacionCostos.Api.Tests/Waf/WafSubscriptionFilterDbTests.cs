using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Filtro por suscripción contra Azure SQL real. Solo con BIT_INTEGRATION_DB=1.
/// Arma un cliente de prueba con dos suscripciones y verifica que summary, sections y el listado
/// respondan a la selección — y que SIN filtro todo siga exactamente igual que antes.
/// </summary>
public class WafSubscriptionFilterDbTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BIT_INTEGRATION_DB") == "1";

    private static ISqlConnectionFactory NewFactory() =>
        new SqlConnectionFactory(AppConfig.FromConfiguration(new ConfigurationBuilder().Build()));

    private const string SubA = "11111111-1111-1111-1111-1111111111aa";
    private const string SubB = "22222222-2222-2222-2222-2222222222bb";

    private static AdvisorRow Row(string name, string resource, string subId, string subName) => new(
        AdvisorName: name, AdvisorCategory: "OperationalExcellence", BusinessImpact: "Medium",
        ResourceName: resource, ResourceType: "microsoft.compute/virtualmachines",
        ResourceGroup: "rg-e2e", SubscriptionId: subId, SubscriptionName: subName,
        AzureResourceId: $"/subscriptions/{subId}/resourceGroups/rg-e2e/providers/microsoft.compute/virtualmachines/{resource}",
        AdditionalInfo: null);

    private static async Task DeleteCanonicalsAsync(ISqlConnectionFactory factory, string tag)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM dbo.waf_canonical_alias WHERE advisor_name LIKE @p;
            DELETE FROM dbo.waf_canonical_alias
            WHERE canonical_id IN (SELECT canonical_id FROM dbo.waf_recommendation_canonical WHERE advisor_name LIKE @p);
            DELETE FROM dbo.waf_recommendation_canonical WHERE advisor_name LIKE @p;
            """;
        cmd.Parameters.Add(new SqlParameter("@p", "%" + tag + "%"));
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Summary_sections_y_suscripciones_responden_a_la_seleccion()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var factory = NewFactory();
        var catalog = new SqlWafCatalogStore(factory);
        var recommendations = new SqlWafRecommendationStore(factory);
        var ingestion = new SqlWafIngestionStore(factory, catalog, recommendations);
        var clients = new SqlClientStore(factory);

        var tag = $"e2e-subfilter-{Guid.NewGuid():N}";
        var clientId = await clients.CreateAsync($"E2E filtro suscripcion {tag}", null, null, null, null);
        try
        {
            // Recomendación 1: presente en las DOS suscripciones. Recomendación 2: solo en B.
            await ingestion.IngestAdvisorRowsAsync(
                clientId, "Azure Advisor API",
                [
                    Row($"Enable backup {tag}", "vm-a", SubA, "sub-alpha"),
                    Row($"Enable backup {tag}", "vm-b", SubB, "sub-beta"),
                    Row($"Enable diagnostics {tag}", "vm-c", SubB, "sub-beta"),
                ],
                "e2e", metrics: null, replaceSubscriptionIds: null, dedupResolver: null, source: "advisor");

            // --- Listado del selector: sale de los hallazgos.
            var options = await recommendations.ListSubscriptionsAsync(clientId);
            Assert.Equal(2, options.Count);
            var alpha = options.Single(o => o.SubscriptionId == SubA);
            var beta = options.Single(o => o.SubscriptionId == SubB);
            Assert.Equal("sub-alpha", alpha.SubscriptionName);
            Assert.Equal(1, alpha.Recommendations);
            Assert.Equal(1, alpha.Resources);
            Assert.Equal(2, beta.Recommendations); // ordena primero la de más recomendaciones
            Assert.Equal(2, beta.Resources);
            Assert.Equal(SubB, options[0].SubscriptionId);

            // --- Sin filtro: comportamiento histórico.
            var summaryAll = await recommendations.GetSummaryAsync(clientId);
            Assert.Equal(2, summaryAll.ActiveRecommendations);
            Assert.Equal(3, summaryAll.ActiveFindings);
            var sectionsAll = await recommendations.GetSectionsAsync(clientId);
            Assert.Equal(3, sectionsAll.Sum(s => s.TotalResources));
            Assert.Equal(2, sectionsAll.Sum(s => s.TotalRecs));

            // --- Filtrando por A: solo la recomendación compartida, y con UN recurso (no dos).
            var summaryA = await recommendations.GetSummaryAsync(clientId, subscriptions: [SubA]);
            Assert.Equal(1, summaryA.ActiveRecommendations);
            Assert.Equal(1, summaryA.ActiveFindings);
            var sectionsA = await recommendations.GetSectionsAsync(clientId, [SubA]);
            Assert.Equal(1, sectionsA.Sum(s => s.TotalRecs));
            Assert.Equal(1, sectionsA.Sum(s => s.TotalResources)); // no el resource_count denormalizado (2)

            // --- Filtrando por B: las dos recomendaciones.
            var sectionsB = await recommendations.GetSectionsAsync(clientId, [SubB]);
            Assert.Equal(2, sectionsB.Sum(s => s.TotalRecs));
            Assert.Equal(2, sectionsB.Sum(s => s.TotalResources));

            // --- Selección que no existe: vacío, sin excepción.
            var sectionsNone = await recommendations.GetSectionsAsync(clientId, ["no-existe"]);
            Assert.Equal(0, sectionsNone.Sum(s => s.TotalRecs));
            var summaryNone = await recommendations.GetSummaryAsync(clientId, subscriptions: ["no-existe"]);
            Assert.Equal(0, summaryNone.ActiveRecommendations);

            // --- Las dos juntas equivalen a no filtrar.
            var sectionsBoth = await recommendations.GetSectionsAsync(clientId, [SubA, SubB]);
            Assert.Equal(3, sectionsBoth.Sum(s => s.TotalResources));
        }
        finally
        {
            await clients.DeleteClientCascadeAsync(clientId);
            await DeleteCanonicalsAsync(factory, tag);
        }
    }
}
