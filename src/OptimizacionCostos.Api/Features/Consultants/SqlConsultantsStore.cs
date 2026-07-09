using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Consultants;

/// <summary>
/// Implementación SQL del módulo Asignación de consultores. Asegura el schema de forma
/// perezosa (espejo de SqlPolicyCatalogStore, sin seed), por lo que el arranque no
/// depende de la BD. Las operaciones que tocan varias tablas van en transacción.
/// </summary>
public sealed class SqlConsultantsStore(ISqlConnectionFactory factory) : IConsultantsStore
{
    private const string PersonSelect = """
        SELECT person_id, name, email, person_type, is_active
        FROM dbo.cdc_people
        """;

    private const string AssignmentSelect = """
        SELECT a.assignment_id, a.client_name, a.service, a.category, a.databases, a.country,
               a.status, a.access_accounts, a.account_role, a.lighthouse, a.client_contact_name,
               a.client_contact_phone, a.client_contact_email, a.contract_end, a.observations,
               a.is_active, a.coordinator_id, pc.name, a.comercial_id, pm.name
        FROM dbo.cdc_assignments a
        LEFT JOIN dbo.cdc_people pc ON pc.person_id = a.coordinator_id
        LEFT JOIN dbo.cdc_people pm ON pm.person_id = a.comercial_id
        """;

    // ---------- Personas ----------

    public async Task<IReadOnlyList<PersonItem>> ListPeopleAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{PersonSelect} ORDER BY name, person_id";

