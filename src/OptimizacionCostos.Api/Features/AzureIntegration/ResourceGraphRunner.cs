using System.Text.Json.Nodes;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using Azure.ResourceManager.Resources;

namespace OptimizacionCostos.Api.Features.AzureIntegration;

/// <summary>
/// Wrapper para Azure Resource Graph. Port de app/azure_resource_graph.py.
/// Las KQL viven en service_catalog (SQL); este módulo SOLO ejecuta, con paginación.
/// </summary>
public interface IResourceGraphRunner
{
    /// <summary>
    /// Ejecuta una KQL contra Resource Graph sobre las suscripciones dadas, paginando.
    /// Devuelve todas las filas como objetos JSON (un <see cref="JsonNode"/> por fila).
    /// </summary>
    Task<IReadOnlyList<JsonNode>> RunQueryAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, string kql, CancellationToken ct = default);
}

public sealed class ResourceGraphRunner(ILogger<ResourceGraphRunner> logger) : IResourceGraphRunner
{
    private const int PageSize = 1000;
    private const int MaxPages = 20;

    public async Task<IReadOnlyList<JsonNode>> RunQueryAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, string kql, CancellationToken ct = default)
    {
        var armClient = new ArmClient(credential);

        // Resource Graph se consulta a nivel de tenant; las suscripciones del request definen
        // el alcance. Tomamos el primer tenant accesible por la credencial.
        TenantResource? tenant = null;
        await foreach (var t in armClient.GetTenants().GetAllAsync(ct))
        {
            tenant = t;
            break;
        }
        if (tenant is null)
            throw new InvalidOperationException("No hay tenant accesible para la credencial dada.");

        var allRows = new List<JsonNode>();
        string? skipToken = null;
        var page = 0;

        while (true)
        {
            page++;
            var content = new ResourceQueryContent(kql)
            {
                Options = new ResourceQueryRequestOptions
                {
                    Top = PageSize,
                    SkipToken = skipToken,
                    ResultFormat = ResultFormat.ObjectArray,
                },
            };
            foreach (var sub in subscriptions)
                content.Subscriptions.Add(sub);

            ResourceQueryResult result = await tenant.GetResourcesAsync(content, ct);

            var rowsThisPage = 0;
            // Con ResultFormat.ObjectArray, Data es un arreglo JSON de objetos (una fila c/u).
            if (result.Data is not null && JsonNode.Parse(result.Data) is JsonArray arr)
            {
                rowsThisPage = arr.Count;
                foreach (var item in arr)
                    if (item is not null)
                        allRows.Add(item.DeepClone());
            }

            skipToken = result.SkipToken;
            logger.LogInformation(
                "Resource Graph page={Page} rows_this_page={Rows} rows_total={Total} has_more={HasMore}",
                page, rowsThisPage, allRows.Count, string.IsNullOrEmpty(skipToken) ? "no" : "yes");

            if (string.IsNullOrEmpty(skipToken))
                break;
            if (page >= MaxPages)
            {
                logger.LogWarning("Resource Graph pagination limit reached at page={Page}", page);
                break;
            }
        }

        return allRows;
    }
}
