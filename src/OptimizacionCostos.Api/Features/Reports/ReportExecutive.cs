using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Port FIEL de app/reports/executive.py. Calcula el semaforo por dominio (verde/amarillo/rojo) a
/// partir del JSON del informe ya generado. NO consulta Azure ni SQL ni recalcula metricas. Solo se
/// incluyen los dominios presentes en el informe del cliente (igual que las secciones dinamicas).
/// El "report" es el objeto JSON del informe (claves del builder.py), recibido como JsonElement.
/// </summary>
public sealed class ReportExecutive : IReportExecutive
{
    public const double PressureThreshold = 90.0;

    public const string EstadoVerde = "verde";
    public const string EstadoAmarillo = "amarillo";
    public const string EstadoRojo = "rojo";

    // _ESTADO_ORDEN
    private static readonly IReadOnlyDictionary<string, int> EstadoOrden = new Dictionary<string, int>
    {
        [EstadoVerde] = 0, [EstadoAmarillo] = 1, [EstadoRojo] = 2,
    };

    // CONNECTED_STATES
    private static readonly IReadOnlySet<string> ConnectedStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "connected", "succeeded", "provisioned", "ready", "ok",
    };

    // _PRIORIDAD_POR_ESTADO
    private static readonly IReadOnlyDictionary<string, string> PrioridadPorEstado = new Dictionary<string, string>
    {
        [EstadoRojo] = "Alta", [EstadoAmarillo] = "Media", [EstadoVerde] = "Baja",
    };

    // executive.py::_domain
    private static SemaforoDomain Domain(string dominio, string etiqueta, string estado, string lectura,
        string criterio, JsonObject? detalle = null) =>
        new(dominio, etiqueta, estado, lectura, criterio, detalle ?? []);

    // ---------- Dominios ----------

    // executive.py::_availability_domain
    private static SemaforoDomain AvailabilityDomain(JsonElement report)
    {
        var events = GetList(report, "caidas");
        if (events.Count > 0)
            return Domain(
                "disponibilidad", "Disponibilidad", EstadoAmarillo,
                $"{events.Count} evento(s) de Azure registrados en el periodo.",
                "Amarillo si hubo eventos de Service Health en el periodo.",
                new JsonObject { ["eventos"] = events.Count });
        return Domain(
            "disponibilidad", "Disponibilidad", EstadoVerde,
            "Sin caidas ni eventos de plataforma reportados en el periodo.",
            "Verde sin eventos de Service Health en el periodo.",
            new JsonObject { ["eventos"] = 0 });
    }

    // executive.py::_vm_domain
    private static SemaforoDomain? VmDomain(JsonElement report)
    {
        var inventory = GetList(report, "inventario");
        if (inventory.Count == 0)
            return null;
        var vms = GetVirtualMachines(report);
        var sustained = new List<string?>();
        var peaks = new List<string?>();
        foreach (var vm in vms)
        {
            if (NumOrZero(vm, "cpu_avg") >= PressureThreshold || NumOrZero(vm, "ram_avg") >= PressureThreshold)
                sustained.Add(StrOrNull(vm, "resource_name"));
            if (NumOrZero(vm, "cpu_max") >= PressureThreshold || NumOrZero(vm, "ram_max") >= PressureThreshold)
                peaks.Add(StrOrNull(vm, "resource_name"));
        }
        var running = inventory.Count(vm => StrOrNull(vm, "status") == "Running");
        var detalle = new JsonObject
        {
            ["total"] = inventory.Count,
            ["encendidas"] = running,
            ["presion_sostenida"] = ToJsonArray(sustained.Take(10)),
            ["con_picos"] = peaks.Count,
        };
        var criterio =
            $"Rojo si alguna VM tiene CPU o RAM promedio >= {PressureThreshold.ToString("F0", CultureInfo.InvariantCulture)}%; "
            + $"amarillo si alguna registra maximos >= {PressureThreshold.ToString("F0", CultureInfo.InvariantCulture)}%.";
        if (sustained.Count > 0)
            return Domain(
                "vms", "Maquinas virtuales", EstadoRojo,
                $"{sustained.Count} VM(s) con presion sostenida de CPU/RAM "
                + $"(promedio >= {PressureThreshold.ToString("F0", CultureInfo.InvariantCulture)}%).",
                criterio, detalle);
        if (peaks.Count > 0)
            return Domain(
                "vms", "Maquinas virtuales", EstadoAmarillo,
                $"{peaks.Count} VM(s) con picos de CPU/RAM >= {PressureThreshold.ToString("F0", CultureInfo.InvariantCulture)}% en el mes.",
                criterio, detalle);
        return Domain(
            "vms", "Maquinas virtuales", EstadoVerde,
            "Sin presion relevante de CPU/RAM en el periodo.",
            criterio, detalle);
    }

    // executive.py::_paas_domain
    private static SemaforoDomain? PaasDomain(JsonElement report)
    {
        var status = GetList(report, "estado_recursos");
        if (status.Count == 0)
            return null;
        var unavailable = status.Sum(row => IntOrZero(row, "con_alerta"));
        var total = status.Sum(row => IntOrZero(row, "total"));
        // sorted(rows con con_alerta>0, key=-con_alerta) — orden estable descendente.
        var worst = status
            .Where(row => IntOrZero(row, "con_alerta") > 0)
            .OrderByDescending(row => IntOrZero(row, "con_alerta"))
            .ToList();
        var detalle = new JsonObject
        {
            ["total"] = total,
            ["no_disponibles"] = unavailable,
            ["tipos_afectados"] = ToJsonArray(worst.Take(5).Select(row => StrOrNull(row, "tipo"))),
        };
        const string criterio = "Rojo si hay recursos no disponibles segun Resource Health.";
        if (unavailable > 0)
            return Domain(
                "paas", "App Services / PaaS", EstadoRojo,
                $"{unavailable} recurso(s) no disponibles de {total} evaluados.",
                criterio, detalle);
        return Domain(
            "paas", "App Services / PaaS", EstadoVerde,
            $"Los {total} recursos PaaS evaluados estan disponibles.",
            criterio, detalle);
    }

    // executive.py::_backup_domain
    private static SemaforoDomain? BackupDomain(JsonElement report)
    {
        var backups = GetObject(report, "backups");
        var items = GetList(backups, "items");
        var vaults = GetList(backups, "vaults");
        if (items.Count == 0 && vaults.Count == 0)
            return null;
        var inventory = GetList(report, "inventario");
        var protectedVms = inventory.Count(vm => Truthy(vm, "has_backup"));
        var failed = items.Count(item => (StrOrNull(item, "estado") ?? "").Contains("Fallido", StringComparison.Ordinal));
        var jobs = GetObject(backups, "jobs_mes");
        var failedJobs = 0;
        if (jobs is { ValueKind: JsonValueKind.Object })
        {
            foreach (var prop in jobs.Value.EnumerateObject())
                if (prop.Name.Contains("Fallido", StringComparison.Ordinal))
                    failedJobs += AsInt(prop.Value);
        }
        var coverageGap = Math.Max(inventory.Count - protectedVms, 0);
        var detalle = new JsonObject
        {
            ["items_protegidos"] = items.Count,
            ["vms_total"] = inventory.Count,
            ["vms_con_backup"] = protectedVms,
            ["fallidos"] = failed + failedJobs,
        };
        const string criterio =
            "Rojo si hubo respaldos fallidos; amarillo si la cobertura de VMs es "
            + "parcial (validar criticidad con el cliente).";
        if (failed > 0 || failedJobs > 0)
            return Domain(
                "backup", "Respaldos", EstadoRojo,
                $"{failed + failedJobs} respaldo(s) con fallo en el periodo.",
                criterio, detalle);
        if (inventory.Count > 0 && coverageGap > 0)
            return Domain(
                "backup", "Respaldos", EstadoAmarillo,
                $"{protectedVms} de {inventory.Count} VMs con backup: validar si las "
                + "restantes son no criticas o estan excluidas formalmente.",
                criterio, detalle);
        return Domain(
            "backup", "Respaldos", EstadoVerde,
            "Operacion de respaldo estable y cobertura completa del inventario.",
            criterio, detalle);
    }

    // executive.py::_connectivity_domain
    private static SemaforoDomain? ConnectivityDomain(JsonElement report)
    {
        var items = GetList(report, "conectividad");
        if (items.Count == 0)
            return null;
        var down = items
            .Where(item => !ConnectedStates.Contains((StrOrNull(item, "estado") ?? "").Trim().ToLowerInvariant()))
            .Select(item => StrOrNull(item, "nombre"))
            .ToList();
        var detalle = new JsonObject
        {
            ["total"] = items.Count,
            ["revisar"] = ToJsonArray(down.Take(5)),
        };
        const string criterio = "Amarillo si algun circuito/conexion no reporta estado operativo.";
        if (down.Count > 0)
            return Domain(
                "conectividad", "Conectividad", EstadoAmarillo,
                $"{down.Count} conexion(es) sin estado operativo confirmado.",
                criterio, detalle);
        return Domain(
            "conectividad", "Conectividad", EstadoVerde,
            "Circuitos y conexiones operativos.",
            criterio, detalle);
    }

    // executive.py::_rbac_domain
    private static SemaforoDomain? RbacDomain(JsonElement report)
    {
        var permisos = GetObject(report, "permisos");
        var resumen = GetObject(permisos, "resumen");
        if (IntOrZero(resumen, "total") == 0)
            return null;
        var elevados = IntOrZero(resumen, "elevados");
        var rootTenant = IntOrZero(resumen, "root_tenant");
        var detalle = new JsonObject
        {
            ["total"] = IntOrZero(resumen, "total"),
            ["elevados"] = elevados,
            ["owners"] = IntOrZero(resumen, "owners"),
            ["root_tenant"] = rootTenant,
        };
        const string criterio =
            "Rojo si hay asignaciones a nivel root/tenant; amarillo si hay roles "
            + "elevados (Owner/Contributor/UAA).";
        if (rootTenant > 0)
            return Domain(
                "rbac", "Permisos (RBAC)", EstadoRojo,
                $"{rootTenant} asignacion(es) a nivel root/tenant requieren revision.",
                criterio, detalle);
        if (elevados > 0)
            return Domain(
                "rbac", "Permisos (RBAC)", EstadoAmarillo,
                $"{elevados} rol(es) elevados: validar necesidad y menor privilegio.",
                criterio, detalle);
        return Domain(
            "rbac", "Permisos (RBAC)", EstadoVerde,
            "Sin roles elevados detectados.",
            criterio, detalle);
    }

    // executive.py::_alerts_domain
    private static SemaforoDomain? AlertsDomain(JsonElement report)
    {
        var alertas = GetObject(report, "alertas");
        // resumen = alertas.get("resumen"); if resumen is None: return None
        JsonElement? resumen = null;
        if (alertas is { ValueKind: JsonValueKind.Object }
            && alertas.Value.TryGetProperty("resumen", out var r) && r.ValueKind != JsonValueKind.Null)
            resumen = r;
        if (resumen is null)
            return null;
        var total = IntOrZero(resumen, "total");
        var criticas = IntOrZero(resumen, "criticas");
        var detalle = new JsonObject
        {
            ["disparos"] = total,
            ["criticas"] = criticas,
        };
        const string criterio =
            "Rojo si hubo disparos criticos (Sev0/Sev1); amarillo si hubo disparos.";
        if (criticas > 0)
            return Domain(
                "alertas", "Alertas BIT", EstadoRojo,
                $"{criticas} disparo(s) criticos de {total} en el mes.",
                criterio, detalle);
        if (total > 0)
            return Domain(
                "alertas", "Alertas BIT", EstadoAmarillo,
                $"{total} disparo(s) de alertas administradas en el mes.",
                criterio, detalle);
        return Domain(
            "alertas", "Alertas BIT", EstadoVerde,
            "Sin disparos de alertas administradas en el periodo.",
            criterio, detalle);
    }

    // executive.py::compute_semaforo
    public Semaforo ComputeSemaforo(JsonElement report)
    {
        var candidates = new[]
        {
            AvailabilityDomain(report),
            VmDomain(report),
            PaasDomain(report),
            BackupDomain(report),
            ConnectivityDomain(report),
            RbacDomain(report),
            AlertsDomain(report),
        };
        var domains = candidates.Where(d => d is not null).Select(d => d!).ToList();
        var overall = EstadoVerde;
        foreach (var domain in domains)
            if (EstadoOrden[domain.Estado] > EstadoOrden[overall])
                overall = domain.Estado;
        return new Semaforo(PressureThreshold, overall, domains);
    }

    // executive.py::build_default_action_plan
    public IReadOnlyList<ActionPlanSeed> BuildDefaultActionPlan(JsonElement report, Semaforo semaforo)
    {
        // items con prioridad/hallazgo/accion/responsable; orden/estado se rellenan al final.
        var raw = new List<(string Prioridad, string Hallazgo, string Accion, string Responsable)>();
        var domains = (semaforo.Dominios ?? []).ToDictionary(d => d.Dominio, d => d);
        var threshold = semaforo.UmbralPresion == 0 ? PressureThreshold : semaforo.UmbralPresion;

        if (domains.TryGetValue("paas", out var paas) && paas.Estado == EstadoRojo)
            raw.Add((
                "Alta",
                $"Recursos PaaS no disponibles: {paas.Lectura}",
                "Validar criticidad, estado real, dependencias y causa raiz de cada recurso.",
                "Cliente + BIT"));

        // vms: presion sostenida ordenada por -max(ram_avg, cpu_avg), top 3.
        var vms = GetVirtualMachines(report);
        var hot = vms
            .Where(vm => NumOrZero(vm, "cpu_avg") >= threshold || NumOrZero(vm, "ram_avg") >= threshold)
            .OrderByDescending(vm => Math.Max(NumOrZero(vm, "ram_avg"), NumOrZero(vm, "cpu_avg")))
            .Take(3);
        foreach (var vm in hot)
            raw.Add((
                "Alta",
                $"{Repr(StrOrNull(vm, "resource_name"))} con presion sostenida: CPU prom "
                + $"{Repr(RawOrNone(vm, "cpu_avg"))}% (max {Repr(RawOrNone(vm, "cpu_max"))}%), RAM prom "
                + $"{Repr(RawOrNone(vm, "ram_avg"))}% (max {Repr(RawOrNone(vm, "ram_max"))}%).",
                "Revisar carga, procesos y horarios de saturacion; evaluar ajuste de capacidad.",
                "BIT"));

        if (domains.TryGetValue("alertas", out var alerts) && alerts.Estado == EstadoRojo)
            raw.Add((
                "Alta",
                $"Alertas criticas del mes: {alerts.Lectura}",
                "Revisar causa raiz de los disparos criticos y acciones correctivas aplicadas.",
                "BIT"));

        if (domains.TryGetValue("backup", out var backup) && backup.Estado != EstadoVerde)
            raw.Add((
                PrioridadPorEstado[backup.Estado],
                $"Cobertura de respaldo: {backup.Lectura}",
                "Confirmar matriz de criticidad y alcance esperado de respaldo con el cliente.",
                "Cliente"));

        if (domains.TryGetValue("rbac", out var rbac) && rbac.Estado != EstadoVerde)
            raw.Add((
                PrioridadPorEstado[rbac.Estado],
                $"Permisos elevados (RBAC): {rbac.Lectura}",
                "Revisar necesidad, vigencia y principio de menor privilegio.",
                "Cliente"));

        if (domains.TryGetValue("conectividad", out var connectivity) && connectivity.Estado != EstadoVerde)
            raw.Add((
                PrioridadPorEstado[connectivity.Estado],
                $"Conectividad: {connectivity.Lectura}",
                "Validar estado de circuitos/conexiones con el proveedor.",
                "Cliente + BIT"));

        // order = {"Alta":0,"Media":1,"Baja":2}; sort estable por prioridad.
        var order = new Dictionary<string, int> { ["Alta"] = 0, ["Media"] = 1, ["Baja"] = 2 };
        var sorted = raw.OrderBy(item => order[item.Prioridad]).ToList();
        var result = new List<ActionPlanSeed>(sorted.Count);
        for (var index = 0; index < sorted.Count; index++)
        {
            var item = sorted[index];
            result.Add(new ActionPlanSeed(
                Prioridad: item.Prioridad,
                Hallazgo: item.Hallazgo,
                Accion: item.Accion,
                Responsable: item.Responsable,
                Orden: index,
                Estado: "Pendiente"));
        }
        return result;
    }

    // executive.py::build_executive_context
    public JsonObject BuildExecutiveContext(JsonElement report, Semaforo semaforo)
    {
        var inventory = GetList(report, "inventario");
        var vms = GetVirtualMachines(report);
        // sorted(vms, key=max(ram_avg, cpu_avg), reverse=True)[:5]
        var topVms = vms
            .OrderByDescending(vm => Math.Max(NumOrZero(vm, "ram_avg"), NumOrZero(vm, "cpu_avg")))
            .Take(5);

        var client = GetObject(report, "client");
        var period = GetObject(report, "period");

        var dominios = new JsonArray();
        foreach (var domain in semaforo.Dominios ?? [])
            dominios.Add(new JsonObject
            {
                ["dominio"] = domain.Dominio,
                ["etiqueta"] = domain.Etiqueta,
                ["estado"] = domain.Estado,
                ["lectura"] = domain.Lectura,
                ["detalle"] = domain.Detalle.DeepClone(),
            });

        var vmsMayorConsumo = new JsonArray();
        foreach (var vm in topVms)
            vmsMayorConsumo.Add(new JsonObject
            {
                ["nombre"] = StrOrNull(vm, "resource_name"),
                ["cpu_avg"] = CloneOrNull(vm, "cpu_avg"),
                ["cpu_max"] = CloneOrNull(vm, "cpu_max"),
                ["ram_avg"] = CloneOrNull(vm, "ram_avg"),
                ["ram_max"] = CloneOrNull(vm, "ram_max"),
            });

        return new JsonObject
        {
            ["cliente"] = StrOrNull(client, "name"),
            ["periodo"] = StrOrNull(period, "label"),
            ["semaforo"] = new JsonObject
            {
                ["estado_general"] = semaforo.EstadoGeneral,
                ["dominios"] = dominios,
            },
            ["kpis"] = new JsonObject
            {
                ["vms_total"] = inventory.Count,
                ["vms_encendidas"] = inventory.Count(vm => StrOrNull(vm, "status") == "Running"),
                ["vms_con_backup"] = inventory.Count(vm => Truthy(vm, "has_backup")),
            },
            ["vms_mayor_consumo"] = vmsMayorConsumo,
            ["estado_recursos"] = CloneOrNull(report, "estado_recursos"),
            ["alertas_resumen"] = CloneOrNull(GetObject(report, "alertas"), "resumen"),
            ["caidas"] = CloneOrNull(report, "caidas"),
        };
    }

    // ---------- Helpers de acceso JSON (semantica Python: .get(...) or default) ----------

    // report.get(key) or [] — lista vacia si ausente, null, o no-array.
    private static IReadOnlyList<JsonElement> GetList(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return [];
        if (!obj.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.Array) return [];
        return p.EnumerateArray().ToArray();
    }

    private static IReadOnlyList<JsonElement> GetList(JsonElement? obj, string key) =>
        obj is { } el ? GetList(el, key) : [];

    // report.get(key) or {} — JsonElement objeto, o null si ausente/null/no-objeto.
    private static JsonElement? GetObject(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        return p;
    }

    private static JsonElement? GetObject(JsonElement? obj, string key) =>
        obj is { } el ? GetObject(el, key) : null;

    // (report.get("performance") or {}).get("virtual_machines") or []
    private static IReadOnlyList<JsonElement> GetVirtualMachines(JsonElement report)
    {
        var performance = GetObject(report, "performance");
        return GetList(performance, "virtual_machines");
    }

    // vm.get(key) — string, o null si ausente/no-string.
    private static string? StrOrNull(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    private static string? StrOrNull(JsonElement? obj, string key) =>
        obj is { } el ? StrOrNull(el, key) : null;

    // (vm.get(key) or 0) en comparacion numerica: numero, o 0 si ausente/null/no-numero/0/false.
    private static double NumOrZero(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return 0;
        if (!obj.TryGetProperty(key, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : 0;
    }

    // int(row.get(key) or 0): numero (trunc), string numerica, o 0. Tolerante (Python int()).
    private static int IntOrZero(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return 0;
        if (!obj.TryGetProperty(key, out var p)) return 0;
        return AsInt(p);
    }

    private static int IntOrZero(JsonElement? obj, string key) =>
        obj is { } el ? IntOrZero(el, key) : 0;

    private static int AsInt(JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.Number => p.TryGetInt32(out var i) ? i
            : (p.TryGetDouble(out var d) ? (int)d : 0),
        JsonValueKind.String => int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s
            : (double.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var sd) ? (int)sd : 0),
        JsonValueKind.True => 1,
        _ => 0,
    };

    // bool(vm.get(key)): truthiness Python — true, numero!=0, string no vacia, lista/objeto no vacios.
    private static bool Truthy(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(key, out var p)) return false;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Number => p.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => !string.IsNullOrEmpty(p.GetString()),
            JsonValueKind.Array => p.GetArrayLength() > 0,
            JsonValueKind.Object => p.EnumerateObject().Any(),
            _ => false,
        };
    }

    // Clona el valor crudo de una propiedad como JsonNode, o JSON null si ausente/null.
    private static JsonNode? CloneOrNull(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var p) || p.ValueKind == JsonValueKind.Null) return null;
        return JsonNode.Parse(p.GetRawText());
    }

    private static JsonNode? CloneOrNull(JsonElement? obj, string key) =>
        obj is { } el ? CloneOrNull(el, key) : null;

    // vm.get(key) crudo (sin coercion) para el f-string del plan: el valor tal cual o None.
    // Devuelve null cuando la propiedad esta ausente o es JSON null (str(None)=="None").
    private static JsonElement? RawOrNone(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(key, out var p) || p.ValueKind == JsonValueKind.Null) return null;
        return p;
    }

    // repr Python en f-string: str(value). None -> "None"; numeros sin formato extra; strings sin comillas.
    private static string Repr(string? value) => value ?? "None";

    private static string Repr(JsonElement? value)
    {
        if (value is not { } el) return "None";
        return el.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => "None",
            JsonValueKind.String => el.GetString() ?? "None",
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => el.GetRawText(),
            _ => el.GetRawText(),
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<string?> values)
    {
        var arr = new JsonArray();
        foreach (var v in values)
            arr.Add(v is null ? null : JsonValue.Create(v));
        return arr;
    }

    private static JsonArray ToJsonArray(IEnumerable<int> values)
    {
        var arr = new JsonArray();
        foreach (var v in values)
            arr.Add(v);
        return arr;
    }
}
