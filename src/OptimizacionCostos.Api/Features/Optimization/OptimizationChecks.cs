using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Optimization;

/// <summary>
/// Registro de los 13 checks del barrido. Port 1:1 de app/optimization/checks/*.py + registry.py.
/// Cada check trae su KQL (Resource Graph) y la función que arma los hallazgos (con ahorro estimado).
/// </summary>
public static class OptimizationChecks
{
    // Propiedad (no campo): evaluación diferida para que los campos de los checks ya estén
    // inicializados al acceder (el orden de inicialización de campos estáticos es textual).
    public static IReadOnlyList<CheckDefinition> Registered =>
    [
        OrphanedDisks, OrphanedPublicIps, StoppedNotDeallocatedVms, LongDeallocatedVms,
        EmptyAppServicePlans, LbAppGwNoBackend, StorageNoRetention, OrphanedNics, EmptySubnets, VmsWithoutHa, BasicLoadBalancers,
        OldSnapshots, VmsWithoutAhb,
    ];

    private static Finding F(CheckDefinition c, string subId, RgRow r, string defaultType,
        IReadOnlyDictionary<string, object?> details, double? savings) =>
        new(c.CheckId, c.Category, c.Severity, subId, r.Str("id") ?? "", r.Str("name") ?? "",
            r.Str("type") ?? defaultType, r.Str("location") ?? "", details, savings);

