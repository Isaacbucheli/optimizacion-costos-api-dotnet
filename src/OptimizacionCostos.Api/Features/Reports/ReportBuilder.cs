using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Cdc;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>El cliente no tiene suscripciones Azure activas (port del ValueError de build_report).</summary>
public sealed class ReportNoSubscriptionsException(string message) : Exception(message);

/// <summary>
/// Builder del informe mensual de gestión (estructura del informe CDC). Port FIEL de
/// app/reports/builder.py. Consulta Azure EN VIVO (Resource Graph + ARM + Monitor) y arma el
/// objeto JSON del informe (mismas claves del Python; las consume el front, el Word y la narrativa).
/// SIN COSTOS: el informe no incluye cifras económicas (igual que el Python). Las secciones son
/// dinámicas: solo aparecen si el cliente tiene recursos de ese tipo.
/// </summary>
public interface IReportBuilder
{
    /// <summary>
    /// Construye el informe completo. Lanza <see cref="ReportNoSubscriptionsException"/> si el cliente
    /// no tiene suscripciones activas administradas (equivale al ValueError del Python).
    /// </summary>
    Task<JsonObject> BuildReportAsync(
        int clientId, int year, int month, TokenCredential credential,
        bool includeNarrative = true, CancellationToken ct = default);
}

