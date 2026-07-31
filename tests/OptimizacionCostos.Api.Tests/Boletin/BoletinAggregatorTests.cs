using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinAggregatorTests
{
    private static readonly DateOnly Hoy = new(2026, 7, 31);

    private static StoredRetirement Fila(string source, string key, string sub, string? resId,
        DateOnly? fecha, string title = "t") =>
        new("FP-" + key + "-" + (resId ?? "sub"), source, key, sub, resId,
            resId is null ? "" : "recurso", "Tipo/X", key, fecha, title, null, null, null);

    [Fact]
    public void AgrupaPorAnuncioYCuentaRecursos()
    {
        var rows = new List<StoredRetirement>
        {
            Fila("advisor", "Basic SKU", "sub-1", "/s/1/ip1", new DateOnly(2025, 9, 30)),
            Fila("advisor", "Basic SKU", "sub-2", "/s/2/ip2", new DateOnly(2025, 9, 30)),
            Fila("service_health", "TRACK-1", "sub-1", null, new DateOnly(2026, 9, 30)),
        };

        var view = BoletinAggregator.BuildView(rows, subscriptionsTotal: 5, Hoy);
        var kpis = (IReadOnlyDictionary<string, object?>)view["kpis"]!;
        var groups = (List<Dictionary<string, object?>>)view["groups"]!;

        Assert.Equal(2, kpis["announcements"]);
        Assert.Equal(1, kpis["already_retired"]); // Basic SKU venció
        Assert.Equal(1, kpis["due_soon"]);        // TRACK-1 vence en < 6 meses
        Assert.Equal(2, kpis["resources"]);       // solo filas con recurso
        Assert.Equal(2, kpis["subscriptions_impacted"]);
        Assert.Equal(5, kpis["subscriptions_total"]);
        Assert.Equal(2, groups.Count);

        var basic = groups.Single(g => (string?)g["announcement_key"] == "Basic SKU");
        Assert.Equal(2, basic["resource_count"]);
        Assert.Equal(BoletinUrgency.Retirado, basic["urgency"]);
    }

    [Fact]
    public void OrdenaPorFechaConSinFechaAlFinal()
    {
        var rows = new List<StoredRetirement>
        {
            Fila("service_health", "SIN-FECHA", "s", null, null),
            Fila("advisor", "Tarde", "s", "/r/a", new DateOnly(2027, 3, 31)),
            Fila("advisor", "Pronto", "s", "/r/b", new DateOnly(2026, 9, 1)),
        };

        var groups = (List<Dictionary<string, object?>>)BoletinAggregator.BuildView(rows, 1, Hoy)["groups"]!;

        Assert.Equal(new[] { "Pronto", "Tarde", "SIN-FECHA" },
            groups.Select(g => (string?)g["announcement_key"]).ToArray());
    }
}
