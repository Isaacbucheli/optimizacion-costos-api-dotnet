using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Engine;
using Xunit;
// El namespace de este test termina en ".CostEngine" y choca con la clase CostEngine; alias para desambiguar.
using EngineCostEngine = OptimizacionCostos.Api.Features.CostEngine.Engine.CostEngine;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests del orquestador (cost_engine.py::calculate_analysis) con fakes de catálogo,
/// loader y store. Las calculadoras y precios son reales (FakePriceRepository), pero
/// aquí solo verificamos la ORQUESTACIÓN: filtrado/orden de servicios, salto de internos,
/// enriquecimiento invocado, persistencia y borrado.
/// </summary>
public sealed class CostEngineTests
{
    [Fact]
    public void CalculateAnalysis_SkipsInternal_OrdersByDisplayOrder_Persists()
    {
        var catalog = new FakeServiceCatalog([
            Svc("public_ip", "public_ip", "microsoft.network/publicipaddresses", "public_ip_details", order: 60),
            Svc("disks", "managed_disk", "microsoft.compute/disks", "disk_details", order: 20),
            Svc("sql_vm", "sql_vm_metadata", "microsoft.sqlvirtualmachine/sqlvirtualmachines", "sql_vm_details", order: 5),
        ]);

        var loader = new FakeResourceLoader();
        loader.Rows["disks"] = [Res.Row(
            ("resource_id", 1), ("resource_name", "disk1"), ("disk_sku", "StandardSSD_LRS"),
            ("disk_size_gb", 128), ("location", "eastus2"))]; // huérfano (sin attached_vm_name)
        loader.Rows["public_ip"] = [Res.Row(
            ("resource_id", 2), ("is_orphan", true), ("sku_name", "Standard"),
            ("allocation_method", "Static"), ("location", "eastus2"))];

        var prices = new FakePriceRepository
        {
            GetDiskPriceFn = (_, _) => 9.99,
            GetPublicIpMonthlyPriceFn = (_, _, _) => 3.65,
        };
        var store = new FakeCostResultStore();
        var engine = new EngineCostEngine(catalog, loader, store, prices, new FakePricingConstants(), NullLogger<EngineCostEngine>.Instance);

        var summary = engine.CalculateAnalysis(99);

        // sql_vm (interno) NO aparece; sí disks y public_ip.
        Assert.True(summary.Summary.ContainsKey("disks"));
        Assert.True(summary.Summary.ContainsKey("public_ip"));
        Assert.False(summary.Summary.ContainsKey("sql_vm"));

        // replaceExisting sin serviceKeys → borra TODO el análisis una vez.
        Assert.Equal(1, store.DeleteAllCount);
        Assert.Empty(store.DeleteForServiceCalls);

        // Se insertaron resultados de ambos servicios (1 cada uno).
        Assert.Equal(1, summary.Summary["disks"].Calculated);
        Assert.Equal(1, summary.Summary["public_ip"].Calculated);
        Assert.Equal(2, store.Inserted.Count);

        // Enriquecimiento de discos: se consultaron las VMs apagadas.
        Assert.True(loader.LoadStoppedVmNamesCalled);

        // Orden de despacho: disks (20) antes que public_ip (60).
        Assert.Equal(new[] { "disks", "public_ip" }, store.InsertOrderByService);
    }

    [Fact]
    public void CalculateAnalysis_WithServiceKeys_DeletesOnlyThose()
    {
        var catalog = new FakeServiceCatalog([
            Svc("public_ip", "public_ip", "microsoft.network/publicipaddresses", "public_ip_details", order: 60),
        ]);
        var loader = new FakeResourceLoader();
        loader.Rows["public_ip"] = [Res.Row(("resource_id", 1), ("is_orphan", true),
            ("sku_name", "Standard"), ("location", "eastus2"))];
        var prices = new FakePriceRepository { GetPublicIpMonthlyPriceFn = (_, _, _) => 3.65 };
        var store = new FakeCostResultStore();
        var engine = new EngineCostEngine(catalog, loader, store, prices, new FakePricingConstants(), NullLogger<EngineCostEngine>.Instance);

        engine.CalculateAnalysis(99, serviceKeys: ["public_ip"]);

        // Con serviceKeys concretos → borra solo esos, NO todo.
        Assert.Equal(0, store.DeleteAllCount);
        Assert.Equal(new[] { "public_ip" }, store.DeleteForServiceCalls.ToArray());
    }

    [Fact]
    public void ComputeResults_DoesNotPersist()
    {
        var catalog = new FakeServiceCatalog([
            Svc("public_ip", "public_ip", "microsoft.network/publicipaddresses", "public_ip_details", order: 60),
        ]);
        var loader = new FakeResourceLoader();
        loader.Rows["public_ip"] = [Res.Row(("resource_id", 1), ("is_orphan", true),
            ("sku_name", "Standard"), ("location", "eastus2"))];
        var prices = new FakePriceRepository { GetPublicIpMonthlyPriceFn = (_, _, _) => 3.65 };
        var store = new FakeCostResultStore();
        var engine = new EngineCostEngine(catalog, loader, store, prices, new FakePricingConstants(), NullLogger<EngineCostEngine>.Instance);

        var results = engine.ComputeResults(99);

        Assert.Single(results);
        Assert.Equal(3.65, results[0].PaygMonthly!.Value, 5);
        // No persiste ni borra.
        Assert.Empty(store.Inserted);
        Assert.Equal(0, store.DeleteAllCount);
    }

    private static ServiceCatalogEntry Svc(string key, string calc, string type, string detail, int order)
        => new()
        {
            ServiceKey = key, CalculatorKey = calc, AzureResourceType = type,
            DetailTableName = detail, DisplayOrder = order, IsActive = true,
        };
}
