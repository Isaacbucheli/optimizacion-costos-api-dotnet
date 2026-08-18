using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Round-trip REAL contra Azure SQL de advisor_name_en (título original de Azure Advisor).
/// Solo corre con BIT_INTEGRATION_DB=1 (mismo gate que DbRoundTripTests); si no, es no-op.
/// Cubre: solo la ingesta 'advisor' siembra, first-write-wins ante consolidación, la curación IA
/// no lo pisa, y el backfill quedó marcado (corre una sola vez).
/// </summary>
public class WafAdvisorNameEnTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BIT_INTEGRATION_DB") == "1";

    private static ISqlConnectionFactory NewFactory() =>
        new SqlConnectionFactory(
            AppConfig.FromConfiguration(new ConfigurationBuilder().Build()),
            NullLogger<SqlConnectionFactory>.Instance);

    private static AdvisorRow Row(string name, string resource) => new(
        AdvisorName: name, AdvisorCategory: "OperationalExcellence", BusinessImpact: "Medium",
        ResourceName: resource, ResourceType: "microsoft.compute/virtualmachines",
        ResourceGroup: "rg-e2e", SubscriptionId: "00000000-0000-0000-0000-0000000000ff",
        SubscriptionName: "e2e-sub", AzureResourceId: $"/subscriptions/x/resourceGroups/rg-e2e/providers/microsoft.compute/virtualmachines/{resource}",
        AdditionalInfo: null);

    private static async Task<string?> ReadNameEnAsync(ISqlConnectionFactory factory, int canonicalId)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT advisor_name_en FROM dbo.waf_recommendation_canonical WHERE canonical_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", canonicalId));
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    private static async Task<int?> FindCanonicalAsync(ISqlConnectionFactory factory, string advisorName)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT canonical_id FROM dbo.waf_recommendation_canonical WHERE advisor_name = @name";
        cmd.Parameters.Add(new SqlParameter("@name", advisorName));
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    /// <summary>Limpia por tag las canónicas y alias que siembra este test.
    ///
    /// <para>La cascada del cliente sí borra las canónicas que ese cliente usaba y quedaron sin nada
    /// que las referencie (desde el 2026-08-18 el barrido está acotado al cliente, ver
    /// <c>WafCanonicalPurgeDbTests</c>), así que esto es la red para lo que ella deja en pie a
    /// propósito: una canónica retenida porque otra la apunta, o lo que quede de una corrida que se
    /// cayó antes del finally.</para></summary>
    private static async Task DeleteCanonicalsAsync(ISqlConnectionFactory factory, string tag)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM dbo.waf_canonical_alias WHERE advisor_name LIKE @p;
            DELETE FROM dbo.waf_canonical_alias
            WHERE canonical_id IN (SELECT canonical_id FROM dbo.waf_recommendation_canonical WHERE advisor_name LIKE @p);
            DELETE FROM dbo.waf_recommendation_canonical WHERE advisor_name LIKE @p;
            """;
        cmd.Parameters.Add(new SqlParameter("@p", "%" + tag + "%"));
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Solo_la_ingesta_advisor_siembra_el_titulo_original()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var factory = NewFactory();
        var catalog = new SqlWafCatalogStore(factory);
        var ingestion = new SqlWafIngestionStore(factory, catalog, new SqlWafRecommendationStore(factory));
        var clients = new SqlClientStore(factory);

        var tag = $"e2e-name-en-{Guid.NewGuid():N}";
        var advisorTitle = $"Enable backup on your virtual machines {tag}";
        var excelTitle = $"Habilitar respaldos en las maquinas virtuales {tag}";

        var clientId = await clients.CreateAsync($"E2E advisor_name_en {tag}", null, null, null, null);
        try
        {
            // 1. Ingesta por API de Advisor → siembra el original.
            await ingestion.IngestAdvisorRowsAsync(
                clientId, "Azure Advisor API", [Row(advisorTitle, "vm-a")], "e2e",
                metrics: null, replaceSubscriptionIds: null, dedupResolver: null, source: "advisor");

            var advisorCanonicalId = await FindCanonicalAsync(factory, advisorTitle);
            Assert.NotNull(advisorCanonicalId);
            Assert.Equal(advisorTitle, await ReadNameEnAsync(factory, advisorCanonicalId!.Value));

            // 2. Ingesta desde Excel/CSV → NO siembra (su advisor_name es el título BIT en español).
            await ingestion.IngestAdvisorRowsAsync(
                clientId, "matriz.xlsx", [Row(excelTitle, "vm-b")], "e2e",
                metrics: null, replaceSubscriptionIds: null, dedupResolver: null, source: "excel");

            var excelCanonicalId = await FindCanonicalAsync(factory, excelTitle);
            Assert.NotNull(excelCanonicalId);
            Assert.Null(await ReadNameEnAsync(factory, excelCanonicalId!.Value));

            // 3. Consolidación: una fila NUEVA de Advisor cae en la canónica que nació del Excel.
            //    Es la única vía por la que esas canónicas consiguen su original.
            var lateTitle = $"Configure backups for virtual machines {tag}";
            await using (var conn = await factory.OpenAsync())
            {
                await WafSchema.EnsureWafSchemaAsync(conn);
                await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
                await catalog.GetOrCreateCanonicalAsync(
                    conn, tx, Row(lateTitle, "vm-c"),
                    dedupResolver: (_, _, _) => Task.FromResult<int?>(excelCanonicalId.Value),
                    source: "advisor", CancellationToken.None);
                await tx.CommitAsync();
            }
            Assert.Equal(lateTitle, await ReadNameEnAsync(factory, excelCanonicalId.Value));

            // 4. FIRST-WRITE-WINS: otra fila de Advisor sobre la misma canónica no cambia el texto
            //    (si no, el título bailaría entre syncs según el orden de proceso).
            await using (var conn = await factory.OpenAsync())
            {
                await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
                await catalog.GetOrCreateCanonicalAsync(
                    conn, tx, Row($"Yet another backup recommendation {tag}", "vm-d"),
                    dedupResolver: (_, _, _) => Task.FromResult<int?>(excelCanonicalId.Value),
                    source: "advisor", CancellationToken.None);
                await tx.CommitAsync();
            }
            Assert.Equal(lateTitle, await ReadNameEnAsync(factory, excelCanonicalId.Value));

            // 5. La curación IA reescribe el español pero no toca el original.
            await using (var conn = await factory.OpenAsync())
            {
                await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
                await catalog.ApplyAiSuggestionAsync(conn, tx, advisorCanonicalId.Value, new WafCurationResult(
                    Decision: "keep", PossibleAdditionalCost: false, CostReason: "", DuplicateGroupKey: "",
                    PillarNumber: 2, ReviewScopeEs: $"Habilitar respaldo en las VMs {tag}",
                    BenefitEs: "Continuidad", ClientActionEs: "Aprobar", BitActionEs: "Configurar",
                    ExclusionReason: "", Confidence: 0.9m, RawModelText: ""), "e2e", CancellationToken.None);
                await tx.CommitAsync();
            }
            Assert.Equal(advisorTitle, await ReadNameEnAsync(factory, advisorCanonicalId.Value));

            // 6. El endpoint/lectura del catálogo devuelve el campo mapeado.
            var canonical = await catalog.GetCanonicalAsync(advisorCanonicalId.Value);
            Assert.Equal(advisorTitle, canonical!.AdvisorNameEn);
        }
        finally
        {
            await clients.DeleteClientCascadeAsync(clientId);
            await DeleteCanonicalsAsync(factory, tag); // el tag va dentro de cada título de prueba
        }
    }

    [Fact]
    public async Task Backfill_corre_una_sola_vez_y_no_deja_originales_sin_poblar()
    {
        if (!Enabled) return;

        var factory = NewFactory();
        await using var conn = await factory.OpenAsync();
        await WafSchema.EnsureWafSchemaAsync(conn);

        // Marcador presente: la segunda pasada de EnsureWafSchemaAsync ya no reejecuta el backfill.
        await using (var marker = conn.CreateCommand())
        {
            marker.CommandText =
                "SELECT COUNT(*) FROM dbo.waf_feature_baseline WHERE feature = 'advisor_name_en_backfill'";
            Assert.Equal(1, Convert.ToInt32(await marker.ExecuteScalarAsync()));
        }

        // Toda canónica que pasó por sync Advisor y tiene título en inglés quedó poblada.
        await using (var pending = conn.CreateCommand())
        {
            pending.CommandText = """
                SELECT COUNT(*)
                FROM dbo.waf_recommendation_canonical c
                WHERE c.advisor_name_en IS NULL
                  AND EXISTS (SELECT 1 FROM dbo.waf_recommendation r
                              WHERE r.canonical_id = c.canonical_id AND r.source = 'advisor')
                  AND c.advisor_name COLLATE Latin1_General_BIN NOT LIKE N'%[áéíóúñÁÉÍÓÚÑ]%'
                  AND c.advisor_name NOT LIKE N'% de %' AND c.advisor_name NOT LIKE N'% para %'
                  AND c.advisor_name NOT LIKE N'% los %' AND c.advisor_name NOT LIKE N'% las %'
                  AND c.advisor_name NOT LIKE N'% en %' AND c.advisor_name NOT LIKE N'% con %'
                """;
            Assert.Equal(0, Convert.ToInt32(await pending.ExecuteScalarAsync()));
        }

        // Las de Excel/legacy siguen sin original: no se inventa nada.
        await using (var legacy = conn.CreateCommand())
        {
            legacy.CommandText = """
                SELECT COUNT(*)
                FROM dbo.waf_recommendation_canonical c
                WHERE c.advisor_name_en IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM dbo.waf_recommendation r
                                  WHERE r.canonical_id = c.canonical_id AND r.source = 'advisor')
                  AND NOT EXISTS (SELECT 1 FROM dbo.waf_canonical_alias a WHERE a.canonical_id = c.canonical_id)
                """;
            Assert.Equal(0, Convert.ToInt32(await legacy.ExecuteScalarAsync()));
        }
    }
}
