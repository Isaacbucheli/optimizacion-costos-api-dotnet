using OptimizacionCostos.Api.Features.CostEngine.Engine;
using OptimizacionCostos.Api.Features.Inventory;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Inventory;

/// <summary>
/// Guarda contra el doble import de inventario (bug encontrado 2026-08-21).
///
/// El importador solo sabe borrar-e-insertar o insertar; no existe un upsert. Importar dos veces el
/// mismo servicio en el mismo análisis sin reemplazar duplicaba cada recurso, y con él cada fila de
/// cost_results, cada conteo y cada monto. Pasó en dos análisis reales, uno con todo el inventario al
/// doble. La casilla de la interfaz venía desmarcada por defecto y su etiqueta prometía un
/// "actualizar" que el backend nunca implementó.
/// </summary>
public sealed class DuplicateImportGuardTests
{
    private static ServiceCatalogEntry Svc(string key, string resourceType, string? displayName = null)
        => new()
        {
            ServiceKey = key,
            AzureResourceType = resourceType,
            DisplayName = displayName,
            CalculatorKey = key,
            IsActive = true,
        };

    private static readonly ServiceCatalogEntry Vms =
        Svc("vms", "microsoft.compute/virtualmachines", "Virtual Machines");
    private static readonly ServiceCatalogEntry Disks =
        Svc("disks", "microsoft.compute/disks", "Managed Disks");

    private static Dictionary<string, int> Existentes(params (string Type, int Count)[] pares)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, c) in pares) d[t] = c;
        return d;
    }

    // ------------------------------------------------------------------------------------
    // El caso del bug: insertar sobre lo que ya está.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Sin_reemplazar_sobre_un_analisis_con_inventario_se_rechaza()
    {
        var msg = InventoryImportService.DuplicateImportRejection(
            replaceExisting: false,
            services: [Vms, Disks],
            existingCountsByResourceType: Existentes(
                ("microsoft.compute/virtualmachines", 100),
                ("microsoft.compute/disks", 54)));

        Assert.NotNull(msg);
        Assert.Contains("Virtual Machines (100)", msg);
        Assert.Contains("Managed Disks (54)", msg);
        Assert.Contains("Reemplazar el inventario existente", msg);
    }

    [Fact]
    public void Se_rechaza_aunque_solo_choque_uno_de_los_servicios()
    {
        var msg = InventoryImportService.DuplicateImportRejection(
            replaceExisting: false,
            services: [Vms, Disks],
            existingCountsByResourceType: Existentes(("microsoft.compute/disks", 54)));

        Assert.NotNull(msg);
        Assert.Contains("Managed Disks (54)", msg);
        Assert.DoesNotContain("Virtual Machines", msg);
    }

    [Fact]
    public void El_tipo_se_compara_sin_importar_mayusculas()
    {
        // Resource Graph devuelve el tipo en minúsculas; el catálogo puede tenerlo con mayúsculas.
        var msg = InventoryImportService.DuplicateImportRejection(
            replaceExisting: false,
            services: [Svc("vms", "Microsoft.Compute/virtualMachines", "Virtual Machines")],
            existingCountsByResourceType: Existentes(("microsoft.compute/virtualmachines", 7)));

        Assert.NotNull(msg);
        Assert.Contains("Virtual Machines (7)", msg);
    }

    // ------------------------------------------------------------------------------------
    // Casos que deben pasar de largo.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Con_reemplazar_nunca_se_rechaza()
    {
        Assert.Null(InventoryImportService.DuplicateImportRejection(
            replaceExisting: true,
            services: [Vms, Disks],
            existingCountsByResourceType: Existentes(
                ("microsoft.compute/virtualmachines", 100),
                ("microsoft.compute/disks", 54))));
    }

    [Fact]
    public void Sin_reemplazar_sobre_un_analisis_vacio_pasa()
    {
        Assert.Null(InventoryImportService.DuplicateImportRejection(
            replaceExisting: false,
            services: [Vms, Disks],
            existingCountsByResourceType: Existentes()));
    }

    [Fact]
    public void Sin_reemplazar_pasa_si_lo_que_ya_esta_es_de_otro_servicio()
    {
        // Importar discos en un análisis que solo tiene VMs no duplica nada: es el caso legítimo
        // de sumar un servicio nuevo al inventario.
        Assert.Null(InventoryImportService.DuplicateImportRejection(
            replaceExisting: false,
            services: [Disks],
            existingCountsByResourceType: Existentes(("microsoft.compute/virtualmachines", 100))));
    }

    [Fact]
    public void Un_conteo_en_cero_no_cuenta_como_inventario()
    {
        Assert.Null(InventoryImportService.DuplicateImportRejection(
            replaceExisting: false,
            services: [Vms],
            existingCountsByResourceType: Existentes(("microsoft.compute/virtualmachines", 0))));
    }

    [Fact]
    public void Sin_display_name_el_mensaje_usa_el_service_key()
    {
        var msg = InventoryImportService.DuplicateImportRejection(
            replaceExisting: false,
            services: [Svc("snapshots", "microsoft.compute/snapshots")],
            existingCountsByResourceType: Existentes(("microsoft.compute/snapshots", 187)));

        Assert.NotNull(msg);
        Assert.Contains("snapshots (187)", msg);
    }
}
