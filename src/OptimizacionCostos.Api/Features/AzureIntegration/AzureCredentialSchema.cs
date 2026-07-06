using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.AzureIntegration;

/// <summary>
/// Columnas de sesiones de usuario en dbo.client_azure_credentials (schema-lazy).
/// auth_type: 'app_secret' (default) | 'user_session'. session_user_email: dueño de la sesión.
/// </summary>
public static class AzureCredentialSchema
{
    public static async Task EnsureAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF COL_LENGTH('dbo.client_azure_credentials', 'auth_type') IS NULL
                ALTER TABLE dbo.client_azure_credentials ADD auth_type VARCHAR(20) NOT NULL CONSTRAINT DF_cac_auth_type DEFAULT 'app_secret';
            IF COL_LENGTH('dbo.client_azure_credentials', 'session_user_email') IS NULL
                ALTER TABLE dbo.client_azure_credentials ADD session_user_email VARCHAR(255) NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
