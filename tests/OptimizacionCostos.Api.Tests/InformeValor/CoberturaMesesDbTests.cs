using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Ida y vuelta REAL contra Azure SQL de <see cref="SqlInformeValorStore.GetCoberturaMesesAsync"/>,
/// el MIN/MAX que le dice al front qué período proponer.
///
/// <para><b>Por qué hace falta la base de verdad.</b> Son tres consultas en un solo lote leídas con
/// <c>NextResultAsync</c>, y el mes viaja como <c>año * 12 + mes</c> sobre columnas SMALLINT y
/// TINYINT. Un lote mal separado, un <c>NextResult</c> de menos o un tipo que no entra en
/// <c>GetInt32</c> devuelven datos de otra tabla o revientan, y ningún falso en memoria lo cata:
/// el falso devuelve lo que se le guardó.</para>
///
/// <para>Mismo gate que el resto de los tests de base del módulo: no-op sin
/// <c>BIT_INTEGRATION_DB=1</c>, así que la suite normal nunca toca Azure SQL.</para>
/// </summary>
public class CoberturaMesesDbTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BIT_INTEGRATION_DB") == "1";

    private static ISqlConnectionFactory NewFactory() =>
        new SqlConnectionFactory(
            AppConfig.FromConfiguration(new ConfigurationBuilder().Build()),
            NullLogger<SqlConnectionFactory>.Instance);

    private static FacturacionRow Fila(string hash, short anio, byte mes) =>
        new(Hash: hash, Tenant: null, SubscriptionName: "sub", SubscriptionId: "sub-1",
            ResourceGroup: "rg", ResourceName: "rec", CostCenter: null, Category: "Cómputo",
            Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
            Pvp: 10m, Year: anio, Month: mes);

    [Fact]
    public async Task La_cobertura_sale_del_primer_y_ultimo_mes_cargado()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var factory = NewFactory();
        var clients = new SqlClientStore(factory);
        var store = new SqlInformeValorStore(factory);

        // Los sobrantes de una corrida anterior se limpian antes de empezar: el borrado en cascada
        // de clientes está roto hoy en esta base (FK_waf_canonical_consolidates, ver el finally), así
        // que una corrida que se cae a la mitad deja su cliente de prueba vivo.
        await BorrarClientesDePruebaAsync(factory);

        var tag = $"e2e-cobertura-{Guid.NewGuid():N}";
        var clientId = await clients.CreateAsync($"E2E cobertura {tag}", null, null, null, null);
        try
        {
            // Sin insumos, los tres ejes son null: la ausencia tiene que llegar como ausencia.
            var vacia = await store.GetCoberturaMesesAsync(clientId, CancellationToken.None);
            Assert.Null(vacia.Facturacion);
            Assert.Null(vacia.Evolucion);
            Assert.Null(vacia.Casos);

            // Diciembre primero y en desorden: el mes más nuevo es 2025-12, no 2026-03, y el más
            // viejo es 2025-03. Ordenar por columna suelta daría 2025-01, un mes que no existe acá.
            var filas = new List<FacturacionRow>
            {
                Fila("h-dic", 2025, 12), Fila("h-mar", 2025, 3), Fila("h-jul", 2025, 7),
            };
            await store.ReplaceFacturacionAsync(
                clientId, "cobertura.xlsx", "e2e",
                new ParseResult<FacturacionRow>(filas, filas.Count, 0, 0, 0, []),
                CancellationToken.None);

            var cobertura = await store.GetCoberturaMesesAsync(clientId, CancellationToken.None);

            Assert.NotNull(cobertura.Facturacion);
            Assert.Equal("2025-03", cobertura.Facturacion!.Desde);
            Assert.Equal("2025-12", cobertura.Facturacion.Hasta);
            // Los otros dos insumos siguen sin filas, y eso no cambia por haber cargado facturación.
            Assert.Null(cobertura.Evolucion);
            Assert.Null(cobertura.Casos);
        }
        finally
        {
            // Limpieza propia y no DeleteClientCascadeAsync: esa cascada borra las canónicas de WAF
            // huérfanas de TODA la base, y hoy falla contra -valida porque alguna se apunta a otra
            // por consolidates_to_id (FK_waf_canonical_consolidates). Es un defecto vivo del borrado
            // de clientes, ajeno a este test; encadenarse a él dejaría basura acá cada vez.
            await BorrarClientesDePruebaAsync(factory);
        }
    }

    /// <summary>Borra los clientes que crea este test y sus filas de insumos. Por nombre, no por id:
    /// también barre lo que haya dejado una corrida que se cayó antes del finally.</summary>
    private static async Task BorrarClientesDePruebaAsync(ISqlConnectionFactory factory)
    {
        await using var conn = await factory.OpenAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DECLARE @ids TABLE (id INT);
            INSERT INTO @ids SELECT client_id FROM dbo.clients WHERE client_name LIKE @patron;
            DELETE FROM dbo.informe_valor_facturacion WHERE client_id IN (SELECT id FROM @ids);
            DELETE FROM dbo.informe_valor_ingesta WHERE client_id IN (SELECT id FROM @ids);
            DELETE FROM dbo.clients WHERE client_id IN (SELECT id FROM @ids);
            """;
        cmd.Parameters.Add(new SqlParameter("@patron", "E2E cobertura %"));
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