        var items = new List<PersonItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(MapPerson(reader));
        return items;
    }

    public async Task<PersonItem?> GetPersonAsync(int personId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{PersonSelect} WHERE person_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", personId));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapPerson(reader) : null;
    }

    public async Task<int> CreatePersonAsync(PersonCreate data, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.cdc_people (name, email, person_type)
            OUTPUT INSERTED.person_id VALUES (@name, @email, @type)
            """;
        cmd.Parameters.Add(new SqlParameter("@name", data.Name));
        cmd.Parameters.Add(new SqlParameter("@email", (object?)data.Email ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@type", data.PersonType));

        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// UPDATE dinámico: solo columnas presentes en `fields` que estén en la whitelist.
    /// Los nombres de columna nunca vienen del usuario; los valores van como parámetros.
    /// </summary>
    public async Task<bool> UpdatePersonAsync(int personId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var sets = new List<string>();
        var parameters = new List<SqlParameter>();
        var i = 0;
        foreach (var col in ConsultantColumns.Person)
        {
            if (!fields.TryGetValue(col, out var value)) continue;
            sets.Add($"{col} = @p{i}");
            parameters.Add(new SqlParameter($"@p{i}", value ?? DBNull.Value));
            i++;
        }
        if (sets.Count == 0) return false;

        sets.Add("updated_at = SYSUTCDATETIME()");
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE dbo.cdc_people SET {string.Join(", ", sets)} WHERE person_id = @id";
        cmd.Parameters.AddRange(parameters.ToArray());
        cmd.Parameters.Add(new SqlParameter("@id", personId));

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SoftDeletePersonAsync(int personId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE dbo.cdc_people SET is_active = 0, updated_at = SYSUTCDATETIME() WHERE person_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", personId));

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlySet<int>> GetActivePersonIdsAsync(IReadOnlyCollection<int> personIds, CancellationToken ct = default)
    {
        var result = new HashSet<int>();
        if (personIds.Count == 0) return result;

        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        var distinct = personIds.Distinct().ToArray();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT person_id FROM dbo.cdc_people WHERE is_active = 1 AND person_id IN " +
            $"({string.Join(", ", distinct.Select((_, i) => $"@p{i}"))})";
        for (var i = 0; i < distinct.Length; i++)
            cmd.Parameters.Add(new SqlParameter($"@p{i}", distinct[i]));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetInt32(0));
        return result;
    }

    // ---------- Asignaciones ----------

    public async Task<IReadOnlyList<AssignmentItem>> ListAssignmentsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        var items = new List<AssignmentItem>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"{AssignmentSelect} WHERE a.is_active = 1 ORDER BY a.client_name, a.assignment_id";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                items.Add(MapAssignment(reader));
        }

        var links = await LoadLinksAsync(conn, assignmentId: null, ct);
        return items.Select(item => WithLinks(item, links)).ToList();
    }

    public async Task<AssignmentItem?> GetAssignmentAsync(int assignmentId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        AssignmentItem? item = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"{AssignmentSelect} WHERE a.assignment_id = @id";
            cmd.Parameters.Add(new SqlParameter("@id", assignmentId));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                item = MapAssignment(reader);
        }
        if (item is null) return null;

        var links = await LoadLinksAsync(conn, assignmentId, ct);
        return WithLinks(item, links);
    }

    public async Task<int> CreateAssignmentAsync(AssignmentCreate data, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        int id;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dbo.cdc_assignments
                    (client_name, service, category, databases, country, status,
                     coordinator_id, comercial_id, access_accounts, account_role, lighthouse,
                     client_contact_name, client_contact_phone, client_contact_email,
                     contract_end, observations)
                OUTPUT INSERTED.assignment_id
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15)
                """;
            var values = new object?[]
            {
                data.ClientName, data.Service, data.Category, data.Databases, data.Country,
                data.Status ?? "ACTIVO", data.CoordinatorId, data.ComercialId, data.AccessAccounts,
                data.AccountRole, data.Lighthouse, data.ClientContactName, data.ClientContactPhone,
                data.ClientContactEmail, data.ContractEnd?.ToDateTime(TimeOnly.MinValue), data.Observations,
            };
            for (var i = 0; i < values.Length; i++)
                cmd.Parameters.Add(new SqlParameter($"@p{i}", values[i] ?? DBNull.Value));
            id = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }

        await ReplaceRoleLinksAsync(conn, tx, id, "principal", data.PrincipalIds ?? [], ct);
        if (data.BackupIds is { Length: > 0 })
            await ReplaceRoleLinksAsync(conn, tx, id, "backup", data.BackupIds, ct);

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<bool> UpdateAssignmentAsync(int assignmentId, IReadOnlyDictionary<string, object?> fields,
        IReadOnlyList<int>? principalIds, IReadOnlyList<int>? backupIds, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM dbo.cdc_assignments WHERE assignment_id = @id";
            check.Parameters.Add(new SqlParameter("@id", assignmentId));
            if ((int)(await check.ExecuteScalarAsync(ct))! == 0) return false;
        }

        var sets = new List<string>();
        var parameters = new List<SqlParameter>();
        var i = 0;
        foreach (var col in ConsultantColumns.Assignment)
        {
            if (!fields.TryGetValue(col, out var value)) continue;
            sets.Add($"{col} = @p{i}");
            parameters.Add(new SqlParameter($"@p{i}", value ?? DBNull.Value));
            i++;
        }
        sets.Add("updated_at = SYSUTCDATETIME()");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE dbo.cdc_assignments SET {string.Join(", ", sets)} WHERE assignment_id = @id";
            cmd.Parameters.AddRange(parameters.ToArray());
            cmd.Parameters.Add(new SqlParameter("@id", assignmentId));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (principalIds is not null)
            await ReplaceRoleLinksAsync(conn, tx, assignmentId, "principal", principalIds, ct);
        if (backupIds is not null)
            await ReplaceRoleLinksAsync(conn, tx, assignmentId, "backup", backupIds, ct);

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> SoftDeleteAssignmentAsync(int assignmentId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE dbo.cdc_assignments SET is_active = 0, updated_at = SYSUTCDATETIME() WHERE assignment_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", assignmentId));

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---------- Reasignación masiva ----------

    public async Task<int> ReassignAsync(int fromPersonId, int toPersonId, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        var changed = new HashSet<int>();
        foreach (var scope in scopes)
        {
            switch (scope)
            {
                case "principal":
                case "backup":
                    await ReassignRoleLinksAsync(conn, tx, scope, fromPersonId, toPersonId, changed, ct);
                    break;
                case "coordinador":
                    await ReassignColumnAsync(conn, tx, "coordinator_id", fromPersonId, toPersonId, changed, ct);
                    break;
                case "comercial":
                    await ReassignColumnAsync(conn, tx, "comercial_id", fromPersonId, toPersonId, changed, ct);
                    break;
            }
        }

        await tx.CommitAsync(ct);
        return changed.Count;
    }

    private static async Task ReassignRoleLinksAsync(SqlConnection conn, SqlTransaction tx, string role,
        int fromId, int toId, HashSet<int> changed, CancellationToken ct)
    {
        // Asignaciones ACTIVAS donde la persona origen tiene el rol.
        await using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT ac.assignment_id
                FROM dbo.cdc_assignment_consultants ac
                JOIN dbo.cdc_assignments a ON a.assignment_id = ac.assignment_id
                WHERE ac.person_id = @from AND ac.role = @role AND a.is_active = 1
                """;
            sel.Parameters.Add(new SqlParameter("@from", fromId));
            sel.Parameters.Add(new SqlParameter("@role", role));
            await using var reader = await sel.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                changed.Add(reader.GetInt32(0));
        }

        // Merge: si la destino YA está en la misma asignación+rol, se elimina el vínculo
        // de la origen en vez de duplicar la PK compuesta.
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = """
                DELETE ac
                FROM dbo.cdc_assignment_consultants ac
                JOIN dbo.cdc_assignments a ON a.assignment_id = ac.assignment_id
                WHERE ac.person_id = @from AND ac.role = @role AND a.is_active = 1
                  AND EXISTS (SELECT 1 FROM dbo.cdc_assignment_consultants d
                              WHERE d.assignment_id = ac.assignment_id
                                AND d.role = ac.role AND d.person_id = @to)
                """;
            del.Parameters.Add(new SqlParameter("@from", fromId));
            del.Parameters.Add(new SqlParameter("@role", role));
            del.Parameters.Add(new SqlParameter("@to", toId));
            await del.ExecuteNonQueryAsync(ct);
        }

        // El resto de vínculos se mueve a la persona destino.
        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE ac SET person_id = @to
                FROM dbo.cdc_assignment_consultants ac
                JOIN dbo.cdc_assignments a ON a.assignment_id = ac.assignment_id
                WHERE ac.person_id = @from AND ac.role = @role AND a.is_active = 1
                """;
            upd.Parameters.Add(new SqlParameter("@to", toId));
            upd.Parameters.Add(new SqlParameter("@from", fromId));
            upd.Parameters.Add(new SqlParameter("@role", role));
            await upd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>`column` viene solo del switch interno (coordinator_id/comercial_id), nunca del usuario.</summary>
    private static async Task ReassignColumnAsync(SqlConnection conn, SqlTransaction tx, string column,
        int fromId, int toId, HashSet<int> changed, CancellationToken ct)
    {
        await using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = $"SELECT assignment_id FROM dbo.cdc_assignments WHERE {column} = @from AND is_active = 1";
            sel.Parameters.Add(new SqlParameter("@from", fromId));
            await using var reader = await sel.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                changed.Add(reader.GetInt32(0));
        }

        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText =
                $"UPDATE dbo.cdc_assignments SET {column} = @to, updated_at = SYSUTCDATETIME() " +
                $"WHERE {column} = @from AND is_active = 1";
            upd.Parameters.Add(new SqlParameter("@to", toId));
            upd.Parameters.Add(new SqlParameter("@from", fromId));
            await upd.ExecuteNonQueryAsync(ct);
        }
    }

    // ---------- Helpers ----------

    private static Task PrepareAsync(SqlConnection conn, CancellationToken ct)
        => ConsultantsSchema.EnsureSchemaAsync(conn, ct);

    /// <summary>Reemplaza el conjunto completo de un rol (DELETE del rol + INSERT nuevos, deduplicados).</summary>
    private static async Task ReplaceRoleLinksAsync(SqlConnection conn, SqlTransaction tx,
        int assignmentId, string role, IReadOnlyList<int> personIds, CancellationToken ct)
    {
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM dbo.cdc_assignment_consultants WHERE assignment_id = @a AND role = @r";
            del.Parameters.Add(new SqlParameter("@a", assignmentId));
            del.Parameters.Add(new SqlParameter("@r", role));
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (var pid in personIds.Distinct())
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText =
                "INSERT INTO dbo.cdc_assignment_consultants (assignment_id, person_id, role) VALUES (@a, @p, @r)";
            ins.Parameters.Add(new SqlParameter("@a", assignmentId));
            ins.Parameters.Add(new SqlParameter("@p", pid));
            ins.Parameters.Add(new SqlParameter("@r", role));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Vínculos persona↔asignación con nombre, ordenados por nombre (para embeber).</summary>
    private static async Task<Dictionary<int, (List<PersonRef> Principals, List<PersonRef> Backups)>> LoadLinksAsync(
        SqlConnection conn, int? assignmentId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ac.assignment_id, ac.person_id, ac.role, p.name
            FROM dbo.cdc_assignment_consultants ac
            JOIN dbo.cdc_people p ON p.person_id = ac.person_id
            """ + (assignmentId is null ? "" : " WHERE ac.assignment_id = @id") + " ORDER BY p.name, ac.person_id";
        if (assignmentId is not null)
            cmd.Parameters.Add(new SqlParameter("@id", assignmentId.Value));

        var map = new Dictionary<int, (List<PersonRef> Principals, List<PersonRef> Backups)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var aid = reader.GetInt32(0);
            if (!map.TryGetValue(aid, out var lists))
            {
                lists = ([], []);
                map[aid] = lists;
            }
            var person = new PersonRef(reader.GetInt32(1), reader.GetString(3));
            (reader.GetString(2) == "principal" ? lists.Principals : lists.Backups).Add(person);
        }
        return map;
    }

    private static AssignmentItem WithLinks(AssignmentItem item,
        Dictionary<int, (List<PersonRef> Principals, List<PersonRef> Backups)> links)
        => links.TryGetValue(item.AssignmentId, out var l)
            ? item with { Principals = l.Principals, Backups = l.Backups }
            : item;

    private static PersonItem MapPerson(SqlDataReader r) => new()
    {
        PersonId = r.GetInt32(0),
        Name = r.GetString(1),
        Email = r.IsDBNull(2) ? null : r.GetString(2),
        PersonType = r.GetString(3),
        IsActive = r.GetBoolean(4),
    };

    private static AssignmentItem MapAssignment(SqlDataReader r) => new()
    {
        AssignmentId = r.GetInt32(0),
        ClientName = r.GetString(1),
        Service = r.IsDBNull(2) ? null : r.GetString(2),
        Category = r.IsDBNull(3) ? null : r.GetString(3),
        Databases = r.IsDBNull(4) ? null : r.GetString(4),
        Country = r.IsDBNull(5) ? null : r.GetString(5),
        Status = r.IsDBNull(6) ? null : r.GetString(6),
        AccessAccounts = r.IsDBNull(7) ? null : r.GetString(7),
        AccountRole = r.IsDBNull(8) ? null : r.GetString(8),
        Lighthouse = r.IsDBNull(9) ? null : r.GetString(9),
        ClientContactName = r.IsDBNull(10) ? null : r.GetString(10),
        ClientContactPhone = r.IsDBNull(11) ? null : r.GetString(11),
        ClientContactEmail = r.IsDBNull(12) ? null : r.GetString(12),
        ContractEnd = r.IsDBNull(13) ? null : DateOnly.FromDateTime(r.GetDateTime(13)),
        Observations = r.IsDBNull(14) ? null : r.GetString(14),
        IsActive = r.GetBoolean(15),
        Coordinator = r.IsDBNull(16) ? null : new PersonRef(r.GetInt32(16), r.GetString(17)),
        Comercial = r.IsDBNull(18) ? null : new PersonRef(r.GetInt32(18), r.GetString(19)),
    };
}
