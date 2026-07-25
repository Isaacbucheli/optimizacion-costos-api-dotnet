using System.Net;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.Inventory;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Inventory;

/// <summary>
/// StorageFilesEnricher (spec 2026-07-24, corregido tras E2E real): lista fileshares por ARM
/// SIN $expand=stats (el LIST no lo acepta — 400 InvalidQueryParameterValue en Azure real),
/// y luego pide el uso (shareUsageBytes) por-share con GET /shares/{name}?$expand=stats.
/// Premium (kind FileStorage) se salta el GET por-share: factura por cuota, no por uso.
/// Agrega por tier (estándar = GiB usados; premium = GiB de cuota), aplica el corte ESTRICTO
/// de 10 TiB (10,240 GiB) y tolera fallos (de listado o de stats por share) con advertencia visible.
/// HTTP mockeado con HttpMessageHandler falso; token con DelegatedTokenCredential.
/// </summary>
public sealed class StorageFilesEnricherTests
{
    private const double Gib = 1024d * 1024 * 1024;

    private static readonly TokenCredential FakeCred =
        DelegatedTokenCredential.Create((_, _) => new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static StorageFilesEnricher NewEnricher(FakeHandler handler)
        => new(new FakeHttpClientFactory(handler), NullLogger<StorageFilesEnricher>.Instance);

    private static JsonNode AccountRow(string name, string kind = "StorageV2")
        => new JsonObject
        {
            ["id"] = $"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/{name}",
            ["name"] = name,
            ["kind"] = kind,
            ["skuName"] = "Standard_LRS",
        };

    /// <summary>Respuesta REAL del LIST (sin $expand, sin shareUsageBytes): solo shareQuota + accessTier.</summary>
    private static HttpResponseMessage ListSharesResponse(params (string Name, int QuotaGib, string? Tier)[] shares)
    {
        var value = new JsonArray();
        foreach (var (name, quota, tier) in shares)
        {
            value.Add(new JsonObject
            {
                ["name"] = name,
                ["properties"] = new JsonObject
                {
                    ["shareQuota"] = quota,
                    ["accessTier"] = tier,
                },
            });
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject { ["value"] = value }.ToJsonString()),
        };
    }

