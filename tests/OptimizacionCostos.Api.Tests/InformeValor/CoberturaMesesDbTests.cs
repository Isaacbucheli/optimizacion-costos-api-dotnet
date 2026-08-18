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

        // Los sobrantes de una corrida anterior se limpian antes de empezar: una corrida que se cae
        // antes del finally deja su cliente de prueba vivo.
        await BorrarClientesDePruebaAsync(factory, clients);

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
            await BorrarClientesDePruebaAsync(factory, clients);
        }
    }

    /// <summary>Borra los clientes que crea este test. Por nombre y no por id, así también barre lo
    /// que haya dejado una corrida que se cayó antes del finally.
    ///
    /// <para>Pasa por la cascada de la app en vez de enumerar tablas a mano: es la que sabe qué hay
    /// que borrar, y una lista escrita acá se desactualiza en silencio en cuanto el módulo suma una
    /// tabla. Estuvo hecha a mano un tiempo porque la cascada fallaba por
    /// <c>FK_waf_canonical_consolidates</c> y se llevaba puesta la limpieza de este test; eso quedó
    /// arreglado el 2026-08-18 (ver <c>WafCanonicalPurgeDbTests</c>).</para></summary>
    private static async Task BorrarClientesDePruebaAsync(ISqlConnectionFactory factory, SqlClientStore clients)
    {
        var ids = new List<int>();
        await using (var conn = await factory.OpenAsync(CancellationToken.None))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT client_id FROM dbo.clients WHERE client_name LIKE @patron";
            cmd.Parameters.Add(new SqlParameter("@patron", "E2E cobertura %"));
            await using var r = await cmd.ExecuteReaderAsync(CancellationToken.None);
            while (await r.ReadAsync(CancellationToken.None)) ids.Add(r.GetInt32(0));
        }
        foreach (var id in ids) await clients.DeleteClientCascadeAsync(id);
    }
}
