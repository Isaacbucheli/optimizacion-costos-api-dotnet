using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Boletin;

public sealed record NovedadClienteRow(
    int Id, int NovedadId, int ClientId, string Estado,
    string? PorQue, string? DecididoPor, DateTime? DecididoAt);

public interface IBoletinNovedadClienteStore
{
    /// <summary>Evalúa con IA las novedades activas AÚN SIN evaluación para este cliente.
    /// Devuelve (Evaluadas, Candidatas): Candidatas son SOLO las que quedaron <c>pendiente</c>
    /// (aplica=true, esperan revisión del consultor), no el total del lote evaluado — el toast del
    /// front dice "N candidata(s) de M evaluada(s)". 0 evaluadas = ya está al día.</summary>
    Task<(int Evaluadas, int Candidatas)> EvaluarPendientesAsync(int clientId, CancellationToken ct = default);

    /// <summary>Novedades ya evaluadas y visibles para el cliente: SOLO estado aprobada/pendiente
    /// (rechazada y no_aplica nunca salen de acá — ver <see cref="NovedadClienteEstados"/>).</summary>
    Task<IReadOnlyList<(NovedadRow Novedad, NovedadClienteRow Estado)>> ListAsync(int clientId, CancellationToken ct = default);

    /// <summary>client_id dueño de la fila (o null si no existe). Patrón FindingStateOwner de
    /// OptimizationController: el controller lo usa para verificar acceso ANTES de llamar a
    /// <see cref="DecidirAsync"/>.</summary>
    Task<int?> OwnerClientIdAsync(int id, CancellationToken ct = default);

    /// <summary>Decide el estado final de una fila (aprobada|rechazada|pendiente — nunca no_aplica,
    /// esa la asigna solo la evaluación IA). <paramref name="clientId"/> se re-verifica en el WHERE
    /// (defensa en profundidad además del check de acceso que ya hizo el controller): si la fila no
    /// pertenece a ese cliente, devuelve false igual que un id inexistente. Una fila ya en
    /// <c>no_aplica</c> también devuelve false y NO se muta: ese veredicto de la IA es terminal e
    /// invisible (el GET nunca lo expone), así que el id no puede haberse obtenido del front — revivirlo
    /// exigiría una re-evaluación explícita, no un PUT sobre un id adivinado. <paramref name="setPorQue"/>
    /// distingue "el body trae por_que" (incluso null, se escribe) de "el body no lo incluye" (se
    /// conserva el valor actual, típicamente el texto que puso la IA al evaluar).</summary>
    Task<bool> DecidirAsync(int id, int clientId, string estado, string? porQue, bool setPorQue, string actor, CancellationToken ct = default);
}

/// <summary>Estados posibles de <c>dbo.boletin_novedad_cliente</c>. <c>no_aplica</c> es terminal: solo
/// lo asigna <see cref="BoletinNovedadClienteStore.EvaluarPendientesAsync"/> cuando la IA determina
/// que la novedad no aplica al inventario del cliente — nunca es un valor decidible por el consultor
/// vía PUT (por eso no está en <see cref="DecidiblesValidos"/>).</summary>
public static class NovedadClienteEstados
{
    public const string Pendiente = "pendiente";
    public const string Aprobada = "aprobada";
    public const string Rechazada = "rechazada";
    public const string NoAplica = "no_aplica";

    public static readonly string[] DecidiblesValidos = [Aprobada, Rechazada, Pendiente];
}