    /// <summary>Respuesta REAL del GET por-share con $expand=stats: incluye shareUsageBytes.</summary>
    private static HttpResponseMessage ShareStatsResponse(string name, long usageBytes, int quotaGib, string? tier)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["name"] = name,
                ["properties"] = new JsonObject
                {
                    ["shareQuota"] = quotaGib,
                    ["accessTier"] = tier,
                    ["shareUsageBytes"] = usageBytes,
                },
            }.ToJsonString()),
        };

    /// <summary>Enruta por URL como lo haría un fake de Azure real: LIST vs GET por-share
    /// ($expand=stats) vs la métrica de diferencial de snapshots. Los tests que usan este
    /// helper no ejercitan el diferencial de snapshots en sí (eso lo cubren los tests
    /// "MetricaDeSnapshots*"/"SnapshotsSumanAlFacturable_*" dedicados) — por eso la métrica
    /// responde con un diferencial real de 0 (un "average": 0 explícito, NO una respuesta
    /// vacía/sin "average") para no disparar la advertencia de FIX 5.</summary>
    private static HttpResponseMessage RouteListAndStats(
        HttpRequestMessage req,
        (string Name, int QuotaGib, string? Tier)[] listShares,
        Dictionary<string, long> usageByShare)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase))
        {
            return SnapshotMetricResponse(0);
        }
        if (url.Contains("expand=stats", StringComparison.OrdinalIgnoreCase))
        {
            // GET /shares/{name}?...&$expand=stats
            var name = listShares.Select(s => s.Name).First(n => url.Contains($"/shares/{n}"));
            var share = listShares.First(s => s.Name == name);
            return ShareStatsResponse(name, usageByShare[name], share.QuotaGib, share.Tier);
        }
        return ListSharesResponse(listShares);
    }

    /// <summary>Respuesta REAL verificada de Azure Monitor para la métrica FileShareSnapshotSize
    /// (namespace Microsoft.Storage/storageAccounts/fileServices): NO se desglosa por-fileshare
    /// pese a declarar esa dimensión — la serie siempre viene con metadatavalue "&lt;All&gt;"
    /// (agregado de la cuenta completa). Verificado contra una cuenta real con 31 snapshots
    /// diarios de Azure Backup.</summary>
    private static HttpResponseMessage SnapshotMetricResponse(double averageBytes)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["cost"] = 100,
                ["timespan"] = "2026-07-21T21:37:28Z/2026-07-24T21:37:28Z",
                ["interval"] = "P1D",
                ["value"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = ".../metrics/FileShareSnapshotSize",
                        ["type"] = "Microsoft.Insights/metrics",
                        ["name"] = new JsonObject { ["value"] = "FileShareSnapshotSize", ["localizedValue"] = "File Share Snapshot Size" },
                        ["displayDescription"] = "The amount of storage used by the snapshots in storage account's File service in bytes.",
                        ["unit"] = "Bytes",
                        ["timeseries"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["metadatavalues"] = new JsonArray
                                {
                                    new JsonObject { ["name"] = new JsonObject { ["value"] = "fileshare", ["localizedValue"] = "fileshare" }, ["value"] = "<All>" },
                                },
                                ["data"] = new JsonArray
                                {
                                    new JsonObject { ["timeStamp"] = "2026-07-22T21:37:00Z", ["average"] = averageBytes },
                                    new JsonObject { ["timeStamp"] = "2026-07-23T21:37:00Z", ["average"] = averageBytes },
                                },
                            },
                        },
                        ["errorCode"] = "Success",
                    },
                },
                ["namespace"] = "Microsoft.Storage/storageAccounts/fileServices",
                ["resourceregion"] = "eastus2",
            }.ToJsonString()),
        };

    /// <summary>Body REAL verificado de un 400 FeatureNotSupportedForAccount (E2E: cuenta
    /// Storage/Premium_LRS de solo page-blob usada por Azure Site Recovery).</summary>
    private static HttpResponseMessage FeatureNotSupportedResponse()
        => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = "FeatureNotSupportedForAccount",
                    ["message"] = "File is not supported for the account.",
                },
            }.ToJsonString()),
        };

    [Fact]
    public async Task CuentaGrandeEstandar_EntraConUsoPorTier()
    {
        // 12,000 GiB usados en hot (cuota 20,480) → supera 10,240 → entra por USO.
        (string, int, string?)[] listShares = [("share0", 20480, "Hot")];
        var usage = new Dictionary<string, long> { ["share0"] = (long)(12000 * Gib) };
        var handler = new FakeHandler(req => RouteListAndStats(req, listShares, usage));
        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgbig")], CancellationToken.None);

        var kept = Assert.Single(result.Kept);
        Assert.Empty(result.Warnings);
        Assert.Equal(1, kept.Int("shareCount"));
        Assert.Equal(12000.0, kept.Dbl("usedGib")!.Value, 1);
        Assert.Equal(20480.0, kept.Dbl("provisionedGib")!.Value, 1);
        Assert.Equal(12000.0, kept.Dbl("billableGib")!.Value, 1);
        Assert.Contains("\"hot\":", kept.Str("tierBreakdownJson"));
    }

    [Fact]
    public async Task CortePorUso_10240ExactoNoEntra_10241Entra()
    {
        (string, int, string?)[] shareExactly = [("share0", 102400, "Hot")];
        (string, int, string?)[] shareOver = [("share0", 102400, "Hot")];
        var exactly = new FakeHandler(req => RouteListAndStats(req, shareExactly,
            new Dictionary<string, long> { ["share0"] = (long)(10240 * Gib) }));
        var over = new FakeHandler(req => RouteListAndStats(req, shareOver,
            new Dictionary<string, long> { ["share0"] = (long)(10241 * Gib) }));

        var resultExactly = await NewEnricher(exactly).EnrichAsync(FakeCred, [AccountRow("a")], default);
        Assert.Empty(resultExactly.Kept);
        Assert.Empty(resultExactly.Warnings); // exclusión por debajo del corte debe ser silenciosa

        Assert.Single((await NewEnricher(over).EnrichAsync(FakeCred, [AccountRow("b")], default)).Kept);
    }

    [Fact]
    public async Task Premium_FacturaPorCuota_NoPorUso()
    {
        // Premium: 11,000 GiB de cuota con solo 100 GiB usados → entra igual (cuota manda).
        // Y NO debe llamar al GET de stats (ver Premium_NoLlamaStatsPorShare para la aserción dedicada).
        (string, int, string?)[] listShares = [("share0", 11000, "Premium")];
        var handler = new FakeHandler(req => ListSharesResponse(listShares)); // si pidiera stats, esto no las tiene
        var result = await NewEnricher(handler)
            .EnrichAsync(FakeCred, [AccountRow("stgprem", kind: "FileStorage")], default);

        var kept = Assert.Single(result.Kept);
        Assert.Equal(11000.0, kept.Dbl("billableGib")!.Value, 1);
        Assert.Contains("\"premium\":", kept.Str("tierBreakdownJson"));
    }

    [Fact]
    public async Task EstandarSinTier_CaeATransactionOptimized()
    {
        (string, int, string?)[] listShares = [("share0", 20480, null)];
        var usage = new Dictionary<string, long> { ["share0"] = (long)(11000 * Gib) };
        var handler = new FakeHandler(req => RouteListAndStats(req, listShares, usage));
        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stg")], default);
        Assert.Contains("\"transaction_optimized\":", Assert.Single(result.Kept).Str("tierBreakdownJson"));
    }

    [Fact]
    public async Task SinShares_NoSeInventariaNiAdvierte()
    {
        var handler = new FakeHandler(_ => ListSharesResponse());
        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgblob")], default);
        Assert.Empty(result.Kept);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task FalloArmPorCuenta_OmiteConAdvertencia_YSigueConLasDemas()
    {
        // Fallo en el LIST mismo (no en el stats por-share): pierde toda la cuenta, con advertencia.
        var handler = new FakeHandler(req =>
            req.RequestUri!.ToString().Contains("stgmala")
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : RouteListAndStats(req, [("share0", 20480, "Hot")],
                    new Dictionary<string, long> { ["share0"] = (long)(12000 * Gib) }));

        var result = await NewEnricher(handler)
            .EnrichAsync(FakeCred, [AccountRow("stgmala"), AccountRow("stgbuena")], default);

        Assert.Single(result.Kept);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("stgmala", warning);
    }

    [Fact]
    public async Task Paginacion_SigueNextLink()
    {
        var page2Url = "https://management.azure.com/page2";
        var usage = new Dictionary<string, long> { ["share0"] = (long)(6000 * Gib), ["share1"] = (long)(6000 * Gib) };
        var handler = new FakeHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("expand=stats", StringComparison.OrdinalIgnoreCase))
            {
                var name = url.Contains("/shares/share0") ? "share0" : "share1";
                return ShareStatsResponse(name, usage[name], 10240, "Hot");
            }
            if (url == page2Url)
            {
                return ListSharesResponse(("share1", 10240, "Hot"));
            }
            var body = JsonNode.Parse(ListSharesResponse(("share0", 10240, "Hot")).Content.ReadAsStringAsync().Result)!.AsObject();
            body["nextLink"] = page2Url;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        });

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stg")], default);
        var kept = Assert.Single(result.Kept); // 12,000 GiB entre las 2 páginas → entra
        Assert.Equal(2, kept.Int("shareCount"));
        Assert.Equal(12000.0, kept.Dbl("billableGib")!.Value, 1);
    }

    [Fact]
    public async Task UsaApiVersionYExpandStats_SoloEnListYNoEnList()
    {
        (string, int, string?)[] listShares = [("share0", 20480, "Hot")];
        var usage = new Dictionary<string, long> { ["share0"] = (long)(11000 * Gib) };
        var handler = new FakeHandler(req => RouteListAndStats(req, listShares, usage));
        await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stg")], default);

        Assert.Equal(3, handler.Urls.Count); // 1 list + 1 stats + 1 métrica de snapshots
        var listUrl = handler.Urls.Single(u => u.EndsWith("/fileServices/default/shares?api-version=2023-05-01"));
        Assert.Contains("api-version=2023-05-01", listUrl);
        Assert.DoesNotContain("expand=stats", listUrl, StringComparison.OrdinalIgnoreCase); // regresión: el LIST no acepta $expand=stats

        var statsUrl = handler.Urls.Single(u => u.Contains("/shares/share0"));
        Assert.Contains("api-version=2023-05-01", statsUrl);
        Assert.Contains("expand=stats", statsUrl, StringComparison.OrdinalIgnoreCase);

        var metricsUrl = handler.Urls.Single(u => u.Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("metricnames=FileShareSnapshotSize", metricsUrl);
        Assert.Contains("api-version=2018-01-01", metricsUrl);
    }

    [Fact]
    public async Task Paginacion_SeDetieneEnMaxPages_ConAdvertencia()
    {
        // nextLink en bucle infinito: cada página trae 1 share y siempre apunta a otra página.
        // El tope MaxPages debe cortar el loop (si no, este test cuelga) y dejar advertencia visible.
        var handler = new FakeHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("expand=stats", StringComparison.OrdinalIgnoreCase))
            {
                return ShareStatsResponse("share0", (long)(100 * Gib), 200, "Hot");
            }
            var body = JsonNode.Parse(ListSharesResponse(("share0", 200, "Hot")).Content.ReadAsStringAsync().Result)!.AsObject();
            body["nextLink"] = "https://management.azure.com/always-next";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        });

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stg")], default);

        Assert.Equal(StorageFilesEnricher.MaxPages, handler.Urls.Count(u =>
            !u.Contains("expand=stats", StringComparison.OrdinalIgnoreCase)
            && !u.Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Warnings, w => w.Contains("truncado"));
    }

    [Fact]
    public async Task Cancelacion_SePropaga_NoSeDegradaAAdvertencia()
    {
        // El token se obtiene antes de cancelar (FakeCred lo ignora); la cancelación real ocurre
        // recién en la llamada HTTP de fileshares, para no confundirla con un fallo de credencial.
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("a")], cts.Token));
    }

    [Fact]
    public async Task Estandar_LeeUsoConGetPorShare()
    {
        // 2 shares, el LIST no trae uso; el uso llega por GET por-share. billableGib = suma de ambos.
        (string, int, string?)[] listShares = [("share0", 6000, "Hot"), ("share1", 6000, "Hot")];
        var usage = new Dictionary<string, long>
        {
            ["share0"] = (long)(5500 * Gib),
            ["share1"] = (long)(5500 * Gib),
        };
        var handler = new FakeHandler(req => RouteListAndStats(req, listShares, usage));
        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stg")], default);

        var kept = Assert.Single(result.Kept);
        Assert.Equal(11000.0, kept.Dbl("billableGib")!.Value, 1); // 5500 + 5500
        Assert.Equal(1, handler.Urls.Count(u =>
            !u.Contains("expand=stats", StringComparison.OrdinalIgnoreCase)
            && !u.Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase))); // 1 list call
        Assert.Equal(2, handler.Urls.Count(u => u.Contains("expand=stats", StringComparison.OrdinalIgnoreCase))); // 2 stats calls
        Assert.Equal(1, handler.Urls.Count(u => u.Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase))); // 1 métrica de snapshots (cuenta completa, no por-share)
    }

    [Fact]
    public async Task Premium_NoLlamaStatsPorShare()
    {
        // Premium sobre el corte por cuota: NO debe llamar al GET por-share NI a la métrica de
        // snapshots (optimización + semántica: factura por cuota, el diferencial no aporta nada).
        (string, int, string?)[] listShares = [("share0", 20480, "Premium")];
        var handler = new FakeHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("expand=stats", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("no debería llamarse a stats por-share para premium");
            }
            if (url.Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("no debería consultarse el diferencial de snapshots para premium");
            }
            return ListSharesResponse(listShares);
        });

        var result = await NewEnricher(handler)
            .EnrichAsync(FakeCred, [AccountRow("stgprem", kind: "FileStorage")], default);

        var kept = Assert.Single(result.Kept);
        Assert.DoesNotContain(handler.Urls, u => u.Contains("expand=stats", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Urls, u => u.Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(20480.0, kept.Dbl("billableGib")!.Value, 1); // por cuota, no por uso
    }

    [Fact]
    public async Task FalloDeStatsEnUnShare_AdvierteYNoPierdeLaCuenta()
    {
        // 2 shares; el stats de share1 falla con 403. La cuenta se procesa igual con el uso
        // disponible (share0), y queda advertencia visible de que el total puede estar incompleto.
        (string, int, string?)[] listShares = [("share0", 6000, "Hot"), ("share1", 6000, "Hot")];
        var handler = new FakeHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase))
            {
                return SnapshotMetricResponse(0); // fuera de alcance de este test (ver FIX 5)
            }
            if (url.Contains("/shares/share1") && url.Contains("expand=stats", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            if (url.Contains("expand=stats", StringComparison.OrdinalIgnoreCase))
            {
                return ShareStatsResponse("share0", (long)(11000 * Gib), 6000, "Hot");
            }
            return ListSharesResponse(listShares);
        });

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stg")], default);

        var kept = Assert.Single(result.Kept);
        Assert.Equal(2, kept.Int("shareCount"));
        Assert.Equal(11000.0, kept.Dbl("usedGib")!.Value, 1); // share1 cuenta como 0 por el fallo
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("stg", warning);
        Assert.Contains("uso", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incompleto", warning);
    }

    // -------------------- FIX 4: shareUsageBytes ausente en un 200 debe advertir, no valer 0 --------------------

    [Fact]
    public async Task StatsSinShareUsageBytes_AdvierteYCuentaComoFallo_NoComoCero()
    {
        // share1 responde 200 OK (no un fallo HTTP), pero "properties" NO trae shareUsageBytes
        // — antes GetShareUsageBytesAsync devolvía 0L en ese caso, indistinguible de "el share
        // genuinamente no tiene uso" y sin incrementar el contador de fallos. Debe tratarse
        // EXACTAMENTE igual que el 403 de FalloDeStatsEnUnShare_AdvierteYNoPierdeLaCuenta.
        (string, int, string?)[] listShares = [("share0", 6000, "Hot"), ("share1", 6000, "Hot")];
        var handler = new FakeHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase))
            {
                return SnapshotMetricResponse(0); // fuera de alcance de este test (ver FIX 5)
            }
            if (url.Contains("/shares/share1") && url.Contains("expand=stats", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JsonObject
                    {
                        ["name"] = "share1",
                        ["properties"] = new JsonObject { ["shareQuota"] = 6000, ["accessTier"] = "Hot" },
                    }.ToJsonString()),
                };
            }
            if (url.Contains("expand=stats", StringComparison.OrdinalIgnoreCase))
            {
                return ShareStatsResponse("share0", (long)(11000 * Gib), 6000, "Hot");
            }
            return ListSharesResponse(listShares);
        });

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgsinbytes")], default);

        var kept = Assert.Single(result.Kept);
        Assert.Equal(2, kept.Int("shareCount"));
        Assert.Equal(11000.0, kept.Dbl("usedGib")!.Value, 1); // share1 cuenta como 0 por el campo ausente
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("stgsinbytes", warning);
        Assert.Contains("uso", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incompleto", warning);
    }

    // -------------------- FIX 5: métrica de snapshots sin dato confiable debe advertir, no valer 0 --------------------

    /// <summary>Serie con puntos que solo tienen timeStamp, SIN "average" — Azure Monitor puede
    /// omitir el campo para intervalos sin datos (a diferencia de un "average": 0 explícito).</summary>
    private static HttpResponseMessage SnapshotMetricResponseSinAverage()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["value"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["timeseries"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["data"] = new JsonArray
                                {
                                    new JsonObject { ["timeStamp"] = "2026-07-22T21:37:00Z" },
                                    new JsonObject { ["timeStamp"] = "2026-07-23T21:37:00Z" },
                                },
                            },
                        },
                        ["errorCode"] = "Success",
                    },
                },
            }.ToJsonString()),
        };

    private static HttpResponseMessage SnapshotMetricResponseConErrorCode()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["value"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["timeseries"] = new JsonArray(),
                        ["errorCode"] = "InternalServerError",
                    },
                },
            }.ToJsonString()),
        };

    [Fact]
    public async Task MetricaDeSnapshotsSinPuntosDeAverage_AdvierteYNoColapsaACero()
    {
        // 9,000 GiB en vivo (bajo el corte de 10,240): sin un diferencial de snapshots
        // confiable, la cuenta se costea SOLO con lo en vivo y queda por debajo del corte — pero
        // la advertencia DEBE aparecer igual (antes esto habría leído 0 GiB de diferencial en
        // silencio, indistinguible de "no hay snapshots", y la cuenta se habría excluido sin
        // ninguna señal de que el dato pudo estar incompleto).
        (string, int, string?)[] listShares = [("share0", 20480, "Hot")];
        var usage = new Dictionary<string, long> { ["share0"] = (long)(9000 * Gib) };
        var handler = new FakeHandler(req =>
            req.RequestUri!.ToString().Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase)
                ? SnapshotMetricResponseSinAverage()
                : RouteListAndStats(req, listShares, usage));

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgsinavg")], default);

        Assert.Empty(result.Kept); // sin diferencial confirmado, 9000 GiB en vivo no alcanza el corte
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("stgsinavg", warning);
        Assert.Contains("snapshot", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incompleto", warning);
    }

    [Fact]
    public async Task MetricaDeSnapshotsConErrorCode_AdvierteYNoColapsaACero()
    {
        // errorCode distinto de "Success" en el elemento de "value": Azure señaló explícitamente
        // que la lectura falló; antes esto se leía igual como "sin timeseries" → 0 GiB silencioso.
        (string, int, string?)[] listShares = [("share0", 20480, "Hot")];
        var usage = new Dictionary<string, long> { ["share0"] = (long)(9000 * Gib) };
        var handler = new FakeHandler(req =>
            req.RequestUri!.ToString().Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase)
                ? SnapshotMetricResponseConErrorCode()
                : RouteListAndStats(req, listShares, usage));

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgerrorcode")], default);

        Assert.Empty(result.Kept);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("stgerrorcode", warning);
        Assert.Contains("incompleto", warning);
    }

    /// <summary>Dos timeseries en la misma métrica, cada una con un solo punto.</summary>
    private static HttpResponseMessage SnapshotMetricResponseConDosSeries(double bytesSerie1, double bytesSerie2)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["value"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["timeseries"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["data"] = new JsonArray
                                {
                                    new JsonObject { ["timeStamp"] = "2026-07-23T21:37:00Z", ["average"] = bytesSerie1 },
                                },
                            },
                            new JsonObject
                            {
                                ["data"] = new JsonArray
                                {
                                    new JsonObject { ["timeStamp"] = "2026-07-23T21:37:00Z", ["average"] = bytesSerie2 },
                                },
                            },
                        },
                        ["errorCode"] = "Success",
                    },
                },
            }.ToJsonString()),
        };

    [Fact]
    public async Task MetricaDeSnapshotsConDosSeries_SumaAmbasEnVezDePisarConLaUltima()
    {
        // Si Azure alguna vez devolviera más de una serie (hoy siempre trae una sola "<All>",
        // verificado empíricamente contra una cuenta real), sumar es la lectura correcta del
        // diferencial total de la cuenta. Antes el código se quedaba con el ÚLTIMO valor visto
        // recorriendo TODAS las series (una variable compartida, no una por serie), lo que
        // habría reducido silenciosamente el total al valor de una sola serie.
        (string, int, string?)[] listShares = [("share0", 20480, "Hot")];
        var usage = new Dictionary<string, long> { ["share0"] = (long)(9000 * Gib) };
        var serie1Bytes = 1000 * Gib;
        var serie2Bytes = 1500 * Gib;
        var handler = new FakeHandler(req =>
            req.RequestUri!.ToString().Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase)
                ? SnapshotMetricResponseConDosSeries(serie1Bytes, serie2Bytes)
                : RouteListAndStats(req, listShares, usage));

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgdosseries")], default);

        var kept = Assert.Single(result.Kept);
        Assert.Empty(result.Warnings);
        Assert.Equal(11500.0, kept.Dbl("billableGib")!.Value, 1); // 9000 vivo + 1000 + 1500 diferencial
    }

    [Fact]
    public async Task SnapshotsSumanAlFacturable_CuentaEntraAunqueVivoSoloNoAlcance()
    {
        // 9,000 GiB en vivo (por debajo del corte de 10,240) + 2,500 GiB de diferencial de
        // snapshots (métrica FileShareSnapshotSize, verificada empíricamente) = 11,500 GiB
        // facturables → SUPERA el corte → la cuenta ENTRA (antes se habría descartado en silencio).
        (string, int, string?)[] listShares = [("share0", 20480, "Hot")];
        var usage = new Dictionary<string, long> { ["share0"] = (long)(9000 * Gib) };
        var snapshotBytes = 2500 * Gib;
        var handler = new FakeHandler(req =>
            req.RequestUri!.ToString().Contains("Insights/metrics", StringComparison.OrdinalIgnoreCase)
                ? SnapshotMetricResponse(snapshotBytes)
                : RouteListAndStats(req, listShares, usage));

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgconsnap")], default);

        var kept = Assert.Single(result.Kept);
        Assert.Empty(result.Warnings);
        Assert.Equal(9000.0, kept.Dbl("usedGib")!.Value, 1); // usedGib es SOLO el uso en vivo
        Assert.Equal(11500.0, kept.Dbl("billableGib")!.Value, 1); // vivo + diferencial de snapshots
        var tierJson = JsonNode.Parse(kept.Str("tierBreakdownJson")!)!;
        Assert.Equal(11500.0, tierJson["hot"]!.GetValue<double>(), 1); // único tier: se lleva el diferencial completo
    }

    [Fact]
    public async Task FeatureNotSupportedForAccount_OmiteEnSilencio_SinAdvertenciaNiExcepcion()
    {
        // Caso real observado en E2E: cuenta Storage/Premium_LRS, solo page-blob, usada por Azure Site
        // Recovery) responde 400 FeatureNotSupportedForAccount al LIST de fileshares. Antes esto
        // generaba una advertencia de "cuenta omitida" (falsa alarma); ahora se omite en silencio,
        // igual que una cuenta con 0 shares.
        var handler = new FakeHandler(_ => FeatureNotSupportedResponse());
        var result = await NewEnricher(handler)
            .EnrichAsync(FakeCred, [AccountRow("stgasronly", kind: "Storage")], default);

        Assert.Empty(result.Kept);
        Assert.Empty(result.Warnings); // sin ruido: no es una falla real de la importación
    }

    [Fact]
    public async Task Otro400EnList_SigueAdvirtiendoComoAntes_NoLoConfundeConFeatureNotSupported()
    {
        // Regresión (guarda que change 2 no silencie fallas reales): un 400 con un código DISTINTO
        // a FeatureNotSupportedForAccount debe seguir tratándose como falla real → advertencia visible.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = "InvalidQueryParameterValue",
                    ["message"] = "Value for one of the query parameters specified in the request URI is invalid.",
                },
            }.ToJsonString()),
        });

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgotro400")], default);

        Assert.Empty(result.Kept);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("stgotro400", warning);
    }

    [Fact]
    public async Task DesgloseDeTiers_SumaExactoAlFacturable_SinDerivaDeRedondeo()
    {
        // Regresión de Change 3: antes se redondeaba DENTRO del loop por-share
        // (tiers[tier] = Math.Round(acumulado + shareBillable, 2)), lo que compone error de
        // redondeo share a share y puede hacer que el desglose por tier NO reconcilie con
        // billable_gib (aparecen lado a lado en el Excel). 3 shares con GiB fraccionario en el
        // MISMO tier: sumar sin redondear y redondear una sola vez al final debe reconciliar EXACTO.
        (string, int, string?)[] listShares =
        [
            ("share0", 4000, "Hot"),
            ("share1", 4000, "Hot"),
            ("share2", 4000, "Hot"),
        ];
        var usage = new Dictionary<string, long>
        {
            ["share0"] = (long)(3500.333 * Gib),
            ["share1"] = (long)(3500.334 * Gib),
            ["share2"] = (long)(3500.335 * Gib),
        };
        var handler = new FakeHandler(req => RouteListAndStats(req, listShares, usage));
        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgtiers")], default);

        var kept = Assert.Single(result.Kept);
        var tierJson = JsonNode.Parse(kept.Str("tierBreakdownJson")!)!;
        var hotValue = tierJson["hot"]!.GetValue<double>();
        Assert.Equal(kept.Dbl("billableGib")!.Value, hotValue); // debe reconciliar EXACTO (un solo tier)
    }
}
