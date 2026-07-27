using ClosedXML.Excel;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public class AccessReviewExcelExporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    private static AccessReviewSnapshot Snapshot() =>
        new(new AccessRunRef(1, 10, "ok", Now, Now, null, null),
            [new(1, "cred", "ok", "ok", null)],
            [new("s1", "Sub Uno", "Enabled", "/subscriptions/s1", "subscription", "Owner", "def",
                 "u1", "User", "Ana", "ana@x.com", "Member", null, null, true, Now.AddDays(-3), "enabled",
                 "owner", false)],
            [new("g1", "Guest Uno", "g1@ext.com", "ext.com", true, "Accepted", null, Now.AddDays(-100), "Reader (Sub Uno)", "disabled")],
            [new("a1", "Admin Uno", "a1@x.com", "Member", true, Now.AddDays(-1), "enabled")]);

    private static XLWorkbook Generate(AccessReviewSnapshot snapshot, out string fileName)
    {
        var accounts = AccessReviewAccountBuilder.Build(snapshot);
        var kpis = AccessReviewKpiCalculator.Compute(snapshot, accounts, 90, Now);
        var findings = AccessReviewFindingsBuilder.Build(snapshot, accounts, kpis, 90, Now);
        var result = new AccessReviewExcelExporter().Generate("Cliente Demo", snapshot, accounts, kpis, findings, [], 90);
        fileName = result.FileName;
        return new XLWorkbook(new MemoryStream(result.Bytes));
    }

    [Fact]
    public void Genera_ocho_hojas_con_datos()
    {
        using var wb = Generate(Snapshot(), out var fileName);

        Assert.EndsWith(".xlsx", fileName);
        Assert.Equal(8, wb.Worksheets.Count);
        Assert.True(wb.Worksheets.Contains("Resumen"));
        Assert.True(wb.Worksheets.Contains("Hallazgos"));
        Assert.True(wb.Worksheets.Contains("Cuentas"));
        Assert.True(wb.Worksheets.Contains("Asignaciones RBAC"));
        Assert.True(wb.Worksheets.Contains("Global Administrators"));
        Assert.True(wb.Worksheets.Contains("Guests"));
        Assert.True(wb.Worksheets.Contains("Service Principals"));
        // Fila de datos en Asignaciones: header en fila 1, primer dato fila 2, "Nombre" es la 8ª columna.
        Assert.Equal("Ana", wb.Worksheet("Asignaciones RBAC").Cell(2, 8).GetString());
    }

    [Fact]
    public void Hoja_de_asignaciones_trae_clase_de_rol()
    {
        using var wb = Generate(Snapshot(), out _);

        var ws = wb.Worksheet("Asignaciones RBAC");
        Assert.Equal("Clase de rol", ws.Cell(1, 5).GetString());
        Assert.Equal("Owner (otorga accesos)", ws.Cell(2, 5).GetString());
    }

    [Fact]
    public void Hoja_de_cuentas_agrega_por_principal()
    {
        using var wb = Generate(Snapshot(), out _);

        var ws = wb.Worksheet("Cuentas");
        Assert.Equal("Cuenta", ws.Cell(1, 1).GetString());
        Assert.Equal("Ana", ws.Cell(2, 1).GetString());
        Assert.Equal("Interna", ws.Cell(2, 3).GetString());
        Assert.Equal(1, ws.Cell(2, 4).GetValue<int>());   // total de asignaciones
        Assert.Equal(1, ws.Cell(2, 5).GetValue<int>());   // Owner
        Assert.Equal("directo", ws.Cell(2, 13).GetString());
    }

    [Fact]
    public void El_bloque_de_credenciales_no_pisa_los_kpis()
    {
        // El ancla del bloque de credenciales se calcula desde la cantidad de KPIs: si vuelve a
        // quedar fija, agregar indicadores lo hace chocar con las últimas filas del resumen.
        using var wb = Generate(Snapshot(), out _);

        var ws = wb.Worksheet("Resumen");
        int Fila(string label) => Enumerable.Range(1, 60).FirstOrDefault(r => ws.Cell(r, 1).GetString() == label);

        var etiqueta = Fila("Estado por credencial:");
        var ultimoKpi = Fila("Service principals únicos");

        Assert.True(etiqueta > 0, "No se encontró el bloque de estado por credencial.");
        Assert.True(ultimoKpi > 0, "No se encontró el último KPI del resumen.");
        Assert.True(ultimoKpi < etiqueta,
            $"El último KPI (fila {ultimoKpi}) se pisa con el bloque de credenciales (fila {etiqueta}).");
        Assert.Equal("cred", ws.Cell(etiqueta + 1, 1).GetString());
    }

    [Fact]
    public void Hoja_de_hallazgos_lista_severidad_y_recomendacion()
    {
        using var wb = Generate(Snapshot(), out _);

        var ws = wb.Worksheet("Hallazgos");
        Assert.Equal("Severidad", ws.Cell(1, 1).GetString());
        Assert.Equal("Recomendación", ws.Cell(1, 4).GetString());
        // 16 reglas + encabezado: los limpios también se listan (saber que se evaluó es información).
        Assert.Equal(17, ws.LastRowUsed()!.RowNumber());
        // Owner a nivel suscripción no dispara el hallazgo de raíz, pero la fila existe igual.
        Assert.Contains("Crítica", ws.Column(1).CellsUsed().Select(c => c.GetString()));
    }

    [Fact]
    public void Un_hallazgo_no_evaluable_no_muestra_conteos()
    {
        // Sin Graph, las reglas de directorio salen sin cifras en vez de con cero.
        var sinGraph = Snapshot() with { Credentials = [new(1, "cred", "ok", "sin_consent", null)] };

        using var wb = Generate(sinGraph, out _);

        var ws = wb.Worksheet("Hallazgos");
        var noEvaluados = ws.RowsUsed().Skip(1).Where(r => r.Cell(7).GetString() == "No").ToList();
        Assert.NotEmpty(noEvaluados);
        Assert.All(noEvaluados, r => Assert.Equal("", r.Cell(5).GetString()));
    }

    [Fact]
    public void Eje_externo_sin_medir_queda_vacio_no_como_interna()
    {
        var sinGraph = Snapshot() with { Credentials = [new(1, "cred", "ok", "sin_consent", null)] };

        using var wb = Generate(sinGraph, out _);

        Assert.Equal("", wb.Worksheet("Cuentas").Cell(2, 3).GetString());
    }
}
