using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Inventario vivo para el informe mensual (Resource Graph + ARM). Port 1:1 de
/// app/reports/azure_inventory.py. NO mejora: replica KQL, api-versions, normalizaciones,
/// nombres de campos, orden y degradación.
/// </summary>
public sealed class ReportInventory(
    IResourceGraphRunner graph,
    IHttpClientFactory httpFactory,
    ILogger<ReportInventory> logger) : IReportInventory
{
    private const string ArmBase = "https://management.azure.com";

    // ---------------------------------------------------------------- constantes (port directo)

    private static readonly IReadOnlyDictionary<string, string> LocationPretty = new Dictionary<string, string>
    {
        ["eastus2"] = "East US 2", ["eastus"] = "East US", ["centralus"] = "Central US",
        ["westus"] = "West US", ["westus2"] = "West US 2", ["westus3"] = "West US 3",
        ["southcentralus"] = "South Central US", ["northcentralus"] = "North Central US",
    };

    private static readonly IReadOnlyDictionary<string, string> BackupMgmtLabels = new Dictionary<string, string>
    {
        ["AzureIaasVM"] = "Azure VM",
        ["AzureWorkload"] = "SQL/Workload en VM",
        ["AzureStorage"] = "Azure File Share",
        ["AzureSql"] = "Azure SQL",
        ["MAB"] = "Agente MARS",
    };

    private static readonly IReadOnlyDictionary<string, string> BackupStatusLabels = new Dictionary<string, string>
    {
        ["Completed"] = "Completado",
        ["CompletedWithWarnings"] = "Completado con advertencias",
        ["Failed"] = "Fallido",
        ["InProgress"] = "En progreso",
    };

    // Lista ORDENADA (orden preservado, como en Python) de (tipo, etiqueta).
    private static readonly (string Type, string Label)[] ServiceStatusTypes =
    [
        ("microsoft.sql/managedinstances", "SQL Managed Instance"),
        ("microsoft.sql/servers", "SQL Server (PaaS)"),
        ("microsoft.dbforpostgresql/flexibleservers", "PostgreSQL"),
        ("microsoft.dbformysql/flexibleservers", "MySQL"),
        ("microsoft.documentdb/databaseaccounts", "Cosmos DB"),
        ("microsoft.web/serverfarms", "App Service Plan"),
        ("microsoft.web/sites", "App Service / Function"),
        ("microsoft.datafactory/factories", "Azure Data Factory"),
        ("microsoft.containerservice/managedclusters", "AKS (Kubernetes)"),
        ("microsoft.containerregistry/registries", "Container Registry"),
        ("microsoft.databricks/workspaces", "Azure Databricks"),
        ("microsoft.synapse/workspaces", "Synapse Analytics"),
        ("microsoft.logic/workflows", "Logic Apps"),
        ("microsoft.keyvault/vaults", "Key Vault"),
        ("microsoft.storage/storageaccounts", "Storage Account"),
        ("microsoft.network/expressroutecircuits", "Express Route"),
        ("microsoft.network/virtualnetworkgateways", "Gateway de red (VPN/ER)"),
        ("microsoft.network/applicationgateways", "Application Gateway"),
        ("microsoft.network/azurefirewalls", "Azure Firewall"),
        ("microsoft.cdn/profiles", "Front Door / CDN"),
        ("microsoft.recoveryservices/vaults", "Recovery Services Vault"),
        ("microsoft.network/virtualnetworks", "Red virtual"),
        ("microsoft.network/publicipaddresses", "IP publica"),
    ];

    private const string VmInventoryKql = """
Resources
| where type =~ 'microsoft.compute/virtualmachines'
| extend nicId = tolower(tostring(properties.networkProfile.networkInterfaces[0].id))
| extend osType = tostring(properties.storageProfile.osDisk.osType)
| extend vmSize = tostring(properties.hardwareProfile.vmSize)
| extend powerState = tostring(properties.extended.instanceView.powerState.displayStatus)
| extend dataDisks = array_length(properties.storageProfile.dataDisks)
| join kind=leftouter (
    Resources
    | where type =~ 'microsoft.network/networkinterfaces'
    | mv-expand ipconfig = properties.ipConfigurations
    | extend ip = tostring(ipconfig.properties.privateIPAddress)
    | extend primary = tobool(ipconfig.properties.primary)
    | where isnotempty(ip)
    | summarize ip = take_anyif(ip, primary == true), ipAny = take_any(ip) by nicId = tolower(tostring(id))
    | extend ip = iff(isempty(ip), ipAny, ip)
    | project nicId, ip
  ) on nicId
| project id, name, ip, subscriptionId, resourceGroup, location, powerState, osType, vmSize, dataDisks
""";

    private const string HealthKql = """
healthresources
| where type =~ 'microsoft.resourcehealth/availabilitystatuses'
| extend tgt = tolower(tostring(properties.targetResourceId)),
         state = tostring(properties.availabilityState)
| project tgt, state
""";

    private const string BackupItemsKql = """
recoveryservicesresources
| where type =~ 'microsoft.recoveryservices/vaults/backupfabrics/protectioncontainers/protecteditems'
| project servidor = tostring(properties.friendlyName),
          mgmt = tostring(properties.backupManagementType),
          policy = tostring(properties.policyName),
          freq = tostring(properties.configuredRPGenerationFrequency),
          reten = tostring(properties.configuredMaximumRetention),
          lastStatus = tostring(properties.lastBackupStatus),
          health = tostring(properties.healthStatus),
          lastTime = tostring(properties.lastBackupTime),
          vaultId = tolower(tostring(properties.vaultId)),
          subscriptionId,
          resourceGroup
""";

    private const string VaultsKql = """
resources
| where type =~ 'microsoft.recoveryservices/vaults'
| project vaultId = tolower(id), name, location
""";

    private const string AppPlansKql = """
resources
| where type =~ 'microsoft.web/serverfarms'
| project id, plan = name,
          sku = strcat(tostring(sku.name), ' / ', tostring(sku.tier)),
          instancias = tostring(toint(sku.capacity)),
          apps = tostring(toint(properties.numberOfSites)),
          estado = tostring(properties.status)
""";

    private const string ConnectivityErKql = """
resources
| where type =~ 'microsoft.network/expressroutecircuits'
| project nombre = name,
          proveedor = tostring(properties.serviceProviderProperties.serviceProviderName),
          ancho = strcat(tostring(properties.serviceProviderProperties.bandwidthInMbps), ' Mbps'),
          estado = tostring(properties.serviceProviderProvisioningState),
          obs = strcat('Peering: ', tostring(properties.serviceProviderProperties.peeringLocation))
""";

    private const string ConnectivityVpnKql = """
resources
| where type =~ 'microsoft.network/virtualnetworkgateways'
| project nombre = name,
          proveedor = tostring(properties.gatewayType),
          ancho = tostring(properties.sku.name),
          estado = tostring(properties.provisioningState),
          obs = strcat('Tipo: ', tostring(properties.gatewayType), ' / ', tostring(properties.vpnType))
""";

    private const string SqlMiKql = """
resources
| where type =~ 'microsoft.sql/managedinstances'
| project id, name, tier = tostring(sku.tier), vcores = toint(properties.vCores),
          storGB = toint(properties.storageSizeInGB), state = tostring(properties.state)
""";

    private const string SqlMiDbsKql = """
resources
| where type =~ 'microsoft.sql/managedinstances/databases'
| extend mi = tostring(split(id, '/databases/')[0])
| summarize bases = count() by mi
| project miName = tostring(split(mi, '/managedInstances/')[1]), bases
""";

    // Requiere Microsoft.Authorization/roleAssignments/read (rol Reader u Owner;
    // Contributor NO lo incluye). Resource Graph filtra por permisos: sin el
    // permiso la query devuelve 0 filas en lugar de fallar.
    private const string RoleAssignmentsKql = """
authorizationresources
| where type =~ 'microsoft.authorization/roleassignments'
| extend scope = tostring(properties.scope)
| where scope !contains '/resourceGroups/'
| extend roleDefId = tolower(tostring(properties.roleDefinitionId))
| extend roleDefParts = split(roleDefId, '/')
| extend roleGuid = tostring(roleDefParts[array_length(roleDefParts) - 1])
| extend principalId = tostring(properties.principalId),
         principalType = tostring(properties.principalType)
| join kind=leftouter (
    authorizationresources
    | where type =~ 'microsoft.authorization/roledefinitions'
    | extend defParts = split(tolower(id), '/')
    | extend roleGuid = tostring(defParts[array_length(defParts) - 1]),
             roleName = tostring(properties.roleName),
             roleType = tostring(properties.type)
    | distinct roleGuid, roleName, roleType
  ) on roleGuid
| project principalId, principalType, roleName, roleType, scope, subscriptionId
""";

    // Roles con permisos de escritura / gestion de acceso considerados "elevados".
    private static readonly HashSet<string> ElevatedRoles = new(StringComparer.Ordinal)
    {
        "owner",
        "contributor",
        "user access administrator",
        "role based access control administrator",
        "key vault administrator",
        "key vault certificates officer",
        "key vault crypto officer",
        "key vault secrets officer",
        "storage account contributor",
        "storage blob data contributor",
        "storage blob data owner",
        "network contributor",
        "virtual machine contributor",
        "sql db contributor",
        "sql server contributor",
        "sql managed instance contributor",
        "monitoring contributor",
        "log analytics contributor",
        "automation contributor",
        "backup contributor",
        "site recovery contributor",
        "managed identity contributor",
        "azure kubernetes service contributor role",
        "azure kubernetes service rbac admin",
        "azure kubernetes service rbac cluster admin",
        "container registry contributor",
        "cosmosdb operator",
        "documentdb account contributor",
        "security admin",
        "security operator",
        "data factory contributor",
        "api management service contributor",
        "logic app contributor",
        "redis cache contributor",
        "dns zone contributor",
        "app configuration data owner",
        "service bus data owner",
        "event hubs data owner",
        "azure event hubs data owner",
    };

    // ---------------------------------------------------------------- helpers puros (port)

    /// <summary>Port de pretty_location.</summary>
    internal static string PrettyLocation(string? loc) =>
        LocationPretty.TryGetValue((loc ?? "").ToLowerInvariant(), out var pretty) ? pretty : (loc ?? "");

    /// <summary>Port de _map_power_state.</summary>
    internal static string MapPowerState(string? displayStatus)
    {
        var status = (displayStatus ?? "").ToLowerInvariant();
        if (status.Contains("running")) return "Running";
        if (status.Contains("deallocat")) return "Stopped (deallocated)";
        if (status.Contains("stopped")) return "Stopped";
        return string.IsNullOrEmpty(displayStatus) ? "Unknown" : displayStatus;
    }

    /// <summary>Port de _format_duration: ISO/TimeSpan de backup ('P30D', '1.00:00:00').</summary>
    internal static string FormatDuration(string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0) return "";
        var upper = raw.ToUpperInvariant();
        try
        {
            if (upper.StartsWith('P') && upper.EndsWith('D') && IsAllDigits(upper[1..^1]))
                return $"{int.Parse(upper[1..^1], CultureInfo.InvariantCulture)} dia(s)";
            if (upper.StartsWith("PT") && upper.EndsWith('H') && IsAllDigits(upper[2..^1]))
                return $"cada {int.Parse(upper[2..^1], CultureInfo.InvariantCulture)} h";
            if (raw.Contains('.') && raw.Contains(':'))
            {
                var days = int.Parse(raw.Split('.')[0], CultureInfo.InvariantCulture);
                if (days >= 1) return $"{days} dia(s)";
            }
            if (raw.Contains(':'))
            {
                var hours = int.Parse(raw.Split(':')[0], CultureInfo.InvariantCulture);
                if (hours >= 1) return $"cada {hours} h";
            }
        }
        catch (FormatException)
        {
            // Python: except ValueError: pass
        }
        catch (OverflowException)
        {
            // int() de Python no desborda; aquí toleramos por seguridad y caemos al raw.
        }
        return raw;
    }

    // Python str.isdigit() sobre el contenido entre P..D / PT..H. Cadena vacía → false (como isdigit()).
    private static bool IsAllDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    /// <summary>Port de _is_elevated.</summary>
    internal static bool IsElevated(string? roleName, string? roleType)
    {
        if ((roleType ?? "").ToLowerInvariant() == "customrole") return true;
        return ElevatedRoles.Contains((roleName ?? "").ToLowerInvariant());
    }

    /// <summary>Port de _scope_level.</summary>
    internal static string ScopeLevel(string? scope)
    {
        var value = (scope ?? "").TrimEnd('/');
        if (value.Length == 0) return "root";
        if (value.ToLowerInvariant().Contains("/providers/microsoft.management/managementgroups/"))
            return "managementgroup";
        var parts = value.Split('/');
        return parts.Length <= 3 ? "subscription" : "resourcegroup";
    }

    // ---------------------------------------------------------------- fetchers

    public async Task<IReadOnlyList<VmInventoryItem>> FetchVmInventoryAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions,
        IReadOnlyDictionary<string, string> subNameMap, CancellationToken ct = default)
    {
        var rows = await graph.RunQueryAsync(credential, subscriptions, VmInventoryKql, ct);
        var inventory = new List<VmInventoryItem>();
        foreach (var node in rows)
        {
            var row = new RgRow(node);
            var subId = row.Str("subscriptionId");
            inventory.Add(new VmInventoryItem(
                Id: (row.Str("id") ?? "").ToLowerInvariant(),
                Name: row.Str("name"),
                Ip: row.Str("ip") ?? "",
                Subscription: SubName(subNameMap, subId),
                ResourceGroup: row.Str("resourceGroup") ?? "",
                Location: PrettyLocation(row.Str("location")),
                LocationRaw: (row.Str("location") ?? "").ToLowerInvariant(),
                Status: MapPowerState(row.Str("powerState")),
                Os: row.Str("osType") ?? "",
                Size: row.Str("vmSize") ?? "",
                Disks: (row.Int("dataDisks") ?? 0) + 1));
        }
        inventory.Sort((a, b) => string.CompareOrdinal(
            (a.Name ?? "").ToLowerInvariant(), (b.Name ?? "").ToLowerInvariant()));
        return inventory;
    }

    public async Task<IReadOnlyDictionary<string, VmSizeInfo>> FetchVmSizesAsync(
        string token, IReadOnlyList<string> subscriptions, IReadOnlyList<string> locations,
        CancellationToken ct = default)
    {
        var sizes = new Dictionary<string, VmSizeInfo>();
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        foreach (var subscription in subscriptions)
        {
            foreach (var location in locations)
            {
                JsonElement root;
                try
                {
                    var url = $"{ArmBase}/subscriptions/{subscription}/providers/"
                            + $"Microsoft.Compute/locations/{location}/vmSizes?api-version=2024-07-01";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var resp = await http.SendAsync(req, ct);
                    resp.EnsureSuccessStatusCode();
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    root = JsonDocument.Parse(body).RootElement.Clone();
                }
                catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or JsonException)
                {
                    logger.LogWarning("vmSizes no disponible para {Sub}/{Loc}: {Type}",
                        subscription, location, exc.GetType().Name);
                    continue;
                }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in value.EnumerateArray())
                    {
                        var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString() : null;
                        if (!string.IsNullOrEmpty(name) && !sizes.ContainsKey(name))
                        {
                            int? cores = item.TryGetProperty("numberOfCores", out var c)
                                && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : null;
                            double memMb = item.TryGetProperty("memoryInMB", out var m)
                                && m.ValueKind == JsonValueKind.Number && m.TryGetDouble(out var md) ? md : 0;
                            sizes[name] = new VmSizeInfo(cores, Math.Round(memMb / 1024.0, 1));
                        }
                    }
                }
            }
            if (sizes.Count > 0)
                // Con una suscripcion suele bastar: los tamanos son globales por region.
                break;
        }
        return sizes;
    }

    public async Task<IReadOnlyDictionary<string, string>> FetchResourceHealthAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default)
    {
        IReadOnlyList<JsonNode> rows;
        try
        {
            rows = await graph.RunQueryAsync(credential, subscriptions, HealthKql, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo consultar Resource Health");
            return new Dictionary<string, string>();
        }
        var map = new Dictionary<string, string>();
        foreach (var node in rows)
        {
            var row = new RgRow(node);
            var tgt = row.Str("tgt");
            if (!string.IsNullOrEmpty(tgt))
                map[tgt.ToLowerInvariant()] = row.Str("state") ?? "";
        }
        return map;
    }

    public async Task<IReadOnlyList<ServiceStatusItem>> FetchServiceStatusAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default)
    {
        var typeList = string.Join(",", ServiceStatusTypes.Select(t => $"'{t.Type}'"));
        var kql = $"""
resources
| where type in~ ({typeList})
| extend prov = tostring(properties.provisioningState), st = tostring(properties.state)
| summarize total = count(),
            ok = countif(prov =~ 'Succeeded' or st in~ ('Ready','Running','Available','Online','Active'))
  by type
""";
        var rows = await graph.RunQueryAsync(credential, subscriptions, kql, ct);
        var byType = new Dictionary<string, RgRow>();
        foreach (var node in rows)
        {
            var row = new RgRow(node);
            byType[(row.Str("type") ?? "").ToLowerInvariant()] = row;
        }
        var status = new List<ServiceStatusItem>();
        foreach (var (typeName, label) in ServiceStatusTypes)
        {
            if (!byType.TryGetValue(typeName, out var row)) continue;
            var total = row.Int("total") ?? 0;
            if (total <= 0) continue;
            var ok = row.Int("ok") ?? 0;
            var alert = total - ok;
            status.Add(new ServiceStatusItem(
                Tipo: label,
                Total: total,
                Disponibles: ok,
                ConAlerta: alert,
                Obs: alert > 0 ? "Revisar recursos no disponibles" : "Operativos"));
        }
        return status;
    }

    public async Task<BackupResult> FetchBackupsAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, DateTime start, DateTime end,
        IReadOnlyDictionary<string, string>? subNameMap = null, CancellationToken ct = default)
    {
        var itemsRaw = await graph.RunQueryAsync(credential, subscriptions, BackupItemsKql, ct);
        // Mantenemos el vaultId por item para agrupar luego (campo "_vault_id" en Python, no se expone).
        var itemsWithVault = new List<(BackupItem Item, string VaultId)>();
        foreach (var node in itemsRaw)
        {
            var row = new RgRow(node);
            var statusRaw = row.Str("lastStatus") ?? "";
            var status = BackupStatusLabels.TryGetValue(statusRaw, out var sl) ? sl : statusRaw;
            var health = row.Str("health") ?? "";
            if (!string.IsNullOrEmpty(health) && health != "Passed")
                status = !string.IsNullOrEmpty(status) ? $"{status} ({health})" : health;
            var subId = row.Str("subscriptionId") ?? "";
            itemsWithVault.Add((new BackupItem(
                Servidor: row.Str("servidor"),
                Tipo: BackupMgmtLabels.TryGetValue(row.Str("mgmt") ?? "", out var ml) ? ml : (row.Str("mgmt") ?? ""),
                Politica: row.Str("policy") ?? "",
                Frecuencia: FormatDuration(row.Str("freq")),
                Retencion: FormatDuration(row.Str("reten")),
                Estado: status,
                Ultimo: First16ReplaceT(row.Str("lastTime") ?? ""),
                Subscription: SubName(subNameMap, subId),
                ResourceGroup: row.Str("resourceGroup") ?? ""),
                (row.Str("vaultId") ?? "").ToLowerInvariant()));
        }
        itemsWithVault.Sort((a, b) => string.CompareOrdinal(
            (a.Item.Servidor ?? "").ToLowerInvariant(), (b.Item.Servidor ?? "").ToLowerInvariant()));

        var vaultRows = await graph.RunQueryAsync(credential, subscriptions, VaultsKql, ct);
        var vaults = new List<BackupVault>();
        foreach (var node in vaultRows)
        {
            var vrow = new RgRow(node);
            var vaultId = (vrow.Str("vaultId") ?? "").ToLowerInvariant();
            var vaultItems = itemsWithVault.Where(x => x.VaultId == vaultId).Select(x => x.Item).ToList();
            // sorted(set(...)) → distintas, ordenadas ordinalmente.
            var policies = vaultItems.Where(i => !string.IsNullOrEmpty(i.Politica))
                .Select(i => i.Politica).Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            var lastTimes = vaultItems.Where(i => !string.IsNullOrEmpty(i.Ultimo))
                .Select(i => i.Ultimo).OrderBy(t => t, StringComparer.Ordinal).ToList();
            vaults.Add(new BackupVault(
                Vault: vrow.Str("name"),
                Region: PrettyLocation(vrow.Str("location")),
                Elementos: vaultItems.Count,
                Politicas: policies.Count == 1 ? policies[0]
                    : (policies.Count > 0 ? $"Varias ({policies.Count})" : "-"),
                Ultimo: lastTimes.Count > 0 ? lastTimes[^1] : "-"));
        }

        var d0 = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var d1 = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var jobsKql = $"""
recoveryservicesresources
| where type =~ 'microsoft.recoveryservices/vaults/backupjobs'
| extend st = tostring(properties.status), start = todatetime(properties.startTime)
| where start >= datetime({d0}) and start < datetime({d1})
| summarize n = count() by st
""";
        IReadOnlyList<JsonNode> jobRows;
        try
        {
            jobRows = await graph.RunQueryAsync(credential, subscriptions, jobsKql, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron consultar backup jobs");
            jobRows = [];
        }
        var jobs = new Dictionary<string, int>();
        foreach (var node in jobRows)
        {
            var row = new RgRow(node);
            var st = row.Str("st") ?? "";
            var label = BackupStatusLabels.TryGetValue(st, out var sl) ? sl : st;
            jobs[label] = row.Int("n") ?? 0;
        }

        return new BackupResult(vaults, itemsWithVault.Select(x => x.Item).ToList(), jobs);
    }

    public async Task<IReadOnlyList<AppServicePlanItem>> FetchAppServicePlansAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default)
    {
        var rows = await graph.RunQueryAsync(credential, subscriptions, AppPlansKql, ct);
        return rows.Select(node =>
        {
            var row = new RgRow(node);
            return new AppServicePlanItem(
                Id: (row.Str("id") ?? "").ToLowerInvariant(),
                Plan: row.Str("plan"),
                Sku: row.Str("sku"),
                Instancias: row.Str("instancias"),
                Apps: row.Str("apps"),
                Estado: row.Str("estado"));
        }).ToList();
    }

    public async Task<IReadOnlyList<ConnectivityItem>> FetchConnectivityAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default)
    {
        var rows = new List<JsonNode>(await graph.RunQueryAsync(credential, subscriptions, ConnectivityErKql, ct));
        rows.AddRange(await graph.RunQueryAsync(credential, subscriptions, ConnectivityVpnKql, ct));
        return rows.Select(node =>
        {
            var row = new RgRow(node);
            return new ConnectivityItem(
                Nombre: row.Str("nombre"),
                Proveedor: row.Str("proveedor"),
                Ancho: row.Str("ancho"),
                Estado: row.Str("estado"),
                Obs: row.Str("obs"));
        }).ToList();
    }

    public async Task<IReadOnlyList<SqlMiItem>> FetchSqlMiInventoryAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default)
    {
        var instances = await graph.RunQueryAsync(credential, subscriptions, SqlMiKql, ct);
        var dbRows = await graph.RunQueryAsync(credential, subscriptions, SqlMiDbsKql, ct);
        var dbsByMi = new Dictionary<string, int>();
        foreach (var node in dbRows)
        {
            var row = new RgRow(node);
            var miName = row.Str("miName");
            if (miName is not null) dbsByMi[miName] = row.Int("bases") ?? 0;
        }
        return instances.Select(node =>
        {
            var row = new RgRow(node);
            var name = row.Str("name");
            return new SqlMiItem(
                Id: (row.Str("id") ?? "").ToLowerInvariant(),
                Name: name,
                Tier: row.Str("tier"),
                Vcores: row.Int("vcores"),
                StorageGb: row.Int("storGB"),
                Bases: name is not null && dbsByMi.TryGetValue(name, out var b) ? b : 0,
                Estado: row.Str("state"));
        }).ToList();
    }

    public async Task<IReadOnlyDictionary<string, object?>> FetchRoleAssignmentsAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions,
        IReadOnlyDictionary<string, string> subNameMap, CancellationToken ct = default)
    {
        IReadOnlyList<JsonNode> rows;
        try
        {
            rows = await graph.RunQueryAsync(credential, subscriptions, RoleAssignmentsKql, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron consultar role assignments");
            return new Dictionary<string, object?>();
        }
        logger.LogInformation("Role assignments RBAC: {Count} filas devueltas por Resource Graph", rows.Count);
        if (rows.Count == 0) return new Dictionary<string, object?>();

        var principalsSeen = new HashSet<string>();
        var rootPrincipals = new HashSet<string>();
        int total = 0, elevados = 0, owners = 0, uaa = 0, users = 0, sps = 0, groups = 0;

        // por suscripcion: sub_id -> dict (orden de inserción, como Python dict).
        var bySub = new Dictionary<string, Dictionary<string, object?>>();
        var bySubOrder = new List<string>();
        // por rol: role_name -> dict.
        var byRole = new Dictionary<string, Dictionary<string, object?>>();
        var byRoleOrder = new List<string>();

        foreach (var node in rows)
        {
            var row = new RgRow(node);
            var principal = row.Str("principalId") ?? "";
            if (string.IsNullOrEmpty(principal)) continue;
            var role = row.Str("roleName") ?? "-";
            var roleType = row.Str("roleType") ?? "";
            var pType = (row.Str("principalType") ?? "").ToLowerInvariant();
            var scope = row.Str("scope") ?? "";
            var subId = row.Str("subscriptionId") ?? "";
            var elevated = IsElevated(role, roleType);
            var scopeLvl = ScopeLevel(scope);
            var roleLower = role.ToLowerInvariant();

            total++;
            if (elevated) elevados++;
            if (roleLower == "owner") owners++;
            if (roleLower == "user access administrator") uaa++;
            if (pType == "user") users++;
            else if (pType is "serviceprincipal" or "application") sps++;
            else if (pType == "group") groups++;
            principalsSeen.Add(principal);
            if (scopeLvl is "root" or "managementgroup") rootPrincipals.Add(principal);

            // por suscripcion
            var subName = subNameMap.TryGetValue(subId, out var sn) ? sn : (string.IsNullOrEmpty(subId) ? "Desconocido" : subId);
            if (!bySub.TryGetValue(subId, out var entry))
            {
                entry = bySub[subId] = new Dictionary<string, object?>
                {
                    ["suscripcion"] = subName, ["total"] = 0, ["elevados"] = 0,
                    ["owners"] = 0, ["contributors"] = 0, ["uaa"] = 0,
                    ["usuarios"] = 0, ["service_principals"] = 0, ["grupos"] = 0,
                };
                bySubOrder.Add(subId);
            }
            entry["total"] = (int)entry["total"]! + 1;
            if (elevated) entry["elevados"] = (int)entry["elevados"]! + 1;
            if (roleLower == "owner") entry["owners"] = (int)entry["owners"]! + 1;
            if (roleLower == "contributor") entry["contributors"] = (int)entry["contributors"]! + 1;
            if (roleLower == "user access administrator") entry["uaa"] = (int)entry["uaa"]! + 1;
            if (pType == "user") entry["usuarios"] = (int)entry["usuarios"]! + 1;
            else if (pType is "serviceprincipal" or "application") entry["service_principals"] = (int)entry["service_principals"]! + 1;
            else if (pType == "group") entry["grupos"] = (int)entry["grupos"]! + 1;

            // por rol (top roles)
            if (!byRole.TryGetValue(role, out var roleEntry))
            {
                roleEntry = byRole[role] = new Dictionary<string, object?>
                {
                    ["rol"] = role,
                    ["elevado"] = elevated,
                    ["usuarios"] = 0, ["service_principals"] = 0, ["grupos"] = 0, ["total"] = 0,
                };
                byRoleOrder.Add(role);
            }
            roleEntry["total"] = (int)roleEntry["total"]! + 1;
            if (pType == "user") roleEntry["usuarios"] = (int)roleEntry["usuarios"]! + 1;
            else if (pType is "serviceprincipal" or "application") roleEntry["service_principals"] = (int)roleEntry["service_principals"]! + 1;
            else if (pType == "group") roleEntry["grupos"] = (int)roleEntry["grupos"]! + 1;
        }

        // sorted por -total; OrderByDescending es estable → conserva orden de inserción ante empates,
        // igual que el sort estable de Python.
        var topRoles = byRoleOrder.Select(k => byRole[k])
            .OrderByDescending(r => (int)r["total"]!)
            .Take(15).ToList();
        var porSub = bySubOrder.Select(k => bySub[k])
            .OrderByDescending(s => (int)s["total"]!)
            .ToList();
        var pctElev = total > 0 ? Math.Round((double)elevados / total * 100, 1) : 0.0;

        return new Dictionary<string, object?>
        {
            ["resumen"] = new Dictionary<string, object?>
            {
                ["total"] = total,
                ["elevados"] = elevados,
                ["pct_elevados"] = pctElev,
                ["owners"] = owners,
                ["root_tenant"] = rootPrincipals.Count,
                ["user_access_admin"] = uaa,
                ["usuarios"] = users,
                ["service_principals"] = sps,
                ["grupos"] = groups,
                ["cuentas_unicas"] = principalsSeen.Count,
            },
            ["top_roles"] = topRoles,
            ["por_suscripcion"] = porSub,
        };
    }

    public async Task<IReadOnlyList<ServiceHealthEvent>> FetchServiceHealthEventsAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, IReadOnlyList<string> regions,
        DateTime start, DateTime end, CancellationToken ct = default)
    {
        if (regions.Count == 0) return [];
        var regionList = string.Join(",", regions.Select(r => "'" + r.Replace("'", "''") + "'"));
        var d0 = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var d1 = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var kql = $"""
servicehealthresources
| where tostring(properties.EventType) == 'ServiceIssue'
| extend start = todatetime(properties.ImpactStartTime), tk = tostring(properties.TrackingId)
| where start >= datetime({d0}) and start < datetime({d1})
| mv-expand imp = properties.Impact
| mv-expand rgn = imp.ImpactedRegions
| extend region = tostring(rgn.ImpactedRegion), service = tostring(imp.ImpactedService)
| where region in ({regionList})
| extend title = tostring(properties.Title),
         endt = todatetime(properties.ImpactMitigationTime)
| summarize servicios = make_set(service), regiones = make_set(region),
            inicio = min(start), fin = max(endt), causa = take_any(title)
  by tk
""";
        IReadOnlyList<JsonNode> rows;
        try
        {
            rows = await graph.RunQueryAsync(credential, subscriptions, kql, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron consultar eventos de Service Health");
            return [];
        }
        var events = new List<ServiceHealthEvent>();
        foreach (var node in rows)
        {
            var row = new RgRow(node);
            var services = row.StrList("servicios");
            var eventRegions = row.StrList("regiones");
            var finRaw = First16ReplaceT(row.Str("fin") ?? "");
            events.Add(new ServiceHealthEvent(
                Inicio: First16ReplaceT(row.Str("inicio") ?? ""),
                Fin: finRaw.Length > 0 ? finRaw : "En curso",
                Servicios: string.Join(", ", services) + " (" +
                    string.Join(", ", eventRegions.Select(PrettyLocation)) + ")",
                Causa: row.Str("causa") ?? ""));
        }
        events.Sort((a, b) => string.CompareOrdinal(a.Inicio, b.Inicio));
        return events;
    }

    // ---------------------------------------------------------------- helpers internos

    /// <summary>Port de sub_name_map.get(sub_id, sub_id): nombre si existe, si no el propio id.</summary>
    private static string? SubName(IReadOnlyDictionary<string, string>? map, string? subId)
    {
        if (subId is null) return null;
        return map is not null && map.TryGetValue(subId, out var name) ? name : subId;
    }

    /// <summary>Port de (s or "")[:16].replace("T", " "): primeros 16 chars, T→espacio.</summary>
    private static string First16ReplaceT(string s)
    {
        var head = s.Length > 16 ? s[..16] : s;
        return head.Replace("T", " ");
    }
}
