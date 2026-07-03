using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OptimizacionCostos.Api.Features.Cdc;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Cdc.Api;

public sealed class AnalysisRefreshApiTests : IClassFixture<CdcApiTestFactory>
{
    private readonly CdcApiTestFactory _factory;
    public AnalysisRefreshApiTests(CdcApiTestFactory factory) => _factory = factory;

    private HttpClient AuthedClient(string role = "admin")
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add("isaac@bit.com", role);
        var token = BitJwt.Create(CdcApiTestFactory.Secret, "isaac@bit.com", "Isaac", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task PostRefresh_SinToken_401()
    {
        var resp = await _factory.CreateClient().PostAsync("/analysis/5/power-history/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PostRefresh_EncolaYResponde202()
    {
        _factory.Access.AnalysisToClient[5] = 1;
        _factory.Queue.Enqueued.Clear();

        var resp = await AuthedClient().PostAsync("/analysis/5/power-history/refresh", null);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("running", body.GetProperty("status").GetString());
        Assert.Single(_factory.Queue.Enqueued);
        Assert.Equal(5, _factory.Queue.Enqueued[0].AnalysisId);
        Assert.Equal("running", (await _factory.Store.GetStatusAsync(5, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task PostRefresh_YaEnProceso_NoEncolaDeNuevo()
    {
        _factory.Access.AnalysisToClient[6] = 1;
        _factory.Queue.Enqueued.Clear();
        await _factory.Store.MarkRunningAsync(6, "x", CancellationToken.None);

        var resp = await AuthedClient().PostAsync("/analysis/6/power-history/refresh", null);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Empty(_factory.Queue.Enqueued);
    }

    [Fact]
    public async Task PostRefresh_AnalisisInexistente_404()
    {
        var resp = await AuthedClient().PostAsync("/analysis/9999/power-history/refresh", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetStatus_Completed_DevuelveSummary()
    {
        _factory.Access.AnalysisToClient[7] = 1;
        await _factory.Store.MarkRunningAsync(7, "x", CancellationToken.None);
        await _factory.Store.MarkCompletedAsync(7, "{\"updated_count\":4,\"source\":\"activity_log\"}", CancellationToken.None);

        var resp = await AuthedClient().GetAsync("/analysis/7/power-history/status");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal(4, body.GetProperty("summary").GetProperty("updated_count").GetInt32());
    }

    [Fact]
    public async Task GetStatus_NuncaCorrio_DevuelveNone()
    {
        _factory.Access.AnalysisToClient[8] = 1;
        var resp = await AuthedClient().GetAsync("/analysis/8/power-history/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("none", body.GetProperty("status").GetString());
    }
}
