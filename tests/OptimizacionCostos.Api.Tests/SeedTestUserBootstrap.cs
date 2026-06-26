using Microsoft.Extensions.Configuration;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Tests;

/// <summary>
/// Utilitario gateado (BIT_SEED_USER=1): crea dbo.app_users si falta y deja un admin
/// de prueba en la BD destino, para poder validar en vivo el flujo autenticado contra
/// la API desplegada. NO corre en la suite normal.
/// </summary>
public class SeedTestUserBootstrap
{
    [Fact]
    public async Task Seed_admin_de_prueba()
    {
        if (Environment.GetEnvironmentVariable("BIT_SEED_USER") != "1") return;

        var email = Environment.GetEnvironmentVariable("BIT_SEED_EMAIL") ?? "poc-dotnet@bit.ec";
        var config = AppConfig.FromConfiguration(new ConfigurationBuilder().Build());
        var factory = new SqlConnectionFactory(config);

        await using var conn = await factory.OpenAsync();
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = """
                IF OBJECT_ID('dbo.app_users','U') IS NULL
                CREATE TABLE dbo.app_users (
                    user_id INT IDENTITY(1,1) PRIMARY KEY,
                    email NVARCHAR(255) NOT NULL UNIQUE,
                    full_name NVARCHAR(200) NOT NULL,
                    role NVARCHAR(30) NOT NULL,
                    password_hash NVARCHAR(255) NOT NULL,
                    is_active BIT NOT NULL DEFAULT 1,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2 NULL
                );
                """;
            await ddl.ExecuteNonQueryAsync();
        }
        await using (var up = conn.CreateCommand())
        {
            up.CommandText = """
                MERGE dbo.app_users AS t
                USING (SELECT @e AS email) AS s ON LOWER(t.email) = LOWER(s.email)
                WHEN MATCHED THEN UPDATE SET role='admin', is_active=1, full_name='PoC .NET'
                WHEN NOT MATCHED THEN
                    INSERT (email, full_name, role, password_hash, is_active)
                    VALUES (@e, 'PoC .NET', 'admin', 'x', 1);
                """;
            up.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@e", email));
            await up.ExecuteNonQueryAsync();
        }
        Assert.True(true);
    }
}