/// <summary>Lógica PURA (sin SQL/Azure) de re-emparejar los resultados del evaluador IA con las
/// novedades candidatas. Separada del store para poder testearla sin BD/credenciales reales — mismo
/// patrón que BoletinSyncPlan/BoletinEol en este módulo.</summary>
internal static class BoletinNovedadClientePlan
{
    /// <summary>Mapea SIEMPRE por <c>FeedGuid</c> — <see cref="IBoletinNovedadEvaluator.EvaluarAsync"/>
    /// devuelve la lista en ORDEN LIBRE, así que emparejar por índice/posición produciría filas con el
    /// NovedadId equivocado (nit duro documentado en el review de T3). Evaluaciones con un guid que no
    /// pertenece a <paramref name="candidatas"/> se descartan defensivamente (no debería ocurrir, el
    /// evaluador ya valida esto contra el lote que él mismo recibió). <c>aplica=false</c> siempre
    /// resulta en <see cref="NovedadClienteEstados.NoAplica"/> con <c>PorQue=null</c>, sin importar lo
    /// que haya llegado en <see cref="EvaluacionNovedad.PorQue"/> (defensa en profundidad, mismo
    /// principio que BoletinEvaluatorParsers.ParseRespuesta).</summary>
    internal static IReadOnlyList<(int NovedadId, string Estado, string? PorQue)> MapEvaluaciones(
        IReadOnlyList<NovedadRow> candidatas, IReadOnlyList<EvaluacionNovedad> evaluaciones)
    {
        var porGuid = candidatas.ToDictionary(n => n.FeedGuid, n => n, StringComparer.Ordinal);
        var result = new List<(int, string, string?)>(evaluaciones.Count);
        foreach (var e in evaluaciones)
        {
            if (!porGuid.TryGetValue(e.FeedGuid, out var novedad)) continue;
            result.Add(e.Aplica
                ? (novedad.Id, NovedadClienteEstados.Pendiente, e.PorQue)
                : (novedad.Id, NovedadClienteEstados.NoAplica, null));
        }
        return result;
    }
}