public sealed class ReportBuilder(
    ISqlConnectionFactory factory,
    IReportInventory inventory,
    IReportMetrics metrics,
    IReportAlerts alerts,
    IPowerHistoryService power,
    IReportNarrativeService narrative,
    IAdvisorScoreStore advisorStore,
    ILogger<ReportBuilder> logger) : IReportBuilder
{
    private const int SchemaVersion = 2;
    private const string ArmScope = "https://management.azure.com/.default";

    private static readonly string[] MonthNames =
    [
        "", "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
        "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre",
    ];

    // PILLAR_NAMES de app/waf/ai_curator.py (usado por _waf_recommendations).
    private static readonly IReadOnlyDictionary<int, string> PillarNames = new Dictionary<int, string>
    {
        [1] = "Eficiencia del rendimiento",
        [2] = "Excelencia operacional",
        [3] = "Seguridad",
        [4] = "Confiabilidad",
        [5] = "Optimizacion de costos",
    };

    // Serializa DTOs respetando sus [JsonPropertyName] (sin naming policy) y ensure_ascii=False.
    private static readonly JsonSerializerOptions DtoJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public async Task<JsonObject> BuildReportAsync(
        int clientId, int year, int month, TokenCredential credential,
        bool includeNarrative = true, CancellationToken ct = default)
    {
        var clientName = await ClientNameAsync(clientId, ct);
        var (subscriptions, subNameMap) = await ClientSubscriptionsAsync(clientId, ct);
        if (subscriptions.Count == 0)
            throw new ReportNoSubscriptionsException(
                "El cliente no tiene suscripciones Azure activas registradas. " +
                "Sincroniza las suscripciones en Credenciales Azure antes de generar el informe.");

        var window = metrics.MonthTimespan(year, month);
        var start = window.Start;
        var end = window.End;

        // --- Inventario vivo ---
        var vms = await inventory.FetchVmInventoryAsync(credential, subscriptions, subNameMap, ct);
        var locations = vms.Where(v => !string.IsNullOrEmpty(v.LocationRaw)).Select(v => v.LocationRaw)
            .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var token = (await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct)).Token;
        var sizes = vms.Count > 0
            ? await inventory.FetchVmSizesAsync(token, subscriptions, locations, ct)
            : new Dictionary<string, VmSizeInfo>();
        var health = await inventory.FetchResourceHealthAsync(credential, subscriptions, ct);
        var serviceStatus = await inventory.FetchServiceStatusAsync(credential, subscriptions, ct);
        var backups = await inventory.FetchBackupsAsync(credential, subscriptions, start.UtcDateTime, end.UtcDateTime, subNameMap, ct);
        var appServices = await inventory.FetchAppServicePlansAsync(credential, subscriptions, ct);
        var connectivity = await inventory.FetchConnectivityAsync(credential, subscriptions, ct);
        var sqlMi = await inventory.FetchSqlMiInventoryAsync(credential, subscriptions, ct);
        var events = await inventory.FetchServiceHealthEventsAsync(credential, subscriptions, locations, start.UtcDateTime, end.UtcDateTime, ct);
        var permissions = await inventory.FetchRoleAssignmentsAsync(credential, subscriptions, subNameMap, ct);
        var alertsData = await alerts.FetchFiredAlertsAsync(credential, subscriptions, subNameMap, start, end, ct);

        var backupNames = new HashSet<string>(
            backups.Items.Select(i => (i.Servidor ?? "").ToLowerInvariant()), StringComparer.Ordinal);

        // Inventario enriquecido (dicts mutables, como el Python): vcpu/ram_gb/has_backup/health.
        var invJson = new List<JsonObject>(vms.Count);
        foreach (var vm in vms)
        {
            var o = ToObj(vm);
            var sizeInfo = vm.Size is not null && sizes.TryGetValue(vm.Size, out var si) ? si : null;
            o["vcpu"] = sizeInfo?.Vcpu is { } vc ? JsonValue.Create(vc) : null;
            o["ram_gb"] = sizeInfo is null ? null : JsonValue.Create(sizeInfo.RamGb);
            o["has_backup"] = backupNames.Contains((vm.Name ?? "").ToLowerInvariant());
            o["health"] = health.TryGetValue(vm.Id ?? "", out var h) ? h : "";
            invJson.Add(o);
        }

        // --- Encendido/apagado del mes (Activity Log): horas y % uptime. Best-effort, SIN COSTOS. ---
        var runningByArm = vms.Where(v => !string.IsNullOrEmpty(v.Id))
            .ToDictionary(v => v.Id, v => IsRunning(v.Status), StringComparer.Ordinal);
        IReadOnlyDictionary<string, (double RunningHours, double UptimePct)> uptimeMap;
        try
        {
            uptimeMap = await power.ComputeUptimeMapAsync(credential, subscriptions, runningByArm, start, end, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Uptime del informe no disponible; continua sin ese dato");
            uptimeMap = new Dictionary<string, (double, double)>();
        }
        foreach (var o in invJson)
        {
            var id = o["id"]?.GetValue<string>();
            if (id is not null && uptimeMap.TryGetValue(id, out var usage))
            {
                o["uptime_pct"] = JsonValue.Create(usage.UptimePct);
                o["running_hours"] = JsonValue.Create(usage.RunningHours);
            }
            else
            {
                o["uptime_pct"] = null;
                o["running_hours"] = null;
            }
        }

        var templates = BuildTemplates(vms, sizes);

        // --- Métricas del mes (Azure Monitor) ---
        var targets = new List<MetricTarget>();
        foreach (var vm in vms)
            if (!string.IsNullOrEmpty(vm.Id))
                targets.Add(new MetricTarget(vm.Id, vm.Name, "microsoft.compute/virtualmachines", vm.Subscription, vm.ResourceGroup));
        foreach (var mi in sqlMi)
            if (!string.IsNullOrEmpty(mi.Id))
                targets.Add(new MetricTarget(mi.Id, mi.Name, "microsoft.sql/managedinstances", null));
        foreach (var plan in appServices)
            if (!string.IsNullOrEmpty(plan.Id))
                targets.Add(new MetricTarget(plan.Id, plan.Plan, "microsoft.web/serverfarms", null));
        var performance = await metrics.ExtractMetricsAsync(credential, targets, start, end, ct);
        var perfJson = (JsonObject)(JsonSerializer.SerializeToNode(performance, DtoJson) ?? new JsonObject());

        // Enriquecer SQL MI con sus métricas y quitar id.
        var miMetrics = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (perfJson["sql_managed_instances"] is JsonArray miArr)
            foreach (var node in miArr)
                if (node is JsonObject mo && mo["resource_name"]?.GetValue<string>() is { } rn)
                    miMetrics[rn] = mo;

        var sqlMiJson = new List<JsonObject>(sqlMi.Count);
        foreach (var mi in sqlMi)
        {
            var o = ToObj(mi);
            var metric = mi.Name is not null && miMetrics.TryGetValue(mi.Name, out var m) ? m : null;
            o["cpu_avg"] = metric?["cpu_avg"]?.DeepClone();
            o["cpu_max"] = metric?["cpu_max"]?.DeepClone();
            o["storage_used_gb"] = metric?["storage_used_gb"]?.DeepClone();
            o.Remove("id");
            sqlMiJson.Add(o);
        }

        var appServicesJson = appServices.Select(p => { var o = ToObj(p); o.Remove("id"); return o; }).ToList();

        var permJson = (JsonObject)(JsonSerializer.SerializeToNode(permissions, DtoJson) ?? new JsonObject());
        var serviceStatusJson = ToArr(serviceStatus);

        var sections = new JsonObject
        {
            ["vms"] = invJson.Count > 0,
            ["plantillas"] = templates.Count > 0,
            ["estado_recursos"] = serviceStatus.Count > 0,
            ["performance"] = IsNonEmptyArray(perfJson["virtual_machines"]) || IsNonEmptyArray(perfJson["app_service_plans"]),
            ["backups"] = backups.Vaults.Count > 0 || backups.Items.Count > 0,
            ["sql_mi"] = sqlMi.Count > 0,
            ["app_services"] = appServices.Count > 0,
            ["conectividad"] = connectivity.Count > 0,
            ["permisos"] = Truthy((permJson["resumen"] as JsonObject)?["total"]),
            ["alertas"] = Truthy(alertsData.TryGetValue("resumen", out var res) ? ToNode(res) : null)
                          || Truthy(alertsData.TryGetValue("error", out var err) ? ToNode(err) : null),
        };

        // inventario final: sin id ni location_raw (igual que el Python).
        var inventarioFinal = new JsonArray();
        foreach (var o in invJson)
        {
            var clone = (JsonObject)o.DeepClone();
            clone.Remove("id");
            clone.Remove("location_raw");
            inventarioFinal.Add(clone);
        }

        var regiones = invJson.Select(o => o["location"]?.GetValue<string>())
            .Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => (JsonNode)JsonValue.Create(s!)).ToArray();
        var suscripciones = subNameMap.Values.Distinct().OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => (JsonNode)JsonValue.Create(s)).ToArray();

        var report = new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["client"] = new JsonObject { ["id"] = clientId, ["name"] = clientName },
            ["period"] = new JsonObject
            {
                ["year"] = year,
                ["month"] = month,
                ["label"] = $"{MonthNames[month]} {year}",
                ["start"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["end"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["partial"] = window.Partial,
            },
            ["generated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["meta"] = new JsonObject
            {
                ["regiones"] = new JsonArray(regiones),
                ["suscripciones"] = new JsonArray(suscripciones),
            },
            ["inventario"] = inventarioFinal,
            ["plantillas"] = templates,
            ["estado_recursos"] = serviceStatusJson,
            ["performance"] = perfJson,
            ["backups"] = ToObj(backups),
            ["sql_mi"] = new JsonArray(sqlMiJson.Select(o => (JsonNode)o).ToArray()),
            ["app_services"] = new JsonArray(appServicesJson.Select(o => (JsonNode)o).ToArray()),
            ["conectividad"] = ToArr(connectivity),
            ["permisos"] = permJson,
            ["alertas"] = (JsonObject)(JsonSerializer.SerializeToNode(alertsData, DtoJson) ?? new JsonObject()),
            ["caidas"] = ToArr(events),
            ["sla"] = BuildSla(sections, year, month),
            ["recomendaciones_waf"] = await WafRecommendationsAsync(clientId, ct),
            ["advisor_score_history"] = await advisorStore.BuildScoreHistoryAsync(clientId, year, month, ct),
            ["sections"] = sections,
            ["narrative"] = null,
        };

        if (includeNarrative)
        {
            try
            {
                var narrativeResult = await narrative.GenerateNarrativeAsync(NarrativeContext(report), ct);
                report["narrative"] = narrativeResult is null ? null : JsonSerializer.SerializeToNode(narrativeResult, DtoJson);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Narrativa IA fallo; el informe continua sin narrativa");
                report["narrative"] = null;
            }
        }

        return report;
    }

    // -------------------- helpers de datos (SQL) --------------------

    private async Task<string?> ClientNameAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_name FROM dbo.clients WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : result.ToString();
    }

    private async Task<(List<string> Ids, Dictionary<string, string> NameMap)> ClientSubscriptionsAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT subscription_id, subscription_name
            FROM dbo.client_azure_subscriptions
            WHERE client_id = @id AND is_active = 1 AND COALESCE(is_managed, 1) = 1
            """;
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        var ids = new List<string>();
        var nameMap = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var subId = r.GetString(0);
            var subName = r.IsDBNull(1) ? subId : r.GetString(1);
            if (string.IsNullOrEmpty(subName)) subName = subId;
            ids.Add(subId);
            nameMap[subId] = subName;
        }
        return (ids, nameMap);
    }

    private async Task<JsonArray> WafRecommendationsAsync(int clientId, CancellationToken ct)
    {
        var list = new JsonArray();
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT TOP 10 r.matrix_code, k.pillar_number, k.review_scope_es,
                       k.benefit_es, k.client_action_es, k.bit_action_es,
                       r.business_impact
                FROM dbo.waf_recommendation r
                INNER JOIN dbo.waf_recommendation_canonical k ON k.canonical_id = r.canonical_id
                WHERE r.client_id = @id AND r.is_active = 1 AND COALESCE(r.is_dismissed, 0) = 0
                ORDER BY r.impact_number DESC, r.matrix_code
                """;
            cmd.Parameters.Add(new SqlParameter("@id", clientId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var pillar = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
                list.Add(new JsonObject
                {
                    ["codigo"] = r.IsDBNull(0) ? null : r.GetString(0),
                    ["pilar"] = PillarNames.GetValueOrDefault(pillar, ""),
                    ["ambito"] = r.IsDBNull(2) ? null : r.GetString(2),
                    ["beneficio"] = r.IsDBNull(3) ? null : r.GetString(3),
                    ["accion_cliente"] = r.IsDBNull(4) ? null : r.GetString(4),
                    ["accion_bit"] = r.IsDBNull(5) ? null : r.GetString(5),
                    ["impacto"] = r.IsDBNull(6) ? null : r.GetString(6),
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudieron leer recomendaciones WAF");
            return new JsonArray();
        }
        return list;
    }

    // -------------------- helpers de armado --------------------

    // Port de _build_templates: agrupa el inventario por SKU (size).
    private static JsonArray BuildTemplates(IReadOnlyList<VmInventoryItem> inventory, IReadOnlyDictionary<string, VmSizeInfo> sizes)
    {
        var order = new List<string>();
        var grouped = new Dictionary<string, (int? Vcpu, double? RamGb, int Vms, SortedSet<string> Os)>();
        foreach (var vm in inventory)
        {
            var sku = string.IsNullOrEmpty(vm.Size) ? "Desconocido" : vm.Size;
            if (!grouped.TryGetValue(sku, out var entry))
            {
                var sizeInfo = vm.Size is not null && sizes.TryGetValue(vm.Size, out var si) ? si : null;
                entry = (sizeInfo?.Vcpu, sizeInfo is null ? null : sizeInfo.RamGb, 0, new SortedSet<string>(StringComparer.Ordinal));
                order.Add(sku);
            }
            entry.Vms += 1;
            if (!string.IsNullOrEmpty(vm.Os)) entry.Os.Add(vm.Os);
            grouped[sku] = entry;
        }

        // Python: templates.sort(key=lambda item: -item["vms"]) — descendente por número de VMs (estable).
        var arr = new JsonArray();
        foreach (var sku in order.OrderByDescending(s => grouped[s].Vms))
        {
            var e = grouped[sku];
            arr.Add(new JsonObject
            {
                ["sku"] = sku,
                ["vcpu"] = e.Vcpu is { } vc ? JsonValue.Create(vc) : null,
                ["ram_gb"] = e.RamGb is { } ram ? JsonValue.Create(ram) : null,
                ["vms"] = e.Vms,
                ["os"] = e.Os.Count > 0 ? string.Join(" / ", e.Os) : "-",
            });
        }
        return arr;
    }

    // Port de _build_sla: disponibilidad plantilla (100%) por servicio presente.
    private static JsonArray BuildSla(JsonObject sections, int year, int month)
    {
        var hours = DateTime.DaysInMonth(year, month) * 24;
        var services = new List<string>();
        if (Truthy(sections["vms"])) services.Add("Maquinas virtuales");
        if (Truthy(sections["sql_mi"])) services.Add("SQL Managed Instance");
        if (Truthy(sections["app_services"])) services.Add("App Services");
        if (Truthy(sections["backups"])) services.Add("Respaldos (Backup)");
        if (Truthy(sections["conectividad"])) services.Add("Conectividad (ER/VPN)");
        var arr = new JsonArray();
        foreach (var service in services)
            arr.Add(new JsonObject
            {
                ["servicio"] = service,
                ["acordado_h"] = hours,
                ["caidas_h"] = 0,
                ["disponibilidad"] = "100%",
            });
        return arr;
    }

    // Port de _narrative_context: versión compacta para el prompt (agregados, sin series diarias).
    private static JsonObject NarrativeContext(JsonObject report)
    {
        var inventory = report["inventario"] as JsonArray ?? new JsonArray();
        var perf = report["performance"] as JsonObject ?? new JsonObject();
        var backups = report["backups"] as JsonObject ?? new JsonObject();

        var running = inventory.OfType<JsonObject>().Count(vm => vm["status"]?.GetValue<string>() == "Running");
        var conBackup = inventory.OfType<JsonObject>().Count(vm => Truthy(vm["has_backup"]));
        var total = inventory.Count;

        var items = backups["items"] as JsonArray ?? new JsonArray();
        var itemsFallidos = items.OfType<JsonObject>()
            .Count(it => (it["estado"]?.GetValue<string>() ?? "").Contains("Fallido"));

        var recomendaciones = new JsonArray();
        if (report["recomendaciones_waf"] is JsonArray recs)
            foreach (var rec in recs.OfType<JsonObject>().Take(6))
                recomendaciones.Add(new JsonObject
                {
                    ["ambito"] = rec["ambito"]?.DeepClone(),
                    ["beneficio"] = rec["beneficio"]?.DeepClone(),
                });

        return new JsonObject
        {
            ["cliente"] = (report["client"] as JsonObject)?["name"]?.DeepClone(),
            ["periodo"] = (report["period"] as JsonObject)?["label"]?.DeepClone(),
            ["inventario"] = new JsonObject
            {
                ["vms_total"] = total,
                ["vms_running"] = running,
                ["vms_apagadas"] = total - running,
                ["con_backup"] = conBackup,
            },
            ["estado_recursos"] = report["estado_recursos"]?.DeepClone(),
            ["performance_vms_top"] = StripDaily(perf["virtual_machines"] as JsonArray),
            ["performance_sql_mi"] = StripDaily(perf["sql_managed_instances"] as JsonArray),
            ["performance_app_plans_top"] = StripDaily(perf["app_service_plans"] as JsonArray),
            ["backups"] = new JsonObject
            {
                ["vaults"] = backups["vaults"]?.DeepClone(),
                ["jobs_mes"] = backups["jobs_mes"]?.DeepClone(),
                ["items_total"] = items.Count,
                ["items_fallidos"] = itemsFallidos,
            },
            ["sql_mi"] = report["sql_mi"]?.DeepClone(),
            ["caidas_azure"] = report["caidas"]?.DeepClone(),
            ["sla"] = report["sla"]?.DeepClone(),
            ["recomendaciones_waf"] = recomendaciones,
            ["advisor_score_history"] = report["advisor_score_history"]?.DeepClone(),
        };
    }

    // _strip_daily: quita 'daily', ordena por cpu_avg desc (None/0 = 0), top 20.
    private static JsonArray StripDaily(JsonArray? items, int limit = 20)
    {
        if (items is null) return new JsonArray();
        var slim = new List<(double Cpu, JsonObject Obj)>();
        foreach (var node in items.OfType<JsonObject>())
        {
            var clone = (JsonObject)node.DeepClone();
            clone.Remove("daily");
            var cpu = clone["cpu_avg"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            slim.Add((cpu, clone));
        }
        var arr = new JsonArray();
        foreach (var (_, obj) in slim.OrderByDescending(s => s.Cpu).Take(limit))
            arr.Add(obj);
        return arr;
    }

    // -------------------- utilidades --------------------

    private static bool IsRunning(string? powerState) =>
        (powerState ?? "").Trim().ToLowerInvariant().Contains("running");

    private static JsonObject ToObj(object dto) => (JsonObject)JsonSerializer.SerializeToNode(dto, DtoJson)!;
    private static JsonArray ToArr<T>(IEnumerable<T> items) => (JsonArray)JsonSerializer.SerializeToNode(items, DtoJson)!;

    private static JsonNode? ToNode(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, DtoJson);

    private static bool IsNonEmptyArray(JsonNode? node) => node is JsonArray arr && arr.Count > 0;

    // bool(x) de Python: null/0/""/[]/{} -> false; resto -> true.
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v => TruthyValue(v),
        _ => true,
    };

    private static bool TruthyValue(JsonValue v)
    {
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<double>(out var d)) return d != 0;
        if (v.TryGetValue<string>(out var s)) return !string.IsNullOrEmpty(s);
        return true;
    }
}
