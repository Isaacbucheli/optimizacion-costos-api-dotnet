using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AlertCatalog;

namespace OptimizacionCostos.Api.Tests;

/// <summary>
/// Round-trip REAL contra Azure SQL (la BD separada sqldb-optimizacion-costos-dotnet).
/// Solo corre si BIT_INTEGRATION_DB=1 y hay credenciales SQL_* en el entorno; si no,
/// es un no-op para que la suite normal siga sin tocar Azure.
/// Ejercita: schema-ensure -> seed idempotente -> list -> create -> get -> update -> soft-delete.
/// </summary>
public class DbRoundTripTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("BIT_INTEGRATION_DB") == "1";

    private static IAlertCatalogStore NewStore()
    {
        var config = AppConfig.FromConfiguration(new ConfigurationBuilder().Build());
        return new SqlAlertCatalogStore(new SqlConnectionFactory(config, NullLogger<SqlConnectionFactory>.Instance));
    }

    [Fact]
    public async Task RoundTrip_completo_contra_AzureSQL()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var store = NewStore();

        // 1. List dispara schema-ensure + seed. La BD nueva arranca vacía -> debe sembrar.
        var alerts = await store.ListAlertsAsync();
        var kql = await store.ListKqlAsync();
        Assert.True(alerts.Count >= 48, $"esperaba >=48 alertas sembradas, hubo {alerts.Count}");
        Assert.True(kql.Count >= 14, $"esperaba >=14 KQL sembradas, hubo {kql.Count}");

        // 2. Create
        var newId = await store.CreateAlertAsync(new AlertCreate
        {
            Name = "PoC round-trip .NET",
            AlertNumber = 999,
            Severity = "ALTA",
            Description = "Alerta de prueba del round-trip",
        });
        Assert.True(newId > 0);

        // 3. Get
        var fetched = await store.GetAlertAsync(newId);
        Assert.NotNull(fetched);
        Assert.Equal("PoC round-trip .NET", fetched!.Name);
        Assert.Equal(999, fetched.AlertNumber);
        Assert.True(fetched.IsActive);

        // 4. Update (solo campos presentes)
        var updated = await store.UpdateAlertAsync(newId, new Dictionary<string, object?>
        {
            ["severity"] = "MEDIA",
            ["description"] = "actualizada",
        });
        Assert.True(updated);
        var afterUpdate = await store.GetAlertAsync(newId);
        Assert.Equal("MEDIA", afterUpdate!.Severity);
        Assert.Equal("actualizada", afterUpdate.Description);

        // 5. Soft-delete -> desaparece del listado activo
        Assert.True(await store.SoftDeleteAlertAsync(newId));
        var afterDelete = await store.GetAlertAsync(newId);
        Assert.False(afterDelete!.IsActive);
        Assert.DoesNotContain(await store.ListAlertsAsync(), a => a.AlertId == newId);

        // 6. Seed idempotente: una segunda pasada NO duplica
        var countBefore = (await store.ListAlertsAsync(includeInactive: true)).Count;
        await store.ListKqlAsync(); // re-dispara _prepare
        var countAfter = (await store.ListAlertsAsync(includeInactive: true)).Count;
        Assert.Equal(countBefore, countAfter);
    }
}
