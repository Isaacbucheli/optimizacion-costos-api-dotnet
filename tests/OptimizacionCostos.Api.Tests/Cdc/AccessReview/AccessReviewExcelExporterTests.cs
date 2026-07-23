using ClosedXML.Excel;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public class AccessReviewExcelExporterTests
{
    [Fact]
    public void Genera_cinco_hojas_con_datos()
    {
        var now = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        var run = new AccessRunRef(1, 10, "ok", now, now, null, null);
        var snapshot = new AccessReviewSnapshot(run,
            [new(1, "cred", "ok", "ok", null)],
            [new("s1", "Sub Uno", "Enabled", "/subscriptions/s1", "subscription", "Reader", "def",
                 "u1", "User", "Ana", "ana@x.com", "Member", null, null, true, now.AddDays(-3), "enabled")],
            [new("g1", "Guest Uno", "g1@ext.com", "ext.com", true, "Accepted", null, now.AddDays(-100), "Reader (Sub Uno)", "disabled")],
            [new("a1", "Admin Uno", "a1@x.com", "Member", true, now.AddDays(-1), "enabled")]);
        var kpis = AccessReviewKpiCalculator.Compute(snapshot, 90, now);

        var result = new AccessReviewExcelExporter().Generate("Cliente Demo", snapshot, kpis, 90);

        Assert.EndsWith(".xlsx", result.FileName);
        using var wb = new XLWorkbook(new MemoryStream(result.Bytes));
        Assert.Equal(5, wb.Worksheets.Count);
        Assert.True(wb.Worksheets.Contains("Resumen"));
        Assert.True(wb.Worksheets.Contains("Asignaciones RBAC"));
        Assert.True(wb.Worksheets.Contains("Global Administrators"));
        Assert.True(wb.Worksheets.Contains("Guests"));
        Assert.True(wb.Worksheets.Contains("Service Principals"));
        // Fila de datos en Asignaciones: header en fila 1, primer dato fila 2.
        Assert.Equal("Ana", wb.Worksheet("Asignaciones RBAC").Cell(2, 6).GetString());
    }
}
