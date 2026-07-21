using System.Text.Json;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Fidelidad con el portal de Advisor (spec 2026-07-21): los ítems pospuestos/descartados llevan
/// suppressionIds y el portal los oculta — la ingesta ARM debe saltarlos y contarlos. Además cada
/// row expone recommendationTypeId (base del cross-check Defender y del atajo de dedup).
/// </summary>
public sealed class AdvisorSuppressionTests
{
    private static JsonElement Item(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<JsonElement> Items(params string[] jsons) =>
        jsons.Select(Item).ToList();

    private const string BaseItem =
        "{\"name\":\"rec-1\",\"properties\":{\"category\":\"Security\",\"impact\":\"High\"," +
        "\"label\":\"Some recommendation\",\"impactedValue\":\"vm-1\"," +
        "\"recommendationTypeId\":\"AB12CD34-0000-1111-2222-333344445555\"{EXTRA}}}";

    private static string WithExtra(string extra) => BaseItem.Replace("{EXTRA}", extra);

    // ------------------------- RecommendationTypeId -------------------------

    [Fact]
    public void ItemToRow_extrae_recommendationTypeId_en_minusculas()
    {
        var row = AdvisorApiClient.ItemToRow(Item(WithExtra("")), "sub-1", "Sub Uno");
        Assert.Equal("ab12cd34-0000-1111-2222-333344445555", row.RecommendationTypeId);
    }

    [Fact]
    public void ItemToRow_sin_recommendationTypeId_devuelve_null()
    {
        var row = AdvisorApiClient.ItemToRow(
            Item("{\"name\":\"rec-1\",\"properties\":{\"category\":\"Cost\",\"label\":\"X\"}}"),
            "sub-1", "Sub Uno");
        Assert.Null(row.RecommendationTypeId);
    }

    // ------------------------- suppressionIds -------------------------

    [Fact]
    public void ItemsToRows_salta_item_con_suppressionIds_y_lo_cuenta()
    {
        var items = Items(
            WithExtra(",\"suppressionIds\":[\"11111111-1111-1111-1111-111111111111\"]"),
            "{\"name\":\"rec-2\",\"properties\":{\"category\":\"Cost\",\"label\":\"Otra\",\"impactedValue\":\"vm-2\"}}");

        var (rows, metrics) = AdvisorApiClient.ItemsToRows(items, "sub-1", "Sub Uno");

        Assert.Single(rows);
        Assert.Equal("Otra", rows[0].AdvisorName);
        Assert.Equal(1, metrics.RowsSuppressedSkipped);
        Assert.Equal(0, metrics.RowsSkipped); // suprimida NO cuenta como skip normal
        Assert.Equal(0, metrics.RowsDuplicateSkipped);
        Assert.Equal(2, metrics.RowsTotal); // total incluye la suprimida
        Assert.Equal(1, metrics.RowsProcessed);
    }

    [Fact]
    public void ItemsToRows_suppressionIds_vacio_o_null_no_salta()
    {
        var items = Items(
            WithExtra(",\"suppressionIds\":[]"),
            "{\"name\":\"rec-2\",\"properties\":{\"category\":\"Cost\",\"label\":\"Con null\"," +
            "\"impactedValue\":\"vm-2\",\"suppressionIds\":null}}");

        var (rows, metrics) = AdvisorApiClient.ItemsToRows(items, "sub-1", "Sub Uno");

        Assert.Equal(2, rows.Count);
        Assert.Equal(0, metrics.RowsSuppressedSkipped);
    }

    [Fact]
    public void ItemsToRows_suppressionIds_solo_entradas_vacias_no_salta()
    {
        // Defensa contra respuestas raras: [null, ""] no es una supresión real.
        var items = Items(WithExtra(",\"suppressionIds\":[null,\"\"]"));

        var (rows, metrics) = AdvisorApiClient.ItemsToRows(items, "sub-1", "Sub Uno");

        Assert.Single(rows);
        Assert.Equal(0, metrics.RowsSuppressedSkipped);
    }
}
