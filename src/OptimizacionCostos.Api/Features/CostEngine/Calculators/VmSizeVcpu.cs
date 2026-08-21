using System.Globalization;
using System.Text.RegularExpressions;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>De dónde salió el conteo de vCores, para poder decirlo en las notas del cálculo.</summary>
public enum VcpuSource
{
    /// <summary>No se pudo determinar. El llamador NO debe inventar un número.</summary>
    Unknown,

    /// <summary>vm_details.vcpu_count, poblado en la importación desde Microsoft.Compute/skus.</summary>
    Inventory,

    /// <summary>Número después del guion de un tamaño de núcleo restringido (E32-16s_v3 → 16).</summary>
    ConstrainedSuffix,

    /// <summary>Tabla explícita de familias donde el número del nombre NO es el conteo de vCPU.</summary>
    LegacyTable,

    /// <summary>Primer número del nombre. Correcto en las familias modernas, adivinanza en el resto.</summary>
    SizeName,
}

/// <summary>Conteo de vCores resuelto y de qué fuente salió.</summary>
public readonly record struct VcpuResolution(int? Vcpus, VcpuSource Source)
{
    /// <summary>true cuando el número salió de leer el nombre del tamaño, no de un dato autoritativo.</summary>
    public bool IsDerivedFromName => Source is VcpuSource.SizeName;
}

