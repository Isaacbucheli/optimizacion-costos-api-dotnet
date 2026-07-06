using System.Globalization;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.AzureIntegration;

public sealed record SubscriptionListItem(
    int ClientSubscriptionId, int ClientId, int CredentialId, string? CredentialName,
    string SubscriptionId, string? SubscriptionName, bool IsActive, bool IsManaged,
    string? CreatedAt, string? LastSyncedAt);

public sealed record ActiveCredential(int CredentialId, string? CredentialName);

/// <summary>
/// Acceso a dbo.client_azure_subscriptions. Port de los accesos SQL de
/// app/routes/azure_subscriptions.py. is_managed lo decide el usuario; el sync NUNCA lo toca.
/// </summary>
public interface IClientSubscriptionStore
{
    Task EnsureSchemaAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SubscriptionListItem>> ListByClientAsync(int clientId, CancellationToken ct = default);
    Task<IReadOnlyList<ActiveCredential>> ActiveCredentialsAsync(int clientId, CancellationToken ct = default);
    Task<string> UpsertAsync(int clientId, int credentialId, string subscriptionId, string? subscriptionName, CancellationToken ct = default);
    Task<int> DeactivateMissingAsync(int clientId, IReadOnlyCollection<string> keepSubscriptionIds, CancellationToken ct = default);
    Task<int?> ClientIdForCredentialAsync(int credentialId, CancellationToken ct = default);
    Task<int?> ClientIdForSubscriptionAsync(int clientSubscriptionId, CancellationToken ct = default);
    Task<int> InsertManualAsync(int clientId, int credentialId, string subscriptionId, string? subscriptionName, CancellationToken ct = default);
    Task<bool> UpdateAsync(int clientSubscriptionId, string? name, bool? isActive, bool? isManaged, CancellationToken ct = default);
}

