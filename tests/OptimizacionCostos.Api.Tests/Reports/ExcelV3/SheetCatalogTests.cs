using OptimizacionCostos.Api.Features.Reports.ExcelV3;
namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;
public class SheetCatalogTests
{
    // Hojas cuyos recursos NO son elegibles a RI por naturaleza → sin bloque RI/ahorro/elegible
    // (discos de VMs apagadas, discos huérfanos/ASR, IP pública). Decisión del usuario 2026-07-07.
    private static readonly HashSet<string> NoRiKeys = new(StringComparer.OrdinalIgnoreCase)
        { "disks_stopped_vms", "disks_orphan_asr", "public_ip" };

    [Fact]
    public void Todas_las_hojas_de_servicio_llevan_PAYG_estado_y_totales()
    {
        foreach (var (key, spec) in SheetCatalog.ServiceSheets())
        {
            var roles = spec.Columns.Select(c => c.Role).ToList();
            Assert.Contains(MoneyRole.Payg, roles);                 // PAYG siempre presente
            Assert.Contains(spec.Columns, c => c.Header == "Estado del cálculo");
            Assert.True(spec.ComparableTotals, $"{key} sin totales comparables");
        }
    }

    [Fact]
    public void Ninguna_hoja_lleva_columna_Nota()
    {
        // "Nota" (texto genérico por recurso) se eliminó de todo el entregable (decisión usuario 2026-07-07).
        foreach (var (key, spec) in SheetCatalog.ServiceSheets())
            Assert.DoesNotContain(spec.Columns, c => c.Header == "Nota");
        Assert.DoesNotContain(SheetCatalog.ResourceDetailSheet().Columns, c => c.Header == "Nota");
    }

    [Fact]
    public void Hojas_elegibles_a_RI_llevan_bloque_RI_y_las_no_elegibles_no()
    {
        foreach (var (key, spec) in SheetCatalog.ServiceSheets())
        {
            var roles = spec.Columns.Select(c => c.Role).ToList();
            var tieneRi = roles.Contains(MoneyRole.Ri1) && roles.Contains(MoneyRole.Ri3);
            var tieneAhorro = roles.Contains(MoneyRole.Savings1Usd) && roles.Contains(MoneyRole.Savings3Usd);
            var tieneElegible = spec.Columns.Any(c => c.Kind == ColKind.Eligibility);
            var tieneReservado = spec.Columns.Any(c => c.Header == "Reservado");

            if (NoRiKeys.Contains(key))
            {
                // Recurso no reservable: sin RI 1A/3A, sin ahorros, sin Elegible y sin Reservado (solo ruido).
                Assert.False(tieneRi, $"{key} NO debe llevar columnas RI 1A/3A");
                Assert.False(tieneAhorro, $"{key} NO debe llevar columnas de ahorro RI");
                Assert.False(tieneElegible, $"{key} NO debe llevar columna 'Elegible a RI'");
                Assert.False(tieneReservado, $"{key} NO debe llevar columna 'Reservado'");
            }
            else
            {
                Assert.True(tieneRi, $"{key} debe llevar columnas RI 1A/3A");
                Assert.True(tieneAhorro, $"{key} debe llevar columnas de ahorro RI");
                Assert.True(tieneElegible, $"{key} debe llevar columna 'Elegible a RI'");
                Assert.True(tieneReservado, $"{key} debe llevar columna 'Reservado'");
            }
        }
    }

    [Fact]
    public void Nombres_de_hoja_en_espanol_y_unicos()
    {
        var names = SheetCatalog.ServiceSheets().Select(s => s.Spec.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("Optimización VMs", names);
        Assert.Contains("IP Pública", names);
        Assert.DoesNotContain(names, n => n.Contains("Managed Instance") && n.Contains("PAYG")); // nada en inglés
    }

    [Fact]
    public void Generica_sirve_para_synapse()
    {
        var spec = SheetCatalog.GenericServiceSheet("Synapse Dedicated Pool");
        Assert.Equal("Synapse Dedicated Pool", spec.Name);
        Assert.Contains(spec.Columns, c => c.Role == MoneyRole.Payg);
    }

    [Fact]
    public void Hojas_elegibles_y_detalle_llevan_columna_Elegible_a_RI()
    {
        foreach (var (key, spec) in SheetCatalog.ServiceSheets().Where(s => !NoRiKeys.Contains(s.DataKey)))
            Assert.Contains(spec.Columns, c => c.Header == "Elegible a RI" && c.Kind == ColKind.Eligibility);
        // El "Detalle de recursos" mezcla todos los servicios → conserva el bloque RI completo.
        Assert.Contains(SheetCatalog.ResourceDetailSheet().Columns, c => c.Header == "Elegible a RI");
    }
}