/// <summary>
/// Resuelve cuántos vCores tiene un tamaño de VM. Existe porque el conteo importa en plata: es el
/// multiplicador de la licencia de SQL Server, que en un servidor Enterprise puede ser el rubro más
/// grande de la VM.
///
/// Historia (bug encontrado 2026-08-21): el cálculo tomaba el conteo del PRIMER NÚMERO del nombre del
/// tamaño con el regex <c>_([A-Z]+)?(\d+)</c>, heredado 1:1 del stack Python. Eso es correcto en las
/// familias modernas, pero se equivoca en dos grupos:
///   - Núcleo restringido (<c>Standard_E32-16s_v3</c>): el nombre lleva los vCores del tamaño base
///     (32) y después del guion los que quedan ACTIVOS (16). Azure licencia SQL por los activos, que
///     es el motivo mismo de existir de estos SKU. Cobrar 32 duplica la licencia.
///   - Familias viejas (<c>Standard_DS11_v2</c> = 2 vCPU, <c>Standard_D5_v2</c> = 16 vCPU,
///     <c>Standard_GS5</c> = 32 vCPU): el número del nombre es un índice de tamaño, no el conteo.
///
/// Orden de resolución, del dato más confiable al menos:
///   1. vcpu_count del inventario (Microsoft.Compute/skus, capacidad vCPUsAvailable).
///   2. El número después del guion, si el tamaño es de núcleo restringido.
///   3. La tabla explícita de familias viejas.
///   4. El primer número del nombre.
///   5. Nada. Se devuelve null a propósito, para que el llamador marque la fila en vez de cobrar
///      un número inventado.
/// </summary>
public static class VmSizeVcpu
{
    /// <summary>
    /// Núcleo restringido: <c>_&lt;letras&gt;&lt;base&gt;-&lt;activos&gt;</c>. Captura los ACTIVOS.
    /// Cubre Standard_E32-16s_v3, Standard_E8-4as_v4, Standard_M128-32ms, Standard_DS13-4_v2.
    /// </summary>
    private static readonly Regex ConstrainedRegex =
        new(@"_[A-Za-z]+\d+-(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Primer número del nombre. Es el regex original, degradado a último recurso.</summary>
    private static readonly Regex FirstNumberRegex =
        new(@"_([A-Z]+)?(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Familias donde el número del nombre NO es el conteo de vCPU. Solo entran las que hace falta
    /// desmentir: en el resto (B, D/E/F/L/M/N v3 en adelante, H, DC) el número sí es el conteo y lo
    /// resuelve <see cref="FirstNumberRegex"/>.
    ///
    /// Un tamaño nuevo de Azure que no esté acá cae al primer número del nombre, igual que antes, pero
    /// ahora el camino normal es el vcpu_count del inventario y esta tabla es solo el respaldo.
    /// </summary>
    private static readonly Dictionary<string, int> LegacySizes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Serie A v1 (el número es índice de tamaño; A8-A11 son de cómputo intensivo).
        ["Standard_A0"] = 1, ["Standard_A1"] = 1, ["Standard_A2"] = 2, ["Standard_A3"] = 4,
        ["Standard_A4"] = 8, ["Standard_A5"] = 2, ["Standard_A6"] = 4, ["Standard_A7"] = 8,
        ["Standard_A8"] = 8, ["Standard_A9"] = 16, ["Standard_A10"] = 8, ["Standard_A11"] = 16,

        // Serie D v1 y DS v1: D3/D4 y la subfamilia 11-14 no coinciden con el nombre.
        ["Standard_D3"] = 4, ["Standard_D4"] = 8,
        ["Standard_D11"] = 2, ["Standard_D12"] = 4, ["Standard_D13"] = 8, ["Standard_D14"] = 16,
        ["Standard_DS3"] = 4, ["Standard_DS4"] = 8,
        ["Standard_DS11"] = 2, ["Standard_DS12"] = 4, ["Standard_DS13"] = 8, ["Standard_DS14"] = 16,

        // Serie D v2 y DS v2.
        ["Standard_D3_v2"] = 4, ["Standard_D4_v2"] = 8, ["Standard_D5_v2"] = 16,
        ["Standard_D11_v2"] = 2, ["Standard_D12_v2"] = 4, ["Standard_D13_v2"] = 8,
        ["Standard_D14_v2"] = 16, ["Standard_D15_v2"] = 20,
        ["Standard_DS3_v2"] = 4, ["Standard_DS4_v2"] = 8, ["Standard_DS5_v2"] = 16,
        ["Standard_DS11_v2"] = 2, ["Standard_DS12_v2"] = 4, ["Standard_DS13_v2"] = 8,
        ["Standard_DS14_v2"] = 16, ["Standard_DS15_v2"] = 20,

        // Serie G y GS (el nombre va de 1 a 5; los vCores van de 2 a 32).
        ["Standard_G1"] = 2, ["Standard_G2"] = 4, ["Standard_G3"] = 8, ["Standard_G4"] = 16, ["Standard_G5"] = 32,
        ["Standard_GS1"] = 2, ["Standard_GS2"] = 4, ["Standard_GS3"] = 8, ["Standard_GS4"] = 16, ["Standard_GS5"] = 32,
    };

    /// <summary>
    /// Resuelve el conteo de vCores. <paramref name="inventoryVcpuCount"/> es vm_details.vcpu_count:
    /// si viene, gana, porque sale de la API de SKUs de ARM.
    /// </summary>
    public static VcpuResolution Resolve(int? inventoryVcpuCount, string? vmSize)
    {
        if (inventoryVcpuCount is > 0)
        {
            return new VcpuResolution(inventoryVcpuCount, VcpuSource.Inventory);
        }

        if (string.IsNullOrEmpty(vmSize))
        {
            return new VcpuResolution(null, VcpuSource.Unknown);
        }

        var constrained = ConstrainedRegex.Match(vmSize);
        if (constrained.Success
            && int.TryParse(constrained.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var active)
            && active > 0)
        {
            return new VcpuResolution(active, VcpuSource.ConstrainedSuffix);
        }

        if (LegacySizes.TryGetValue(vmSize, out var legacy))
        {
            return new VcpuResolution(legacy, VcpuSource.LegacyTable);
        }

        var first = FirstNumberRegex.Match(vmSize);
        if (first.Success
            && int.TryParse(first.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && n > 0)
        {
            return new VcpuResolution(n, VcpuSource.SizeName);
        }

        return new VcpuResolution(null, VcpuSource.Unknown);
    }
}
