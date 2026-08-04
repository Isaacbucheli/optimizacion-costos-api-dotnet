using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinMigracionTests
{
    private static StoredRetirement Row(string source, string key, string title, string feature,
        DateOnly? date = null, string? resourceId = "/subscriptions/s1/x", string sub = "s1") =>
        new("fp-" + key + "-" + (resourceId ?? "null"), source, key, sub, resourceId,
            "recurso-demo", "Microsoft.Web/sites", feature, date, title, null, null, null);

    private static MigracionEntry Entry(int id, string clave, string pattern, bool active = true) =>
        new(id, clave, "Desde " + clave, "Hacia " + clave, "Notas " + clave, pattern, null, active);

    [Fact]
    public void Gana_el_patron_mas_largo()
    {
        var text = BoletinMigracion.MatchText(Row("advisor", "k1", "Migrate off Windows Server 2012 R2 hosts", "Windows Server 2012 R2"));
        var best = BoletinMigracion.BestRoute(
            [Entry(1, "ws2012", "windows server 2012"), Entry(2, "ws2012r2", "windows server 2012 r2")], text);
        Assert.Equal("ws2012r2", best!.Clave);
    }

    [Fact]
    public void Entrada_inactiva_no_matchea()
    {
        var best = BoletinMigracion.BestRoute([Entry(1, "x", "basic load balancer", active: false)],
            "upgrade your basic load balancer");
        Assert.Null(best);
    }

    [Fact]
    public void Eol_matchea_por_producto_y_retiros_por_titulo_mas_feature()
    {
        Assert.Equal("windows server 2012 r2",
            BoletinMigracion.MatchText(Row("eol", "k", "Windows Server 2012 R2 — fin de soporte", "Windows Server 2012 R2")));
        Assert.Contains("migrate your service bus sdks",
            BoletinMigracion.MatchText(Row("advisor", "k", "Migrate your Service Bus SDKs by 30 September 2026", "Service Bus")));
    }

    [Fact]
    public void BuildSection_agrupa_por_ruta_con_conteos_y_fecha_mas_proxima()
    {
        var today = new DateOnly(2026, 8, 4);
        var rows = new[]
        {
            Row("advisor", "a1", "Upgrade Basic Load Balancer", "Basic Load Balancer", new DateOnly(2025, 9, 30)),
            Row("advisor", "a1", "Upgrade Basic Load Balancer", "Basic Load Balancer", new DateOnly(2025, 9, 30), "/subscriptions/s1/y"),
            Row("service_health", "a2", "Basic Load Balancer will be retired", "Basic Load Balancer", new DateOnly(2025, 9, 30), resourceId: null),
            Row("advisor", "a3", "Algo sin ruta", "Misterio", new DateOnly(2027, 1, 1)),
        };
        var section = BoletinMigracion.BuildSection(rows, [Entry(1, "basic-lb", "basic load balancer")], today);

        var rutas = (List<Dictionary<string, object?>>)section["rutas"]!;
        var ruta = Assert.Single(rutas);
        Assert.Equal("basic-lb", ruta["clave"]);
        var anuncios = (List<Dictionary<string, object?>>)ruta["announcements"]!;
        Assert.Equal(2, anuncios.Count);                       // a1 y a2 bajo la misma ruta
        Assert.Equal("2025-09-30", ruta["nearest_date"]);
        Assert.Equal(2, ruta["total_resources"]);              // a1 tiene 2 recursos, a2 cero

        var sinRuta = (List<Dictionary<string, object?>>)section["sin_ruta"]!;
        Assert.Equal("a3", Assert.Single(sinRuta)["announcement_key"]);
        Assert.Equal("retirado", anuncios[0]["urgency"]);      // urgencia calculada al leer, como BuildView
    }

    [Fact]
    public void Catalogo_vacio_manda_todo_a_sin_ruta_y_title_es_cae_al_ingles()
    {
        var rows = new[] { Row("advisor", "a1", "Title EN", "F") with { TitleEs = "Título ES" } };
        var section = BoletinMigracion.BuildSection(rows, [], new DateOnly(2026, 8, 4));
        Assert.Empty((List<Dictionary<string, object?>>)section["rutas"]!);
        var sr = Assert.Single((List<Dictionary<string, object?>>)section["sin_ruta"]!);
        Assert.Equal("Título ES", sr["title_es"]);
    }

    [Fact]
    public void SinRuta_devuelve_un_anuncio_por_key_con_textos_para_la_ia()
    {
        var rows = new[]
        {
            Row("advisor", "a1", "T1", "F1") with { Summary = "S1", RecommendedAction = "R1" },
            Row("advisor", "a1", "T1", "F1", resourceId: "/subscriptions/s1/z"),
        };
        var sin = BoletinMigracion.SinRuta(rows, []);
        var a = Assert.Single(sin);
        Assert.Equal(("T1", "S1", "R1"), (a.Title, a.Summary, a.RecommendedAction));
    }
}
