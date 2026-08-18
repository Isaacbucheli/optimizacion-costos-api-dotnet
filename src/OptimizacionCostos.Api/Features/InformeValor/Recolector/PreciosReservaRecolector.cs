using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>Precio mensual PAYG y RI de un SKU de VM, ya mensualizado, para el respaldo de
/// reservas desde el archivo (entrega 8, pieza A). Lo produce el recolector (IO) y lo consume
/// la calculadora pura de la Tarea 2 como dato.</summary>
public sealed record PrecioReservaVm(decimal PaygMensual, decimal RiMensual);

/// <summary>Consulta el catálogo de precios propio (<see cref="IPriceRepository"/>, caché SQL
/// sobre Azure Retail) para las líneas de reserva del archivo de evolución. IO: vive en
/// Recolector, nunca en Calculo. Solo lo llama el controller cuando la foto de Azure NO midió
/// (la foto es la autoridad, decisión 2026-08-18 — con foto medida el respaldo no corre y este
/// recolector ni se invoca).
///
/// <para><b>PAYG con osType "Linux" a propósito</b>: sin foto no se sabe el OS de las VMs
/// cubiertas, y el PAYG Linux subestima el ahorro de las Windows sin AHB en vez de inflarlo —
/// mismo criterio que el fix de AHB (2026-07-03): ante ambigüedad, la cifra menor es la
/// defendible.</para>
///
/// <para><b>La región del archivo es display ("US East 2"); el catálogo espera ARM
/// ("eastus2").</b> Mismo criterio de tokens que <c>ReservasFacturadasCalculador.RegionCoincide</c>
/// (todos los tokens del display contenidos en el nombre ARM), más la preferencia por el match
/// exacto de longitud para que "US East" no matchee también eastus2. Sin resolución única: null,
/// y la línea queda sin precio — la fila se publica con su cargo y sin monto (regla de la spec),
/// nunca con un precio de una región adivinada (D9).</para></summary>
public static class PreciosReservaRecolector
{
    /// <summary>La misma convención de horas/mes del motor de costos (730).</summary>
    public const decimal HorasMes = 730m;

    /// <summary>Regiones ARM públicas que este respaldo sabe resolver. Lista cerrada a
    /// propósito: una región fuera de ella deja la línea sin precio Y declarada, nunca un
    /// match adivinado.</summary>
    private static readonly string[] RegionesArm =
    [
        "eastus", "eastus2", "westus", "westus2", "westus3", "centralus", "northcentralus",
        "southcentralus", "westcentralus", "canadacentral", "canadaeast", "brazilsouth",
        "northeurope", "westeurope", "uksouth", "ukwest", "francecentral", "germanywestcentral",
        "swedencentral", "switzerlandnorth", "norwayeast", "italynorth", "spaincentral",
        "polandcentral", "eastasia", "southeastasia", "japaneast", "japanwest", "koreacentral",
        "australiaeast", "australiasoutheast", "centralindia", "southindia", "westindia",
        "uaenorth", "southafricanorth", "israelcentral", "qatarcentral", "mexicocentral",
        "chilecentral",
    ];

    public static string? ResolverRegionArm(string regionEvolucion)
    {
        var tokens = regionEvolucion.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;

        var candidatas = RegionesArm.Where(arm => tokens.All(arm.Contains)).ToList();
        if (candidatas.Count == 1) return candidatas[0];
        if (candidatas.Count == 0) return null;

        // 2+ candidatas: gana la concatenación exacta ("us"+"east" cubre eastus, largo 6; eastus2
        // tiene largo 7 y queda fuera). Si ni así queda una sola, no se adivina.
        var largoExacto = tokens.Sum(t => t.Length);
        var exactas = candidatas.Where(c => c.Length == largoExacto).ToList();
        return exactas.Count == 1 ? exactas[0] : null;
    }

    /// <summary>La clave del diccionario de precios: la misma que usa la calculadora de la
    /// Tarea 2 para buscar el precio de cada línea. Minúsculas para que la capitalización del
    /// export no separe lo que es el mismo SKU.</summary>
    public static string Clave(string sku, string region, string termIso) =>
        sku.ToLowerInvariant() + "|" + region.ToLowerInvariant() + "|" + termIso.ToLowerInvariant();

    public static IReadOnlyDictionary<string, PrecioReservaVm> Resolver(
        IPriceRepository precios, IReadOnlyCollection<(string Sku, string Region, string TermIso)> lineas)
    {
        var resultado = new Dictionary<string, PrecioReservaVm>();
        foreach (var (sku, region, termIso) in lineas.DistinctBy(l => Clave(l.Sku, l.Region, l.TermIso)))
        {
            var regionArm = ResolverRegionArm(region);
            if (regionArm is null) continue;

            var vm = precios.GetVmPrices(sku, regionArm, "Linux");
            if (vm.PaygHourly is not > 0) continue;

            // P5Y no existe para VMs en el catálogo: la línea queda sin precio, declarada por la
            // calculadora (regla 6 de la Tarea 2), nunca aproximada con otro término.
            decimal? riMensual = termIso.ToUpperInvariant() switch
            {
                "P1Y" => vm.Ri1yTotal is > 0 ? (decimal)vm.Ri1yTotal.Value / 12m : null,
                "P3Y" => vm.Ri3yTotal is > 0 ? (decimal)vm.Ri3yTotal.Value / 36m : null,
                _ => null,
            };
            if (riMensual is not { } ri) continue;

            resultado[Clave(sku, region, termIso)] = new PrecioReservaVm(
                PaygMensual: (decimal)vm.PaygHourly.Value * HorasMes, RiMensual: ri);
        }
        return resultado;
    }
}
