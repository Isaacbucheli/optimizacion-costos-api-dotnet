using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinAggregatorTests
{
    private static readonly DateOnly Hoy = new(2026, 7, 31);

    private static StoredRetirement Fila(string source, string key, string sub, string? resId,
        DateOnly? fecha, string title = "t") =>
        new("FP-" + key + "-" + (resId ?? "sub"), source, key, sub, resId,
            resId is null ? "" : "recurso", "Tipo/X", key, fecha, title, null, null, null,
            null, null, null);

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

    [Fact]
    public void ExponeTraduccionesDelPrimerRegistroQueLasTenga()
    {
        var conEs = Fila("advisor", "Basic SKU", "sub-1", "/s/1/ip1", new DateOnly(2025, 9, 30))
            with { TitleEs = "Se retira la SKU Básica", RecommendedActionEs = "Migra a Standard" };
        var sinEs = Fila("advisor", "Basic SKU", "sub-2", "/s/2/ip2", new DateOnly(2025, 9, 30));

        var view = BoletinAggregator.BuildView([sinEs, conEs], 5, new DateOnly(2026, 8, 3));
        var g = ((List<Dictionary<string, object?>>)view["groups"]!).Single();

        Assert.Equal("Se retira la SKU Básica", g["title_es"]);
        Assert.Equal("Migra a Standard", g["recommended_action_es"]);
        Assert.Null(g["summary_es"]);
    }

    [Fact]
    public void FilterToManagedExcluyeFilasDeSubsNoAdministradas()
    {
        var rows = new List<StoredRetirement>
        {
            Fila("advisor", "Basic SKU", "sub-1", "/s/1/ip1", new DateOnly(2025, 9, 30)),
            Fila("advisor", "Basic SKU", "sub-2", "/s/2/ip2", new DateOnly(2025, 9, 30)),
            Fila("service_health", "TRACK-1", "sub-3", null, new DateOnly(2026, 9, 30)),
        };

        // "SUB-2" en mayúsculas para probar OrdinalIgnoreCase; sub-3 no está administrada.
        var filtered = BoletinAggregator.FilterToManaged(rows, new[] { "sub-1", "SUB-2" });

        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, r => r.SubscriptionId == "sub-3");
        Assert.Contains(filtered, r => r.SubscriptionId == "sub-1");
        Assert.Contains(filtered, r => r.SubscriptionId == "sub-2");

        // Las administradas quedan intactas en la vista y KPIs; TRACK-1 (sub-3) desaparece.
        var view = BoletinAggregator.BuildView(filtered, subscriptionsTotal: 2, Hoy);
        var kpis = (IReadOnlyDictionary<string, object?>)view["kpis"]!;
        var groups = (List<Dictionary<string, object?>>)view["groups"]!;

        Assert.Single(groups);
        Assert.Equal("Basic SKU", groups[0]["announcement_key"]);
        Assert.Equal(1, kpis["announcements"]);
        Assert.Equal(2, kpis["resources"]);
        Assert.Equal(2, kpis["subscriptions_impacted"]);
        Assert.Equal(2, kpis["subscriptions_total"]);
    }

    // -------------------- derived (Task 5: detectores de inventario) --------------------

    [Fact]
    public void ExponeDerivedEnRecursosYConteo()
    {
        var normal = Fila("service_health", "T1", "s", "/r/a", null);
        var derivada = Fila("service_health", "T1", "s", "/r/b", null) with { Derived = true };

        var g = ((List<Dictionary<string, object?>>)BoletinAggregator
            .BuildView([normal, derivada], 1, new DateOnly(2026, 8, 3))["groups"]!).Single();

        Assert.Equal(2, g["resource_count"]);
        Assert.Equal(1, g["derived_resource_count"]);
        var resources = (List<Dictionary<string, object?>>)g["resources"]!;
        Assert.Equal(1, resources.Count(r => (bool)r["derived"]!));
    }

    // -------------------- BuildSubscriptionsView (A2) --------------------

    [Fact]
    public void BuildSubscriptionsViewMapeaIdYNombre()
    {
        var rows = new List<(string SubscriptionId, string? Name)>
        {
            ("sub-1", "Producción"),
            ("sub-2", "Desarrollo"),
        };

        var view = BoletinAggregator.BuildSubscriptionsView(rows);

        Assert.Equal(2, view.Count);
        Assert.Equal("sub-1", view[0]["subscription_id"]);
        Assert.Equal("Producción", view[0]["name"]);
        Assert.Equal("sub-2", view[1]["subscription_id"]);
        Assert.Equal("Desarrollo", view[1]["name"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildSubscriptionsViewUsaElIdComoFallbackSiNoHayNombre(string? name)
    {
        var view = BoletinAggregator.BuildSubscriptionsView([("sub-1", name)]);

        Assert.Equal("sub-1", view[0]["name"]);
    }
}
