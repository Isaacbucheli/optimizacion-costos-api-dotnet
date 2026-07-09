using System.Globalization;
using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de Virtual Machines. Port de app/calculators/compute_vm.py
/// (clase ComputeVMCalculator, service_key "vms").
///
/// FIX v0.5.2 — Windows License Premium en RI:
/// Cuando compras un RI en Azure SIN AHB, te ahorras solo el compute base (Linux).
/// La licencia Windows se sigue pagando por hora encima de la RI. Por eso:
///
///     RI Windows total/mes = (RI_base_anual / 12)  +  windows_premium_mensual
///     windows_premium     = (PAYG_Windows - PAYG_Linux) × 730
///
/// Si la VM tiene AHB Windows activo (licenseType = 'Windows_Server'), entonces
/// sí pagas solo el RI base Linux puro.
///
/// FIX AHB en PAYG (2026-07-03) — divergencia intencional del port Python:
/// Con AHB Windows activo el cliente aporta su propia licencia y Azure factura SOLO el
/// compute base = tarifa Linux de la misma SKU. AHB no existe como meter en la Azure Retail
/// Prices API: pedir "Windows" devuelve el meter CON licencia. Antes el PAYG de una VM con AHB
/// se cotizaba a la tarifa Windows (con premium), sobreestimando su costo mensual por exactamente
/// ese premium — justo el ahorro que el propio módulo declara en CostEstimation.AhbMonthlySavings.
/// Ahora el PAYG de una VM con AHB se cotiza a la tarifa Linux (paygOs = "Linux"). El .py NO se
/// toca (regla "solo .NET"): este calculador deja de ser 1:1 con Python para VMs Windows con AHB.
///
/// FIX Windows_Client (2026-07-07) — misma lógica que AHB, extendida:
/// licenseType = 'Windows_Client' (Win 10/11 con Multitenant Hosting Rights) también cotiza el PAYG
/// a tarifa Linux. Un OS cliente NUNCA lleva licencia de Windows Server por hora: Azure lo factura
/// "at Linux compute rates" y el licenciamiento cliente es por usuario (BYOL), aparte. Cobrarle el
/// premium de Windows Server (como antes) sobreestimaba su costo. Ver docs Azure Multitenant Hosting.
///
/// Reglas:
/// - payg_hourly = precio API según armSkuName + región + OS.
/// - Si VM apagada: PAYG referencial completo SIN RI (ri_applies=false); ya no se cortocircuita a $0 (decisión 2026-07-08).
/// - SQL VM add-on se suma al PAYG y al RI (no se descuenta con RI).
/// - RI 1Y / 3Y prorrateo mensual + windows premium si aplica.
/// </summary>
public sealed class ComputeVmCalculator : ICostCalculator
{
    private readonly IPriceRepository _prices;
    private readonly IPricingConstants _constants;

    // Regex equivalente a re.search(r"_([A-Z]+)?(\d+)", vm_size): grupo 2 = primer número.
    private static readonly Regex VcpuFromSizeRegex = new(@"_([A-Z]+)?(\d+)", RegexOptions.Compiled);

