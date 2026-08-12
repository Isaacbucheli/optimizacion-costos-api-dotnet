using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// RetirosRecolector contra Azure SQL real. Solo con BIT_INTEGRATION_DB=1 (mismo patrón que
/// WafSubscriptionFilterDbTests). Los tres hallazgos de la revisión de grupo sobre este recolector
/// (conteo de recursos, exclusión de fin de soporte, filtro de suscripciones administradas)
/// dependen de la agregación real de SQL Server (COUNT/GROUP BY/EXISTS): un test de texto no
/// prueba el comportamiento, solo que las palabras estén ahí. Esta clase siembra filas mezcladas
/// directamente en dbo.boletin_retirement (no hay un store de "insertar una fila" para ese
/// recolector: el único escritor real es el sync completo de BoletinService) y verifica el
/// resultado de LeerAsync contra Azure SQL.
/// </summary>
public class RetirosRecolectorDbTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BIT_INTEGRATION_DB") == "1";

    private static ISqlConnectionFactory NewFactory() =>
        new SqlConnectionFactory(
            AppConfig.FromConfiguration(new ConfigurationBuilder().Build()),
            NullLogger<SqlConnectionFactory>.Instance);

    /// <summary>Inserta una fila cruda de boletin_retirement con las columnas mínimas que
    /// RetirosRecolector necesita para el escenario de prueba (el resto usa el DEFAULT del
    /// esquema: resource_name/resource_type/retiring_feature/title en '', status en 'vigente' si
    /// no se pasa). fingerprintSeed solo tiene que ser único por fila: no reproduce la fórmula real
    /// de RetirementRow.Fingerprint, solo evita chocar con la UNIQUE(client_id, fingerprint).</summary>
    private static async Task InsertRetirementRowAsync(
        SqlConnection conn, int clientId, string fingerprintSeed, string source,
        string announcementKey, string subscriptionId, string? azureResourceId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.boletin_retirement
                (client_id, fingerprint, source, announcement_key, subscription_id, azure_resource_id, status)
            VALUES (@cid, @fp, @source, @key, @sub, @resId, 'vigente')
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@fp", SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSeed))));
        cmd.Parameters.Add(new SqlParameter("@source", source));
        cmd.Parameters.Add(new SqlParameter("@key", announcementKey));
        cmd.Parameters.Add(new SqlParameter("@sub", subscriptionId));
        cmd.Parameters.Add(new SqlParameter("@resId", (object?)azureResourceId ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Cuenta_solo_recursos_excluye_eol_y_suscripciones_no_administradas()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var factory = NewFactory();
        var clients = new SqlClientStore(factory);
        var credentialStore = new SqlClientCredentialStore(factory);
        var subscriptionStore = new SqlClientSubscriptionStore(factory);

        var tag = $"e2e-retiros-{Guid.NewGuid():N}";
        var clientId = await clients.CreateAsync($"E2E retiros {tag}", null, null, null, null);
        try
        {
            var credentialId = await credentialStore.InsertAsync(
                clientId, $"cred-{tag}", "tenant-e2e", "app-e2e", $"secret-{tag}", null);

            const string subManaged = "11111111-1111-1111-1111-1111111111aa";
            const string subNoAdministrada = "22222222-2222-2222-2222-2222222222bb";
            var idManaged = await subscriptionStore.InsertManualAsync(clientId, credentialId, subManaged, "sub-managed");
            var idNoAdministrada = await subscriptionStore.InsertManualAsync(clientId, credentialId, subNoAdministrada, "sub-no-administrada");
            // Explícito: no depender del DEFAULT del esquema para is_active/is_managed.
            await subscriptionStore.UpdateAsync(idManaged, name: null, isActive: true, isManaged: true);
            await subscriptionStore.UpdateAsync(idNoAdministrada, name: null, isActive: true, isManaged: false);

            await using var conn = await factory.OpenAsync();
            await BoletinService.EnsureSchemaAsync(conn, CancellationToken.None);

            // Mismo anuncio (AN-1), misma suscripcion administrada: 2 filas CON recurso + 1 SIN
            // recurso (el caso comun de Service Health que documenta la clase). Si el conteo
            // volviera a COUNT(*), recursos_afectados daria 3 en vez de 2.
            await InsertRetirementRowAsync(conn, clientId, $"{tag}-1", "service_health", "AN-1", subManaged,
                $"/subscriptions/{subManaged}/resourceGroups/rg/providers/microsoft.compute/virtualmachines/vm-1");
            await InsertRetirementRowAsync(conn, clientId, $"{tag}-2", "service_health", "AN-1", subManaged,
                $"/subscriptions/{subManaged}/resourceGroups/rg/providers/microsoft.compute/virtualmachines/vm-2");
            await InsertRetirementRowAsync(conn, clientId, $"{tag}-3", "service_health", "AN-1", subManaged, null);

            // Fin de soporte: NO es un retiro (BoletinAggregator lo cuenta aparte). No debe salir.
            await InsertRetirementRowAsync(conn, clientId, $"{tag}-4", "eol", "AN-EOL", subManaged,
                $"/subscriptions/{subManaged}/resourceGroups/rg/providers/microsoft.compute/virtualmachines/vm-eol");

            // Suscripcion que el usuario dejo de administrar: tampoco debe salir.
            await InsertRetirementRowAsync(conn, clientId, $"{tag}-5", "advisor", "AN-NOADMIN", subNoAdministrada,
                $"/subscriptions/{subNoAdministrada}/resourceGroups/rg/providers/microsoft.compute/virtualmachines/vm-noadmin");

            var retiros = await RetirosRecolector.LeerAsync(conn, clientId);

            var unico = Assert.Single(retiros);
            Assert.Equal("AN-1", unico.AnnouncementKey);
            Assert.Equal(2, unico.RecursosAfectados); // no 3: la fila sin recurso no cuenta
        }
        finally
        {
            await using var conn = await factory.OpenAsync();
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DELETE FROM dbo.boletin_retirement WHERE client_id = @id";
            cleanup.Parameters.Add(new SqlParameter("@id", clientId));
            await cleanup.ExecuteNonQueryAsync();
            await clients.DeleteClientCascadeAsync(clientId); // borra credenciales + suscripciones
        }
    }
}