    // -------------------- 1. Discos huérfanos --------------------
    public static readonly CheckDefinition OrphanedDisks = new(
        "orphaned_disks", "Discos administrados no conectados",
        "Discos en estado 'Unattached' que siguen generando costo sin estar en uso.",
        OptCategory.CostWaste, 5, OptSeverity.Medium,
        """
        Resources
        | where type =~ 'microsoft.compute/disks'
        | extend diskState = tostring(properties.diskState)
        | where diskState == 'Unattached' or isnull(managedBy)
        | project id, name, type, location, sku = tostring(sku.name), diskSizeGB = toint(properties.diskSizeGB), diskState, managedBy
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                var state = (r.Str("diskState") ?? "").ToLowerInvariant();
                var managedBy = r.Str("managedBy");
                if (!string.IsNullOrEmpty(state) && state != "unattached" && !string.IsNullOrEmpty(managedBy)) continue;
                var savings = cost.DiskMonthlySavings(r.Str("sku") ?? "", r.Int("diskSizeGB"), r.Str("location") ?? "");
                outl.Add(F(check, subId, r, "microsoft.compute/disks",
                    new Dictionary<string, object?> { ["diskState"] = r.Str("diskState"), ["sku"] = r.Str("sku"), ["diskSizeGB"] = r.Int("diskSizeGB") }, savings));
            }
            return outl;
        });

    // -------------------- 2. Public IPs huérfanas --------------------
    public static readonly CheckDefinition OrphanedPublicIps = new(
        "orphaned_public_ips", "Public IPs sin asociar",
        "Direcciones IP públicas que no están asociadas a ningún recurso y siguen generando costo.",
        OptCategory.CostWaste, 5, OptSeverity.Medium,
        """
        Resources
        | where type =~ 'microsoft.network/publicipaddresses'
        | extend ipConfigId = tostring(properties.ipConfiguration.id)
        | where isempty(ipConfigId)
        | project id, name, type, location, sku = tostring(sku.name), ipConfigId
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if (!string.IsNullOrEmpty(r.Str("ipConfigId"))) continue;
                var savings = cost.PublicIpMonthlySavings(r.Str("sku") ?? "Standard", r.Str("location") ?? "");
                outl.Add(F(check, subId, r, "microsoft.network/publicipaddresses",
                    new Dictionary<string, object?> { ["sku"] = r.Str("sku") }, savings));
            }
            return outl;
        });

    // -------------------- 3. VMs detenidas no desasignadas --------------------
    public static readonly CheckDefinition StoppedNotDeallocatedVms = new(
        "stopped_not_deallocated_vms", "VMs detenidas no desasignadas",
        "VMs en estado 'Stopped' (no 'Deallocated'): siguen reservando y cobrando compute. Conviene desasignarlas.",
        OptCategory.CostWaste, 5, OptSeverity.High,
        """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend powerState = tostring(properties.extended.instanceView.powerState.code)
        | where powerState == 'PowerState/stopped'
        | project id, name, type, location, powerState, vmSize = tostring(properties.hardwareProfile.vmSize), osType = tostring(properties.storageProfile.osDisk.osType)
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if ((r.Str("powerState") ?? "").ToLowerInvariant() != "powerstate/stopped") continue;
                var savings = cost.VmComputeMonthlySavings(r.Str("vmSize") ?? "", r.Str("location") ?? "", r.Str("osType") ?? "Linux");
                outl.Add(F(check, subId, r, "microsoft.compute/virtualmachines",
                    new Dictionary<string, object?> { ["powerState"] = r.Str("powerState"), ["vmSize"] = r.Str("vmSize") }, savings));
            }
            return outl;
        });

    // -------------------- 4. VMs desasignadas (gobernanza) --------------------
    public static readonly CheckDefinition LongDeallocatedVms = new(
        "long_deallocated_vms", "VMs desasignadas (posibles VMs olvidadas)",
        "VMs en estado 'Deallocated': no cobran compute pero suelen dejar discos y recursos asociados. Revisar si deben eliminarse.",
        OptCategory.Governance, 5, OptSeverity.Low,
        """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend powerState = tostring(properties.extended.instanceView.powerState.code)
        | where powerState == 'PowerState/deallocated'
        | project id, name, type, location, powerState
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if ((r.Str("powerState") ?? "").ToLowerInvariant() != "powerstate/deallocated") continue;
                outl.Add(F(check, subId, r, "microsoft.compute/virtualmachines",
                    new Dictionary<string, object?> { ["powerState"] = r.Str("powerState"), ["note"] = "VM desasignada; revisar si sigue siendo necesaria. Discos asociados se reportan por separado." }, null));
            }
            return outl;
        });

    // -------------------- 5. App Service Plans vacíos --------------------
    private static readonly HashSet<string> FreeSkus = new(StringComparer.OrdinalIgnoreCase) { "free", "shared", "f1", "d1" };
    public static readonly CheckDefinition EmptyAppServicePlans = new(
        "empty_app_service_plans", "App Service Plans sin aplicaciones",
        "Planes de App Service sin ninguna app desplegada que siguen generando costo.",
        OptCategory.CostWaste, 5, OptSeverity.Medium,
        """
        Resources
        | where type =~ 'microsoft.web/serverfarms'
        | extend numberOfSites = toint(properties.numberOfSites)
        | where numberOfSites == 0
        | project id, name, type, location, numberOfSites, sku = tostring(sku.name), isLinux = tobool(properties.reserved)
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if ((r.Int("numberOfSites") ?? 0) != 0) continue;
                var sku = r.Str("sku") ?? "";
                var savings = FreeSkus.Contains(sku) ? null : cost.AppServicePlanMonthlySavings(sku, r.Str("location") ?? "", r.BoolFalse("isLinux"));
                outl.Add(F(check, subId, r, "microsoft.web/serverfarms",
                    new Dictionary<string, object?> { ["sku"] = sku, ["numberOfSites"] = 0 }, savings));
            }
            return outl;
        });

    // -------------------- 6. LB / App Gateway sin backend --------------------
    public static readonly CheckDefinition LbAppGwNoBackend = new(
        "lb_appgw_no_backend", "Balanceadores / App Gateways sin backend",
        "Load Balancers o Application Gateways sin pool de backend configurado: configuración incompleta o recurso sin uso.",
        OptCategory.Governance, 5, OptSeverity.Low,
        """
        Resources
        | where type =~ 'microsoft.network/loadbalancers' or type =~ 'microsoft.network/applicationgateways'
        | extend backendCount = iff(type =~ 'microsoft.network/loadbalancers', array_length(properties.backendAddressPools), array_length(properties.backendAddressPools))
        | extend backendCount = toint(coalesce(backendCount, 0))
        | project id, name, type, location, backendCount, sku = tostring(sku.name)
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if ((r.Int("backendCount") ?? 0) > 0) continue;
                outl.Add(F(check, subId, r, "",
                    new Dictionary<string, object?> { ["sku"] = r.Str("sku"), ["backendCount"] = 0 }, null));
            }
            return outl;
        });

    // -------------------- 7. Storage sin retención --------------------
    public static readonly CheckDefinition StorageNoRetention = new(
        "storage_no_retention", "Storage sin política de retención",
        "Cuentas de almacenamiento sin soft-delete/retención de blobs: riesgo de pérdida de datos.",
        OptCategory.Governance, 5, OptSeverity.Low,
        """
        Resources
        | where type =~ 'microsoft.storage/storageaccounts'
        | extend hasRetention = isnotnull(properties.deleteRetentionPolicy) and tobool(properties.deleteRetentionPolicy.enabled)
        | project id, name, type, location, hasRetention
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if (r.BoolFalse("hasRetention")) continue;
                outl.Add(F(check, subId, r, "microsoft.storage/storageaccounts",
                    new Dictionary<string, object?> { ["note"] = "Sin política de retención/soft-delete de blobs detectada. Verificar y habilitar." }, null));
            }
            return outl;
        });

    // -------------------- 8. NICs huérfanas --------------------
    // Fuente: Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit
    public static readonly CheckDefinition OrphanedNics = new(
        "orphaned_nics", "Interfaces de red huérfanas",
        "NICs sin VM, Private Endpoint, Private Link Service ni workload asociado: remanentes que conviene eliminar.",
        OptCategory.Governance, 5, OptSeverity.Low,
        // Fase 2.5: excluir también privateLinkService y hostedWorkloads (NetApp/otros servicios),
        // según la query estándar (dolevshor/azure-orphan-resources) — reduce falsos positivos.
        """
        Resources
        | where type =~ 'microsoft.network/networkinterfaces'
        | extend vmId = tostring(properties.virtualMachine.id), peId = tostring(properties.privateEndpoint.id),
                 plsId = tostring(properties.privateLinkService.id), hostedCount = array_length(properties.hostedWorkloads)
        | where isempty(vmId) and isempty(peId) and isempty(plsId) and (isnull(hostedCount) or hostedCount == 0)
        | project id, name, type, location, vmId, peId, plsId, hostedCount
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if (!string.IsNullOrEmpty(r.Str("vmId")) || !string.IsNullOrEmpty(r.Str("peId"))
                    || !string.IsNullOrEmpty(r.Str("plsId")) || (r.Int("hostedCount") ?? 0) > 0) continue;
                outl.Add(F(check, subId, r, "microsoft.network/networkinterfaces",
                    new Dictionary<string, object?> { ["note"] = "NIC sin VM, Private Endpoint, Private Link Service ni workload. Verificar y eliminar." }, null));
            }
            return outl;
        });

    // -------------------- 9. Subnets vacías --------------------
    // Fuente: Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit
    // Subnets reservadas de infraestructura: aunque estén "vacías" NO son desperdicio (Fase 2.5).
    private static readonly HashSet<string> ReservedSubnetNames = new(StringComparer.OrdinalIgnoreCase)
        { "GatewaySubnet", "AzureBastionSubnet", "AzureFirewallSubnet", "AzureFirewallManagementSubnet", "RouteServerSubnet" };

    public static readonly CheckDefinition EmptySubnets = new(
        "empty_subnets", "Subnets vacías",
        "Subnets sin IP configs, delegaciones, service endpoints ni App Gateway: red reservada sin uso (excluye subnets de infraestructura).",
        OptCategory.Governance, 5, OptSeverity.Low,
        // Fase 2.5: excluir applicationGatewayIPConfigurations y serviceAssociationLinks (subnet delegada a un
        // servicio) + subnets reservadas por nombre. La ausencia de applicationGatewayIPConfigurations era el
        // falso positivo típico (subnets de App Gateway).
        """
        Resources
        | where type =~ 'microsoft.network/virtualnetworks'
        | extend vnetName = name
        | mv-expand subnet = properties.subnets
        | extend ipCount = array_length(subnet.properties.ipConfigurations),
                 delegCount = array_length(subnet.properties.delegations),
                 seCount = array_length(subnet.properties.serviceEndpoints),
                 agwCount = array_length(subnet.properties.applicationGatewayIPConfigurations),
                 salCount = array_length(subnet.properties.serviceAssociationLinks),
                 subnetName = tostring(subnet.name)
        | where (isnull(ipCount) or ipCount == 0) and (isnull(delegCount) or delegCount == 0)
                and (isnull(seCount) or seCount == 0) and (isnull(agwCount) or agwCount == 0)
                and (isnull(salCount) or salCount == 0)
                and subnetName !in~ ('GatewaySubnet', 'AzureBastionSubnet', 'AzureFirewallSubnet', 'AzureFirewallManagementSubnet', 'RouteServerSubnet')
        | project id = tostring(subnet.id), name = subnetName, type = 'microsoft.network/virtualnetworks/subnets', location, vnet = vnetName, ipCount, delegCount, agwCount, salCount
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if ((r.Int("ipCount") ?? 0) > 0 || (r.Int("delegCount") ?? 0) > 0
                    || (r.Int("agwCount") ?? 0) > 0 || (r.Int("salCount") ?? 0) > 0) continue;
                if (ReservedSubnetNames.Contains(r.Str("name") ?? "")) continue;
                outl.Add(F(check, subId, r, "microsoft.network/virtualnetworks/subnets",
                    new Dictionary<string, object?> { ["vnet"] = r.Str("vnet"), ["note"] = "Subnet sin uso." }, null));
            }
            return outl;
        });

    // -------------------- 10. VMs sin alta disponibilidad --------------------
    // Fuente: Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit
    public static readonly CheckDefinition VmsWithoutHa = new(
        "vms_without_ha", "VMs sin alta disponibilidad",
        "VMs sin zona de disponibilidad, availability set ni VMSS: riesgo de indisponibilidad.",
        OptCategory.Governance, 5, OptSeverity.Medium,
        """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend hasZone = isnotnull(zones) and array_length(zones) > 0,
                 hasAvSet = isnotnull(properties.availabilitySet),
                 inVmss = isnotnull(properties.virtualMachineScaleSet)
        | where hasZone == false and hasAvSet == false and inVmss == false
        | project id, name, type, location, vmSize = tostring(properties.hardwareProfile.vmSize), hasZone, hasAvSet, inVmss
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if (r.BoolFalse("hasZone") || r.BoolFalse("hasAvSet") || r.BoolFalse("inVmss")) continue;
                outl.Add(F(check, subId, r, "microsoft.compute/virtualmachines",
                    new Dictionary<string, object?> { ["vmSize"] = r.Str("vmSize"), ["note"] = "Sin zona/availability set/VMSS." }, null));
            }
            return outl;
        });

    // -------------------- 11. Load Balancers Basic --------------------
    // Fuente: Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit
    public static readonly CheckDefinition BasicLoadBalancers = new(
        "basic_load_balancers", "Load Balancers Basic (en retiro)",
        "Load Balancers SKU Basic: el tier se está retirando; planificar migración a Standard.",
        OptCategory.Governance, 5, OptSeverity.Low,
        """
        Resources
        | where type =~ 'microsoft.network/loadbalancers'
        | extend skuName = tostring(sku.name)
        | where tolower(skuName) == 'basic'
        | project id, name, type, location, sku = skuName
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                if (!string.Equals(r.Str("sku"), "Basic", StringComparison.OrdinalIgnoreCase)) continue;
                outl.Add(F(check, subId, r, "microsoft.network/loadbalancers",
                    new Dictionary<string, object?> { ["sku"] = r.Str("sku"), ["note"] = "Basic LB en retiro; migrar a Standard." }, null));
            }
            return outl;
        });

    // -------------------- 12. Snapshots antiguos --------------------
    private const int OldSnapshotDays = 90;
    // Fuente: Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit
    public static readonly CheckDefinition OldSnapshots = new(
        "old_snapshots", "Snapshots antiguos",
        "Snapshots de disco con más de 90 días: revisar y eliminar. Ahorro es cota superior (los snapshots incrementales facturan menos).",
        OptCategory.CostWaste, 5, OptSeverity.Low,
        """
        Resources
        | where type =~ 'microsoft.compute/snapshots'
        | project id, name, type, location, created = tostring(properties.timeCreated),
                  sku = tostring(sku.name), diskSizeGB = toint(properties.diskSizeGB), incremental = tobool(properties.incremental)
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            var threshold = DateTime.UtcNow.AddDays(-OldSnapshotDays);
            foreach (var r in rows)
            {
                var created = ParseUtc(r.Str("created"));
                if (created is null || created.Value > threshold) continue; // sin fecha o reciente → omitir
                var savings = cost.SnapshotMonthlySavings(r.Str("sku"), r.Int("diskSizeGB"), r.Str("location") ?? "");
                outl.Add(F(check, subId, r, "microsoft.compute/snapshots",
                    new Dictionary<string, object?>
                    {
                        ["created"] = r.Str("created"), ["diskSizeGB"] = r.Int("diskSizeGB"),
                        ["sku"] = r.Str("sku"), ["incremental"] = r.BoolN("incremental"),
                        ["note"] = "Ahorro es cota superior; los snapshots incrementales facturan menos.",
                    }, savings));
            }
            return outl;
        });

    private static DateTime? ParseUtc(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    // -------------------- 13. Azure Hybrid Benefit no aplicado --------------------
    // Fuente: Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit
    public static readonly CheckDefinition VmsWithoutAhb = new(
        "vms_without_ahb", "Azure Hybrid Benefit no aplicado",
        "VMs Windows sin Azure Hybrid Benefit (licenseType != 'Windows_Server'): pagan el premium de licencia evitable.",
        OptCategory.CostWaste, 5, OptSeverity.Medium,
        // Fase 2.5: proyectar powerState. Una VM desasignada no incurre costo de licencia ahora → se reporta
        // (gobernanza) pero sin ahorro cuantificado; una encendida/detenida sí paga el premium evitable.
        """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend osType = tostring(properties.storageProfile.osDisk.osType), lic = tostring(properties.licenseType),
                 powerState = tostring(properties.extended.instanceView.powerState.code)
        | where osType =~ 'Windows'
        | where isempty(lic) or (lic !~ 'Windows_Server' and lic !~ 'Windows_Client')
        | project id, name, type, location, vmSize = tostring(properties.hardwareProfile.vmSize), osType, lic, powerState
        """,
        (check, rows, subId, cost) =>
        {
            var outl = new List<Finding>();
            foreach (var r in rows)
            {
                var os = (r.Str("osType") ?? "").ToLowerInvariant();
                var lic = r.Str("lic") ?? "";
                if (os != "windows") continue;
                if (string.Equals(lic, "Windows_Server", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(lic, "Windows_Client", StringComparison.OrdinalIgnoreCase)) continue;
                var deallocated = string.Equals(r.Str("powerState"), "PowerState/deallocated", StringComparison.OrdinalIgnoreCase);
                var savings = deallocated ? (double?)null : cost.AhbMonthlySavings(r.Str("vmSize") ?? "", r.Str("location") ?? "");
                var note = deallocated
                    ? "VM desasignada: sin costo de licencia actual; aplicar Azure Hybrid Benefit antes de encenderla."
                    : "Aplicar Azure Hybrid Benefit.";
                outl.Add(F(check, subId, r, "microsoft.compute/virtualmachines",
                    new Dictionary<string, object?> { ["vmSize"] = r.Str("vmSize"), ["licenseType"] = lic, ["powerState"] = r.Str("powerState"), ["note"] = note }, savings));
            }
            return outl;
        });
}