public sealed class SqlClientSubscriptionStore(ISqlConnectionFactory factory) : IClientSubscriptionStore
{
    private static string? D(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetValue(i) is DateTime dt
            ? dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
            : r.GetValue(i).ToString();

    // Port de _ensure_subscription_schema: agrega updated_at / last_synced_at / is_managed si faltan.
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
    }

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF COL_LENGTH('dbo.client_azure_subscriptions', 'updated_at') IS NULL
                ALTER TABLE dbo.client_azure_subscriptions ADD updated_at DATETIME2 NULL;
            IF COL_LENGTH('dbo.client_azure_subscriptions', 'last_synced_at') IS NULL
                ALTER TABLE dbo.client_azure_subscriptions ADD last_synced_at DATETIME2 NULL;
            IF COL_LENGTH('dbo.client_azure_subscriptions', 'is_managed') IS NULL
                ALTER TABLE dbo.client_azure_subscriptions ADD is_managed BIT NOT NULL DEFAULT 1;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SubscriptionListItem>> ListByClientAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.client_subscription_id, s.client_id, s.credential_id, c.credential_name,
                   s.subscription_id, s.subscription_name, s.is_active, s.is_managed,
                   s.created_at, s.last_synced_at
            FROM dbo.client_azure_subscriptions s
            INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
            WHERE s.client_id = @cid ORDER BY s.created_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<SubscriptionListItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SubscriptionListItem(
                r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                !r.IsDBNull(6) && r.GetBoolean(6), r.IsDBNull(7) || r.GetBoolean(7), // is_managed: null→true
                D(r, 8), D(r, 9)));
        return list;
    }

    public async Task<IReadOnlyList<ActiveCredential>> ActiveCredentialsAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT credential_id, credential_name FROM dbo.client_azure_credentials
            WHERE client_id = @cid AND is_active = 1 ORDER BY created_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<ActiveCredential>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ActiveCredential(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1)));
        return list;
    }

    public async Task<string> UpsertAsync(
        int clientId, int credentialId, string subscriptionId, string? subscriptionName, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using (var find = conn.CreateCommand())
        {
            find.CommandText = """
                SELECT client_subscription_id FROM dbo.client_azure_subscriptions
                WHERE client_id = @cid AND subscription_id = @sid
                """;
            find.Parameters.Add(new SqlParameter("@cid", clientId));
            find.Parameters.Add(new SqlParameter("@sid", subscriptionId));
            var existing = await find.ExecuteScalarAsync(ct);
            if (existing is not null and not DBNull)
            {
                await using var upd = conn.CreateCommand();
                upd.CommandText = """
                    UPDATE dbo.client_azure_subscriptions
                    SET credential_id = @cred, subscription_name = @name, is_active = 1,
                        updated_at = SYSUTCDATETIME(), last_synced_at = SYSUTCDATETIME()
                    WHERE client_subscription_id = @id
                    """;
                upd.Parameters.Add(new SqlParameter("@cred", credentialId));
                upd.Parameters.Add(new SqlParameter("@name", (object?)subscriptionName ?? DBNull.Value));
                upd.Parameters.Add(new SqlParameter("@id", Convert.ToInt32(existing)));
                await upd.ExecuteNonQueryAsync(ct);
                return "updated";
            }
        }
        await using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO dbo.client_azure_subscriptions
                    (client_id, credential_id, subscription_id, subscription_name, is_active, last_synced_at)
                VALUES (@cid, @cred, @sid, @name, 1, SYSUTCDATETIME())
                """;
            ins.Parameters.Add(new SqlParameter("@cid", clientId));
            ins.Parameters.Add(new SqlParameter("@cred", credentialId));
            ins.Parameters.Add(new SqlParameter("@sid", subscriptionId));
            ins.Parameters.Add(new SqlParameter("@name", (object?)subscriptionName ?? DBNull.Value));
            await ins.ExecuteNonQueryAsync(ct);
        }
        return "created";
    }

    public async Task<int> DeactivateMissingAsync(
        int clientId, IReadOnlyCollection<string> keepSubscriptionIds, CancellationToken ct = default)
    {
        if (keepSubscriptionIds.Count == 0) return 0;
        await using var conn = await factory.OpenAsync(ct);
        var inParams = keepSubscriptionIds.Select((_, i) => $"@s{i}").ToList();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE dbo.client_azure_subscriptions
            SET is_active = 0, updated_at = SYSUTCDATETIME(), last_synced_at = SYSUTCDATETIME()
            WHERE client_id = @cid AND is_active = 1 AND subscription_id NOT IN ({string.Join(", ", inParams)})
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var i = 0;
        foreach (var sid in keepSubscriptionIds)
            cmd.Parameters.Add(new SqlParameter($"@s{i++}", sid));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int?> ClientIdForCredentialAsync(int credentialId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.client_azure_credentials WHERE credential_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", credentialId));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt32(v);
    }

    public async Task<int?> ClientIdForSubscriptionAsync(int clientSubscriptionId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.client_azure_subscriptions WHERE client_subscription_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", clientSubscriptionId));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt32(v);
    }

    public async Task<int> InsertManualAsync(
        int clientId, int credentialId, string subscriptionId, string? subscriptionName, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.client_azure_subscriptions (client_id, credential_id, subscription_id, subscription_name)
            OUTPUT INSERTED.client_subscription_id
            VALUES (@cid, @cred, @sid, @name)
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@cred", credentialId));
        cmd.Parameters.Add(new SqlParameter("@sid", subscriptionId));
        cmd.Parameters.Add(new SqlParameter("@name", (object?)subscriptionName ?? DBNull.Value));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<bool> UpdateAsync(
        int clientSubscriptionId, string? name, bool? isActive, bool? isManaged, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        var sets = new List<string>();
        var ps = new List<SqlParameter>();
        if (name is not null) { sets.Add("subscription_name = @name"); ps.Add(new SqlParameter("@name", name)); }
        if (isActive is not null) { sets.Add("is_active = @active"); ps.Add(new SqlParameter("@active", isActive.Value ? 1 : 0)); }
        if (isManaged is not null) { sets.Add("is_managed = @managed"); ps.Add(new SqlParameter("@managed", isManaged.Value ? 1 : 0)); }
        if (sets.Count == 0) return false;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE dbo.client_azure_subscriptions SET {string.Join(", ", sets)} WHERE client_subscription_id = @id";
        foreach (var p in ps) cmd.Parameters.Add(p);
        cmd.Parameters.Add(new SqlParameter("@id", clientSubscriptionId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
