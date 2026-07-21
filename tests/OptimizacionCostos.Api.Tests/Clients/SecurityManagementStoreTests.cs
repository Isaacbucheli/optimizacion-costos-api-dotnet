using Microsoft.Extensions.Configuration;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Clients;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Clients;

/// <summary>
/// Round-trip REAL contra Azure SQL del flag security_managed_externally + nota (dbo.clients).
/// Solo corre con BIT_INTEGRATION_DB=1 (mismo gate que DbRoundTripTests); si no, es no-op.
/// Ejercita: default false → set (true + nota) → get → set nota vacía = NULL → cleanup.
/// </summary>
public class SecurityManagementStoreTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BIT_INTEGRATION_DB") == "1";

    private static IClientStore NewStore()
    {
        var config = AppConfig.FromConfiguration(new ConfigurationBuilder().Build());
        return new SqlClientStore(new SqlConnectionFactory(config));
    }

    [Fact]
    public async Task Flag_Y_Nota_RoundTrip()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var store = NewStore();
        var clientId = await store.CreateAsync("E2E sec-mgmt round-trip", null, null, null, null);
        try
        {
            // 1. Default: cliente nuevo → no gestionado, sin nota.
            var initial = await store.GetSecurityManagementAsync(clientId);
            Assert.False(initial.Managed);
            Assert.Null(initial.Note);

            // 2. Set true + nota → persiste.
            await store.SetSecurityManagementAsync(clientId, true, "Revisado por Gestión de Vulnerabilidades");
            var afterSet = await store.GetSecurityManagementAsync(clientId);
            Assert.True(afterSet.Managed);
            Assert.Equal("Revisado por Gestión de Vulnerabilidades", afterSet.Note);

            // 3. Nota en blanco → se guarda NULL (flag sigue activable).
            await store.SetSecurityManagementAsync(clientId, true, "   ");
            var afterBlank = await store.GetSecurityManagementAsync(clientId);
            Assert.True(afterBlank.Managed);
            Assert.Null(afterBlank.Note);

            // 4. Apagar.
            await store.SetSecurityManagementAsync(clientId, false, null);
            var afterOff = await store.GetSecurityManagementAsync(clientId);
            Assert.False(afterOff.Managed);
        }
        finally
        {
            await store.DeleteClientCascadeAsync(clientId);
        }
    }
}
