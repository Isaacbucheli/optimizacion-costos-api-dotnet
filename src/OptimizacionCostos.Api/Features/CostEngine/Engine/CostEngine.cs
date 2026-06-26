using Microsoft.Extensions.Logging;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>
/// Motor de cálculo de costos. Port 1:1 de cost_engine.py::calculate_analysis.
/// Orquesta: lista servicios del catálogo → expande dependencias → por servicio:
/// carga recursos (JOIN), enriquece (VMs con SQL, discos con VMs apagadas), despacha
/// a la calculadora del CalculatorRegistry, e inserta los cost_results.
/// </summary>
public sealed class CostEngine(
    IServiceCatalog catalog,
    IResourceLoader loader,
    ICostResultStore store,
    IPriceRepository prices,
    IPricingConstants constants,
    ILogger<CostEngine> logger)
{
    /// <summary>Ejecuta el motor y PERSISTE los cost_results. Devuelve el resumen por servicio.</summary>
    public CalculationSummary CalculateAnalysis(
        int analysisId,
        IReadOnlyList<string>? serviceKeys = null,
        int resourceOffset = 0,
        int? resourceLimit = null,
        bool replaceExisting = true)
    {
        var (services, expanded, error) = ResolveServices(serviceKeys);
        if (error is not null)
        {
            return new CalculationSummary { AnalysisId = analysisId, Error = error };
        }

        // Borrado previo: si se pidieron servicios concretos, borra solo esos; si no, todo.
        if (replaceExisting)
        {
            if (expanded is { Count: > 0 })
            {
                foreach (var svc in expanded)
                {
                    store.DeleteForService(analysisId, svc);
                }
            }
            else
            {
                store.DeleteAllForAnalysis(analysisId);
            }
        }

        var summary = new CalculationSummary { AnalysisId = analysisId };
        foreach (var service in OrderedNonInternal(services))
        {
            var svcSummary = RunService(analysisId, service, resourceOffset, resourceLimit, persist: true);
            summary.Summary[service.ServiceKey] = svcSummary;
        }
        return summary;
    }

    /// <summary>
    /// Calcula SIN persistir y devuelve todos los CostResult (para el diff de paridad
    /// contra el Python: lectura no destructiva sobre la BD).
    /// </summary>
    public IReadOnlyList<CostResult> ComputeResults(
        int analysisId,
        IReadOnlyList<string>? serviceKeys = null,
        int resourceOffset = 0,
        int? resourceLimit = null)
    {
        var (services, _, error) = ResolveServices(serviceKeys);
        if (error is not null)
        {
            return [];
        }

        var all = new List<CostResult>();
        foreach (var service in OrderedNonInternal(services))
        {
            all.AddRange(ComputeService(analysisId, service, resourceOffset, resourceLimit));
        }
        return all;
    }

    // -------------------- internos --------------------

    private (IReadOnlyList<ServiceCatalogEntry> Services, IReadOnlyList<string>? Expanded, string? Error)
        ResolveServices(IReadOnlyList<string>? serviceKeys)
    {
        var services = catalog.ListServices(onlyActive: true);
        var expanded = ServiceDependencies.ExpandServiceKeys(serviceKeys);
        if (expanded is { Count: > 0 })
        {
            var set = new HashSet<string>(expanded, StringComparer.Ordinal);
            services = services.Where(s => set.Contains(s.ServiceKey)).ToList();
        }
        if (services.Count == 0)
        {
            return ([], expanded, "No services to calculate");
        }
        return (services, expanded, null);
    }

    private static IEnumerable<ServiceCatalogEntry> OrderedNonInternal(IReadOnlyList<ServiceCatalogEntry> services)
        => services.Where(s => !s.IsInternal).OrderBy(s => s.DisplayOrder ?? 100);

    private ServiceCalculationSummary RunService(
        int analysisId, ServiceCatalogEntry service, int offset, int? limit, bool persist)
    {
        var summary = new ServiceCalculationSummary
        {
            ResourceOffset = Math.Max(0, offset),
            ResourceLimit = limit,
        };

        var calculator = CalculatorRegistry.GetCalculator(service.CalculatorKey, prices, constants);
        if (calculator is null)
        {
            logger.LogWarning("No calculator for key={CalculatorKey} service={ServiceKey}",
                service.CalculatorKey, service.ServiceKey);
            summary.Error = $"Calculator '{service.CalculatorKey}' not registered";
            return summary;
        }

        IReadOnlyList<ResourceRow> resources;
        try
        {
            resources = LoadAndEnrich(analysisId, service, offset, limit);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed loading resources for {ServiceKey}", service.ServiceKey);
            summary.Error = $"Load error: {ex.GetType().Name}";
            return summary;
        }

        IReadOnlyList<CostResult> results;
        try
        {
            results = calculator.Calculate(resources, analysisId);
            if (persist)
            {
                store.Insert(results);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Calculator failed for {ServiceKey}", service.ServiceKey);
            summary.Error = $"Calculator error: {ex.GetType().Name}";
            return summary;
        }

        foreach (var cr in results)
        {
            summary.StatusBreakdown[cr.CalculationStatus] =
                summary.StatusBreakdown.GetValueOrDefault(cr.CalculationStatus) + 1;
        }
        summary.Calculated = results.Count;
        summary.ResourcesLoaded = resources.Count;
        summary.HasMore = limit is not null && resources.Count >= limit;
        return summary;
    }

    private IReadOnlyList<CostResult> ComputeService(
        int analysisId, ServiceCatalogEntry service, int offset, int? limit)
    {
        var calculator = CalculatorRegistry.GetCalculator(service.CalculatorKey, prices, constants);
        if (calculator is null)
        {
            return [];
        }
        var resources = LoadAndEnrich(analysisId, service, offset, limit);
        return calculator.Calculate(resources, analysisId);
    }

    /// <summary>Carga recursos y aplica el enriquecimiento por servicio (vms / disks).</summary>
    private IReadOnlyList<ResourceRow> LoadAndEnrich(
        int analysisId, ServiceCatalogEntry service, int offset, int? limit)
    {
        var resources = loader.LoadResourcesForService(analysisId, service, offset, limit);

        if (service.ServiceKey == "vms")
        {
            var sqlInfo = loader.LoadSqlVmInfo(analysisId);
            if (sqlInfo.Count > 0)
            {
                resources = resources.Select(vm =>
                {
                    var azureId = (vm.GetString("azure_resource_id") ?? "").ToLowerInvariant();
                    if (sqlInfo.TryGetValue(azureId, out var info))
                    {
                        return WithExtras(vm, new Dictionary<string, object?>
                        {
                            ["sql_image_sku"] = info.SqlImageSku,
                            ["sql_license_type"] = info.SqlLicenseType,
                            ["sql_image_offer"] = info.SqlImageOffer,
                        });
                    }
                    return vm;
                }).ToList();
            }
        }
        else if (service.ServiceKey == "disks")
        {
            var stopped = loader.LoadStoppedVmNames(analysisId);
            resources = resources.Select(d => WithExtras(d, new Dictionary<string, object?>
            {
                ["_stopped_vm_names_lower"] = stopped,
            })).ToList();
        }

        return resources;
    }

    /// <summary>Crea una ResourceRow nueva con las claves extra mezcladas (ResourceRow es inmutable).</summary>
    private static ResourceRow WithExtras(ResourceRow row, IReadOnlyDictionary<string, object?> extras)
    {
        var merged = new Dictionary<string, object?>(row.Raw, StringComparer.Ordinal);
        foreach (var kv in extras)
        {
            merged[kv.Key] = kv.Value;
        }
        return new ResourceRow(merged);
    }
}