    public ComputeVmCalculator(IPriceRepository prices, IPricingConstants constants)
    {
        _prices = prices;
        _constants = constants;
    }

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        var hours = _constants.HoursPerMonth();

        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "vms");

            // vm_size = r.get("vm_size") or r.get("size_name")
            var vmSize = FirstNonEmpty(r.GetString("vm_size"), r.GetString("size_name"));
            var location = r.GetString("location");
            // os_type = r.get("os_type") or "Windows"
            var osType = FirstNonEmpty(r.GetString("os_type"), "Windows")!;
            // power_state = r.get("power_state") or r.get("status") or ""
            var powerState = FirstNonEmpty(r.GetString("power_state"), r.GetString("status"), "")!;
            var licenseType = r.GetString("os_license_benefit");
            var isAhbWindows = licenseType == "Windows_Server";      // AHB de Windows Server (cliente trae su licencia)
            var isWindowsClient = licenseType == "Windows_Client";   // Win 10/11 con Multitenant Hosting Rights
            // Ambos casos: el cliente aporta la licencia y Azure factura SOLO el compute base = tarifa
            // Linux (sin premium de licencia de Windows Server). La regla de facturación de Azure se
            // dispara por este mismo atributo licenseType: Windows Server → AHB; Windows_Client → un OS
            // cliente NUNCA lleva licencia de Windows Server por hora (se cobra "at Linux compute rates";
            // el licenciamiento cliente es por usuario, aparte). Ver docs Azure Multitenant Hosting Rights.
            var noWindowsServerLicense = isAhbWindows || isWindowsClient;

            var region = NormalizeRegion(location);

            // VM no encendida: ya NO se cortocircuita a $0 (decisión 2026-07-08). Sigue el flujo
            // normal de pricing para obtener un PAYG referencial completo, y al final se marca
            // como no elegible a RI (no se recomienda reservar una máquina apagada).
            var isOff = !IsRunning(powerState);

            // not vm_size or not region (truthy estilo Python: cadena vacía o null => falsy)
            if (string.IsNullOrEmpty(vmSize) || string.IsNullOrEmpty(region))
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = "Missing vm_size or location";
                results.Add(result);
                continue;
            }

            // Para VMs Windows SIN beneficio de licencia (ni AHB Server ni Windows_Client), necesitamos
            // AMBOS precios para calcular el premium de licencia de Windows Server.
            var isWindows = osType.ToLowerInvariant() == "windows";
            var needsWindowsPremium = isWindows && !noWindowsServerLicense;

            // OS de tarificación del PAYG. Con beneficio de licencia (AHB Server o Windows_Client) el
            // cliente aporta su propia licencia y Azure factura SOLO el compute base = tarifa Linux de la
            // misma SKU. El beneficio no está representado como meter en la Azure Retail Prices API: pedir
            // "Windows" devolvería el meter CON licencia de Windows Server (inflando el costo por el
            // premium). Por eso estas VMs se cotizan a la tarifa Linux. Las VMs Linux nativas conservan su
            // OS; Windows SIN beneficio usa la rama de premium.
            var paygOs = noWindowsServerLicense ? "Linux" : osType;

            VmPrices winPrices;
            VmPrices lnxPrices;
            try
            {
                if (needsWindowsPremium)
                {
                    // Pedimos Windows (para PAYG) y Linux (para calcular premium y RI base)
                    winPrices = _prices.GetVmPrices(vmSize, region, "Windows");
                    lnxPrices = _prices.GetVmPrices(vmSize, region, "Linux");
                }
                else
                {
                    // AHB activo (paygOs="Linux", compute base sin premium de licencia) o VM Linux nativa.
                    var primary = _prices.GetVmPrices(vmSize, region, paygOs);
                    winPrices = primary;
                    lnxPrices = primary;
                }
            }
            catch (Exception ex)
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Price lookup error: {ex.GetType().Name}";
                results.Add(result);
                continue;
            }

            // PAYG. En la rama sin premium se reconsulta get_vm_prices con el OS de tarificación
            // (Linux si AHB, para que la VM con beneficio híbrido no pague el premium de licencia).
            var primaryPrices = needsWindowsPremium ? null : _prices.GetVmPrices(vmSize, region, paygOs);
            double? paygHourly = needsWindowsPremium
                ? winPrices.PaygHourly
                : primaryPrices!.PaygHourly;

            if (paygHourly is null)
            {
                // Si Windows no se encontró, intentar Linux (fallback)
                if (needsWindowsPremium && lnxPrices.PaygHourly is not null)
                {
                    paygHourly = lnxPrices.PaygHourly;
                    result.CalculationNotes = "Sin precio Windows; usando Linux como fallback";
                }
                else
                {
                    result.CalculationStatus = "manual_required";
                    result.CalculationNotes =
                        $"No hubo candidato real en Azure Retail Prices API para VM " +
                        $"sku={vmSize} region={region} os={osType}. Requiere validacion manual.";
                    results.Add(result);
                    continue;
                }
            }

            var basePaygMonthly = paygHourly.Value * hours;

            // Windows License Premium (solo si Windows sin AHB y tenemos ambos precios)
            var windowsPremiumMonthly = 0.0;
            if (needsWindowsPremium)
            {
                var winHr = winPrices.PaygHourly;
                var lnxHr = lnxPrices.PaygHourly;
                if (winHr is not null && lnxHr is not null && winHr > lnxHr)
                {
                    windowsPremiumMonthly = (winHr.Value - lnxHr.Value) * hours;
                }
            }

            // RI base (Linux, OS-agnostic)
            var ri1yTotal = lnxPrices.Ri1yTotal;
            var ri3yTotal = lnxPrices.Ri3yTotal;

            // SQL add-on
            var sqlEdition = r.GetString("sql_image_sku");
            var sqlLicense = r.GetString("sql_license_type");
            // vcpus = r.get("vcpu_count") or _vcpu_count_from_size(vm_size)
            var vcpus = r.GetInt("vcpu_count") ?? VcpuCountFromSize(vmSize);

            var sqlAddonMonthly = 0.0;
            // if sql_edition and vcpus: (truthy => no nulo/no vacío y vcpus != 0)
            if (!string.IsNullOrEmpty(sqlEdition) && vcpus is not null && vcpus.Value != 0)
            {
                var addonPerVcoreHr = _constants.SqlAddonPerVcoreHour(sqlEdition, sqlLicense ?? "");
                sqlAddonMonthly = addonPerVcoreHr * vcpus.Value * hours;
            }

            // PAYG total
            result.PaygHourly = paygHourly.Value + (sqlAddonMonthly != 0.0 ? sqlAddonMonthly / hours : 0);
            result.PaygMonthly = basePaygMonthly + sqlAddonMonthly;
            result.SqlAddonMonthly = sqlAddonMonthly > 0 ? sqlAddonMonthly : null;
            result.PaygMeterId = needsWindowsPremium
                ? (winPrices.PaygHourly is not null ? winPrices.PaygMeterId : lnxPrices.PaygMeterId)
                : primaryPrices!.PaygMeterId;

            // RI total = (RI_base / N años) + Windows premium + SQL add-on
            // SQL y Windows premium NO se descuentan con RI: se siguen pagando por hora.
            // VM apagada: sin RI — el PAYG es referencial y no se recomienda reservar.
            if (isOff)
            {
                result.RiApplies = false;
                result.RiNotApplicableReason = "VM apagada al momento del análisis";
            }
            else
            {
                if (ri1yTotal is not null)
                {
                    result.Ri1yMonthly = (ri1yTotal.Value / 12.0) + windowsPremiumMonthly + sqlAddonMonthly;
                }
                if (ri3yTotal is not null)
                {
                    result.Ri3yMonthly = (ri3yTotal.Value / 36.0) + windowsPremiumMonthly + sqlAddonMonthly;
                }
                result.RiApplies = true;
            }
            result.ComputeSavings();
            result.DiscardNonSavingRi();

            // Notes
            var notes = new List<string>();
            if (isOff)
            {
                var stateLabel = string.IsNullOrEmpty(powerState) ? "desconocido" : powerState;
                notes.Add($"VM apagada — costo PAYG referencial (power state: {stateLabel})");
            }
            if (isAhbWindows)
            {
                notes.Add("Windows AHB activo (sin premium)");
            }
            else if (isWindowsClient)
            {
                notes.Add("Windows cliente (multitenant hosting): sin cargo de licencia Windows Server");
            }
            else if (needsWindowsPremium && windowsPremiumMonthly > 0)
            {
                notes.Add($"Windows premium: +${windowsPremiumMonthly.ToString("F2", CultureInfo.InvariantCulture)}/mes (sin AHB)");
            }
            if (sqlAddonMonthly > 0)
            {
                notes.Add($"SQL {sqlEdition} {sqlLicense}: +${sqlAddonMonthly.ToString("F2", CultureInfo.InvariantCulture)}/mes");
            }
            if (notes.Count > 0)
            {
                var existing = result.CalculationNotes ?? "";
                result.CalculationNotes = string.Join("; ", notes)
                    + (existing.Length > 0 ? " | " + existing : "");
            }

            // match_strategy = win_prices.get("match_strategy") or lnx_prices.get("match_strategy") or "deterministic"
            var matchStrategy = FirstNonEmpty(winPrices.MatchStrategy, lnxPrices.MatchStrategy, "deterministic")!;
            result.CalculationNotes =
                (!string.IsNullOrEmpty(result.CalculationNotes) ? result.CalculationNotes + " | " : "")
                + $"sku={vmSize} region={region} os={osType} match={matchStrategy}";

            results.Add(result);
        }

        return results;
    }

    // ---------------- helpers (port de las funciones módulo de compute_vm.py) ----------------

    private static string NormalizeRegion(string? location)
    {
        if (string.IsNullOrEmpty(location)) return "";
        return location.ToLowerInvariant().Replace(" ", "");
    }

    private static bool IsRunning(string? powerState)
    {
        if (string.IsNullOrEmpty(powerState)) return false;
        return powerState.ToLowerInvariant().Contains("running");
    }

    private static int? VcpuCountFromSize(string? vmSize)
    {
        if (string.IsNullOrEmpty(vmSize)) return null;
        var match = VcpuFromSizeRegex.Match(vmSize);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }
            return null;
        }
        return null;
    }

    /// <summary>Imita el encadenado <c>a or b or c</c> de Python sobre cadenas (vacío/null = falsy).</summary>
    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c)) return c;
        }
        return candidates.Length > 0 ? candidates[^1] : null;
    }
}