/// <summary>Evaluación IA de novedades POR CLIENTE (Fase 2 Entrega 3, Task 4): a diferencia del
/// catálogo global de novedades (T2), esta tabla vincula cada novedad con un cliente concreto y su
/// veredicto (aplica/no aplica al inventario real) + la decisión humana final. Schema lazy (patrón
/// lifecycle/novedad global).</summary>
public sealed class BoletinNovedadClienteStore(
    ISqlConnectionFactory factory, IAzureCredentialFactory credentials, IResourceGraphRunner rg,
    IBoletinNovedadEvaluator evaluator, ILogger<BoletinNovedadClienteStore> logger) : IBoletinNovedadClienteStore
{
    private static object Db(object? v) => v ?? DBNull.Value;

    public async Task<(int Evaluadas, int Candidatas)> EvaluarPendientesAsync(int clientId, CancellationToken ct = default)
    {
        // Chequeo más barato primero (sin I/O): si la IA está apagada, ni siquiera vale la pena
        // tocar SQL/Azure. El controller traduce esta excepción a 503.
        if (!evaluator.IsConfigured)
            throw new InvalidOperationException("La IA no está configurada; no es posible evaluar novedades.");

        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        var candidatas = await LoadCandidatasAsync(conn, clientId, ct);
        if (candidatas.Count == 0) return (0, 0); // ya está al día: no hace falta inventario ni IA

        var groups = await ManagedSubscriptionsAsync(conn, clientId, ct);
        var inventario = await BuildInventarioAsync(groups, clientId, ct);

        var evaluaciones = await evaluator.EvaluarAsync(inventario, candidatas, ct);
        var mapeadas = BoletinNovedadClientePlan.MapEvaluaciones(candidatas, evaluaciones);

        var evaluadas = 0;
        var candidatasNuevas = 0;
        foreach (var (novedadId, estado, porQue) in mapeadas)
        {
            // InsertResultadoAsync devuelve false cuando pierde la carrera de doble evaluación
            // simultánea (2627 del UNIQUE): esa fila la insertó la otra evaluación, así que NO cuenta
            // acá como evaluada por esta llamada (evitaba sobreconteo del retorno al caller).
            if (await InsertResultadoAsync(conn, clientId, novedadId, estado, porQue, ct))
            {
                evaluadas++;
                if (estado == NovedadClienteEstados.Pendiente) candidatasNuevas++;
            }
        }
        return (evaluadas, candidatasNuevas);
    }

    public async Task<IReadOnlyList<(NovedadRow Novedad, NovedadClienteRow Estado)>> ListAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.id, n.feed_guid, n.titulo, n.titulo_es, n.descripcion, n.descripcion_es, n.link,
                   n.estado_feed, n.categoria_bit, n.categorias_feed, n.published_at, n.is_active,
                   c.id, c.novedad_id, c.client_id, c.estado, c.por_que, c.decidido_por, c.decidido_at
            FROM dbo.boletin_novedad_cliente c
            INNER JOIN dbo.boletin_novedad n ON n.id = c.novedad_id
            WHERE c.client_id = @cid AND c.estado IN ('aprobada', 'pendiente')
            ORDER BY n.published_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<(NovedadRow, NovedadClienteRow)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var novedad = MapNovedad(r);
            var estado = new NovedadClienteRow(
                r.GetInt32(12), r.GetInt32(13), r.GetInt32(14), r.GetString(15),
                r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetString(17),
                r.IsDBNull(18) ? null : DateTime.SpecifyKind(r.GetDateTime(18), DateTimeKind.Utc));
            list.Add((novedad, estado));
        }
        return list;
    }

    public async Task<int?> OwnerClientIdAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.boletin_novedad_cliente WHERE id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", id));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt32(v);
    }

    public async Task<bool> DecidirAsync(int id, int clientId, string estado, string? porQue, bool setPorQue, string actor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.boletin_novedad_cliente
            SET estado = @estado,
                por_que = CASE WHEN @setPorQue = 1 THEN @porQue ELSE por_que END,
                decidido_por = @actor, decidido_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @cid AND estado <> 'no_aplica'
            """;
        cmd.Parameters.Add(new SqlParameter("@estado", estado));
        cmd.Parameters.Add(new SqlParameter("@porQue", Db(porQue)));
        cmd.Parameters.Add(new SqlParameter("@setPorQue", setPorQue));
        cmd.Parameters.Add(new SqlParameter("@actor", Db(actor)));
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // -------------------- inventario --------------------

    /// <summary>Suma de <see cref="BoletinQueries.TiposDeRecurso"/> por credencial (subs
    /// administradas del cliente). Cero credenciales administradas es un problema de CONFIGURACIÓN
    /// (el cliente no tiene nada administrado, no es un fallo transitorio) — se señaliza igual que
    /// <see cref="BoletinService.RunSyncAsync"/> con <see cref="BoletinNoManagedSubscriptionsException"/>,
    /// que el controller mapea a 400. Distinto de "TODAS las credenciales existentes fallaron al
    /// consultarse" (sí transitorio: Azure caído, permisos revocados) — ese caso sigue siendo
    /// <see cref="InvalidOperationException"/> → 503, porque sin inventario real la IA marcaría todo
    /// como no_aplica de forma prematura y PERMANENTE (no_aplica es terminal, nunca se re-evalúa).
    /// Fallo de UNA credencial = warning + se sigue con inventario parcial (la IA es conservadora
    /// ante info incompleta); solo si TODAS fallan se lanza la excepción transitoria.</summary>
    private async Task<IReadOnlyList<TipoRecurso>> BuildInventarioAsync(
        IReadOnlyDictionary<int, List<string>> groups, int clientId, CancellationToken ct)
    {
        if (groups.Count == 0)
            throw new BoletinNoManagedSubscriptionsException();

        var acumulado = new Dictionary<string, int>(StringComparer.Ordinal);
        var fallidas = 0;
        foreach (var (credentialId, subIds) in groups)
        {
            try
            {
                var cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct);
                var nodes = await rg.RunQueryAsync(cred, subIds, BoletinQueries.TiposDeRecurso, ct);
                foreach (var n in nodes)
                {
                    var tipo = BoletinEvaluatorParsers.FromTipoRow(new RgRow(n));
                    if (tipo is null) continue;
                    acumulado[tipo.Type] = acumulado.GetValueOrDefault(tipo.Type) + tipo.Cantidad;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "evaluacion boletin cliente {Cid}: inventario fallo credencial {Cred}", clientId, credentialId);
                fallidas++;
            }
        }
        if (fallidas == groups.Count)
            throw new InvalidOperationException("No se pudo leer el inventario del cliente.");

        return acumulado.Select(kv => new TipoRecurso(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Predicado canónico de suscripciones administradas (copia local — mismo patrón que
    /// BoletinService/Optimization/WAF/Inventory, cada servicio mantiene su propia copia privada).</summary>
    private static async Task<IReadOnlyDictionary<int, List<string>>> ManagedSubscriptionsAsync(
        SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.credential_id, s.subscription_id
            FROM dbo.client_azure_subscriptions s
            INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
            WHERE s.client_id = @cid AND s.is_active = 1
              AND COALESCE(s.is_managed, 1) = 1 AND c.is_active = 1
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var groups = new Dictionary<int, List<string>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var credId = r.GetInt32(0);
            if (!groups.TryGetValue(credId, out var list)) groups[credId] = list = [];
            list.Add(r.GetString(1));
        }
        return groups;
    }

    // -------------------- SQL --------------------

    private static async Task<List<NovedadRow>> LoadCandidatasAsync(SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.id, n.feed_guid, n.titulo, n.titulo_es, n.descripcion, n.descripcion_es, n.link,
                   n.estado_feed, n.categoria_bit, n.categorias_feed, n.published_at, n.is_active
            FROM dbo.boletin_novedad n
            WHERE n.is_active = 1
              AND NOT EXISTS (
                SELECT 1 FROM dbo.boletin_novedad_cliente c
                WHERE c.novedad_id = n.id AND c.client_id = @cid)
            ORDER BY n.published_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<NovedadRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(MapNovedad(r));
        return list;
    }

    /// <summary>Devuelve false cuando el INSERT pierde la carrera de doble evaluación simultánea (ver
    /// catch de abajo) — el caller usa el resultado para no sobrecontar `evaluadas`.</summary>
    private static async Task<bool> InsertResultadoAsync(
        SqlConnection conn, int clientId, int novedadId, string estado, string? porQue, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.boletin_novedad_cliente (novedad_id, client_id, estado, por_que)
            VALUES (@novedadId, @cid, @estado, @porQue)
            """;
        cmd.Parameters.Add(new SqlParameter("@novedadId", novedadId));
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@estado", estado));
        cmd.Parameters.Add(new SqlParameter("@porQue", Db(porQue)));
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqlException ex) when (ex.Number == 2627)
        {
            // Carrera de doble evaluación simultánea (mismo patrón que BoletinNovedadStore.IngestAsync):
            // el NOT EXISTS de LoadCandidatasAsync no es atómico y el UNIQUE(novedad_id, client_id)
            // atrapa al segundo INSERT. Duplicado legítimo, no un error.
            return false;
        }
    }

    private static NovedadRow MapNovedad(SqlDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetString(8), r.GetString(9), DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc), r.GetBoolean(11));

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.boletin_novedad_cliente','U') IS NULL
            CREATE TABLE dbo.boletin_novedad_cliente (
              id INT IDENTITY(1,1) PRIMARY KEY,
              novedad_id INT NOT NULL,
              client_id INT NOT NULL,
              estado NVARCHAR(16) NOT NULL DEFAULT 'pendiente',
              por_que NVARCHAR(MAX) NULL,
              decidido_por NVARCHAR(256) NULL,
              decidido_at DATETIME2 NULL,
              created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
              CONSTRAINT UX_boletin_novedad_cliente UNIQUE (novedad_id, client_id))
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
