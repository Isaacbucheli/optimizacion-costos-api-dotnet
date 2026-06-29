using OptimizacionCostos.Api.Features.Cdc;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Cdc;

/// <summary>
/// Lógica pura de B5 (sin Azure ni BD): algoritmo de uptime, cruce/agregación de RI y parseos.
/// Replican el comportamiento de power_history.py / coverage.py / reservations.py.
/// </summary>
public sealed class CdcLogicTests
{
    private static DateTimeOffset T(int day, int hour = 0) => new(2026, 5, day, hour, 0, 0, TimeSpan.Zero);

    // -------------------- power history --------------------

    [Fact]
    public void PreviousCalendarMonth_DevuelveMesAnteriorUtc()
    {
        var (start, end) = PowerHistoryService.PreviousCalendarMonth(new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void ComputeUptime_SinEventos_UsaEstadoActual()
    {
        var start = T(1); var end = T(31);
        var running = PowerHistoryService.ComputeUptime([], true, start, end);
        Assert.Equal(100.0, running.UptimePct);
        Assert.Equal(720.0, running.RunningHours); // 30 días * 24h
        var stopped = PowerHistoryService.ComputeUptime([], false, start, end);
        Assert.Equal(0.0, stopped.UptimePct);
        Assert.Equal(0.0, stopped.RunningHours);
    }

    [Fact]
    public void ComputeUptime_StopLuegoStart_CuentaSegmentosEncendidos()
    {
        // Ventana de 10 días. stop el día 3, start el día 8 → encendida [1..3] y [8..11) = 2+3 = 5 días.
        var start = T(1); var end = T(11);
        var events = new List<PowerHistoryService.PowerEvent>
        {
            new("/vm/x", "stop", T(3), null),
            new("/vm/x", "start", T(8), null),
        };
        var r = PowerHistoryService.ComputeUptime(events, true, start, end);
        Assert.Equal(5 * 24.0, r.RunningHours, 3);
        Assert.Equal(50.0, r.UptimePct, 3);
    }

    [Fact]
    public void NormalizeEvent_SoloEnergiaSucceeded()
    {
        var ok = System.Text.Json.JsonDocument.Parse("""
            {"operationName":{"value":"Microsoft.Compute/virtualMachines/start/action"},"status":{"value":"Succeeded"},
             "resourceId":"/subscriptions/s/VM-A","eventTimestamp":"2026-05-10T03:00:00Z","caller":"x@y.com"}
            """).RootElement;
        var ev = PowerHistoryService.NormalizeEvent(ok);
        Assert.NotNull(ev);
        Assert.Equal("start", ev!.Kind);
        Assert.Equal("/subscriptions/s/vm-a", ev.ResourceId); // normalizado a lower

        var notSucceeded = System.Text.Json.JsonDocument.Parse("""
            {"operationName":{"value":"...start/action"},"status":{"value":"Started"},"resourceId":"/x","eventTimestamp":"2026-05-10T03:00:00Z"}
            """).RootElement;
        Assert.Null(PowerHistoryService.NormalizeEvent(notSucceeded));
    }

    // -------------------- RI coverage --------------------

    [Fact]
    public void MatchConfirmed_UnicoPorRecurso()
    {
        var map = new Dictionary<string, int> { ["/vm/a"] = 10, ["/vm/b"] = 20 };
        var rsv = new List<RiCoverageService.RsvWithConsumers>
        {
            new("R1", "P1Y", "VM", "eastus", 2, ["/VM/A", "/VM/B"]),
            new("R2", "P3Y", "VM", "eastus", 1, ["/VM/A"]), // ya cubierto → no se duplica
        };
        var confirmed = RiCoverageService.MatchConfirmed(rsv, map);
        Assert.Equal(2, confirmed.Count);
        Assert.Equal([10, 20], confirmed.Select(c => c.ResourceId).OrderBy(x => x));
    }

    [Fact]
    public void AggregateReservations_RestaConfirmadosDelReservado()
    {
        var rsv = new List<RiCoverageService.RsvWithConsumers>
        {
            new("R1", "P1Y", "VM Dv5", "eastus", 3, []),
            new("R2", "P1Y", "VM Dv5", "eastus", 2, []), // mismo grupo → reserved 5
        };
        var confirmedByGroup = new Dictionary<(string, string, string), int> { [("vm dv5", "eastus", "p1y")] = 2 };
        var agg = RiCoverageService.AggregateReservations(rsv, confirmedByGroup);
        var g = Assert.Single(agg);
        Assert.Equal(5, g["reserved"]);
        Assert.Equal(2, g["confirmed"]);
        Assert.Equal(3, g["estimated_units"]); // 5 - 2
    }

    // -------------------- reservations parsing --------------------

    [Theory]
    [InlineData("2026-08-15T00:00:00Z", 2026, 8, 15)]
    [InlineData("2026-08-15", 2026, 8, 15)]
    public void ParseExpiry_AceptaIsoYFecha(string raw, int y, int m, int d)
    {
        var parsed = AzureReservationsClient.ParseExpiry(raw);
        Assert.Equal(new DateOnly(y, m, d), parsed);
    }

    [Fact]
    public void ParseInstanceId_ExtraeSubGrupoNombre()
    {
        var (sub, rg, name) = AzureReservationsClient.ParseInstanceId(
            "/subscriptions/abc/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/VM-1");
        Assert.Equal("abc", sub);
        Assert.Equal("rg1", rg);
        Assert.Equal("VM-1", name);
    }
}
