using System.Collections;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de Managed Disks. Port 1:1 de
/// app/calculators/managed_disk.py::ManagedDiskCalculator (service_key "disks").
///
/// FIX v0.5.3: Azure Retail Prices usa skuName tipo "E10 LRS" (con sufijo de
/// redundancia), no solo "E10". Por eso derivamos el tier completo con sufijo.
///
/// Reglas:
/// - Precio mensual por tier (S10 LRS, P10 LRS, E10 LRS, etc.).
/// - No aplica RI.
/// - Categorización en notes:
///     - 'attached'           → disco usado por VM running (se descarta, no se reporta).
///     - 'attached_stopped'   → disco de VM apagada (candidato a depurar).
///     - 'orphan'             → disco sin VM asociada (huérfano).
///     - 'asr'                → disco ASR (no eliminar, replicación DR; se descarta).
/// </summary>
public sealed class ManagedDiskCalculator(IPriceRepository prices, IPricingConstants constants) : ICostCalculator
{
    private readonly IPriceRepository _prices = prices;
    // Nota: la lógica de discos no usa constantes (no hay horas/mes ni RI), pero se inyecta
    // por uniformidad con el resto de calculadoras y para mantener el contrato del motor.
    private readonly IPricingConstants _constants = constants;

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();

        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "disks");

            var diskSku = r.GetString("disk_sku");
            var diskSizeGb = r.GetInt("disk_size_gb");
            var location = r.GetString("location");
            // r.get("resource_name") or "" → cadena vacía si ausente/null.
            var diskName = r.GetString("resource_name") ?? "";
            var attachedVm = r.GetString("attached_vm_name");

            if (IsAsr(diskName))
            {
                continue;
            }

            string category;
            // En Python: if attached_vm: → truthy (no None ni cadena vacía).
            if (!string.IsNullOrEmpty(attachedVm))
            {
                var stoppedVms = GetStoppedVmNamesLower(r);
                // if stopped_vms and attached_vm.lower() in stopped_vms:
                if (stoppedVms.Count > 0 && stoppedVms.Contains(attachedVm.ToLowerInvariant()))
                {
                    category = "attached_stopped";
                }
                else
                {
                    // Disco adjunto a VM encendida → no se reporta.
                    continue;
                }
            }
            else
            {
                category = "orphan";
            }

            var region = NormalizeRegion(location);
            var tier = DeriveDiskTier(diskSku, diskSizeGb);

            if (string.IsNullOrEmpty(tier) || string.IsNullOrEmpty(region))
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Cannot derive tier for sku={diskSku} size={diskSizeGb}";
                results.Add(result);
                continue;
            }

            double? monthly;
            try
            {
                monthly = _prices.GetDiskPrice(tier, region);
            }
            catch (Exception ex)
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Price lookup error: {ex.GetType().Name}";
                results.Add(result);
                continue;
            }

            if (monthly is null)
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"No price for tier={tier} region={region}";
                results.Add(result);
                continue;
            }

            result.PaygMonthly = monthly;
            result.RiApplies = false;
            result.RiNotApplicableReason = "Disks no soportan Reserved Instances";

            result.CalculationNotes = $"category={category}, tier={tier}";

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Lee el set "_stopped_vm_names_lower" del recurso. En Python es
    /// <c>r.get("_stopped_vm_names_lower", set())</c>; aquí devolvemos un conjunto
    /// (vacío si la clave no existe o no es una colección).
    /// </summary>
    private static HashSet<string> GetStoppedVmNamesLower(ResourceRow r)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (r.Raw.TryGetValue("_stopped_vm_names_lower", out var v) && v is IEnumerable items and not string)
        {
            foreach (var item in items)
            {
                if (item is not null)
                {
                    set.Add(item.ToString() ?? "");
                }
            }
        }
        return set;
    }

    private static string NormalizeRegion(string? location)
    {
        if (string.IsNullOrEmpty(location))
        {
            return "";
        }
        return location.ToLowerInvariant().Replace(" ", "");
    }

    /// <summary>
    /// Extrae el sufijo de redundancia (LRS, ZRS, GRS) del disk_sku.
    /// Ej: 'StandardSSD_LRS' → 'LRS', 'Premium_ZRS' → 'ZRS'. Default: LRS.
    /// </summary>
    private static string DeriveRedundancySuffix(string? diskSku)
    {
        if (string.IsNullOrEmpty(diskSku))
        {
            return "LRS";
        }
        var parts = diskSku.Split('_');
        if (parts.Length >= 2)
        {
            var suffix = parts[^1].ToUpperInvariant();
            if (suffix is "LRS" or "ZRS" or "GRS" or "RAGRS" or "GZRS" or "RAGZRS")
            {
                return suffix;
            }
        }
        return "LRS";
    }

    /// <summary>
    /// Deriva el tier (S10 LRS, P10 LRS, E10 LRS, etc.) a partir del SKU + tamaño.
    /// Formato Azure Retail Prices: '&lt;Letter&gt;&lt;Number&gt; &lt;Suffix&gt;'.
    /// </summary>
    private static string? DeriveDiskTier(string? diskSku, int? sizeGb)
    {
        // if not disk_sku or not size_gb: → size_gb=0 también cuenta como falsy en Python.
        if (string.IsNullOrEmpty(diskSku) || sizeGb is null or 0)
        {
            return null;
        }

        var skuLower = diskSku.ToLowerInvariant();
        var suffix = DeriveRedundancySuffix(diskSku);

        // Letra base por tipo de disco (orden idéntico al de Python).
        string letter;
        if (skuLower.Contains("premium"))
        {
            letter = "P";
        }
        else if (skuLower.Contains("standardssd"))
        {
            letter = "E";
        }
        else if (skuLower.Contains("standard"))
        {
            letter = "S";
        }
        else if (skuLower.Contains("ultra"))
        {
            return null; // Ultra disks tienen pricing distinto.
        }
        else
        {
            return null;
        }

        // Tabla de tiers Azure por tamaño.
        var size = sizeGb.Value;
        string tierNum;
        if (size <= 4)
        {
            tierNum = "1";
        }
        else if (size <= 8)
        {
            tierNum = "2";
        }
        else if (size <= 16)
        {
            tierNum = "3";
        }
        else if (size <= 32)
        {
            tierNum = "4";
        }
        else if (size <= 64)
        {
            tierNum = "6";
        }
        else if (size <= 128)
        {
            tierNum = "10";
        }
        else if (size <= 256)
        {
            tierNum = "15";
        }
        else if (size <= 512)
        {
            tierNum = "20";
        }
        else if (size <= 1024)
        {
            tierNum = "30";
        }
        else if (size <= 2048)
        {
            tierNum = "40";
        }
        else if (size <= 4096)
        {
            tierNum = "50";
        }
        else if (size <= 8192)
        {
            tierNum = "60";
        }
        else if (size <= 16384)
        {
            tierNum = "70";
        }
        else
        {
            tierNum = "80";
        }

        return $"{letter}{tierNum} {suffix}";
    }

    private static bool IsAsr(string diskName)
        => !string.IsNullOrEmpty(diskName) && diskName.ToLowerInvariant().Contains("asrseeddisk");
}
