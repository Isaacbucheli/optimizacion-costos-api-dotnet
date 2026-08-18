using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Ida y vuelta REAL contra Azure SQL de la bitácora de entregas (F4 de la entrega 3): archivar con
/// <see cref="SqlInformeValorStore.RegistrarEntregaAsync"/> y releer con
/// <see cref="SqlInformeValorStore.GetEntregaAsync"/>/<c>GetEntregasAsync</c>.
///
/// <para><b>Por qué hace falta pegarle a la base de verdad.</b> Los dos lectores de la tabla van por
/// índice posicional sobre una lista de columnas compartida. Una columna agregada en el medio, o un
/// índice mal contado, lee la columna de al lado sin fallar: si es del mismo tipo, la fila vuelve con
/// datos de otra columna y ninguna prueba en memoria lo cata, porque el falso del store devuelve lo
/// que se le guardó. Esto es lo único que lo detecta.</para>
///
/// <para>Mismo gate que <c>InformeValorRbacRoleClassRoundTripDbTests</c>: no-op sin
/// <c>BIT_INTEGRATION_DB=1</c>, así que la suite normal nunca toca Azure SQL.</para>
/// </summary>
public class InformeValorEntregaRoundTripDbTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BIT_INTEGRATION_DB") == "1";

    private static ISqlConnectionFactory NewFactory() =>
        new SqlConnectionFactory(
            AppConfig.FromConfiguration(new ConfigurationBuilder().Build()),
            NullLogger<SqlConnectionFactory>.Instance);

    [Fact]
    public async Task La_entrega_archivada_vuelve_completa_de_la_base()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var factory = NewFactory();
        var clients = new SqlClientStore(factory);
        var store = new SqlInformeValorStore(factory);

        var tag = $"e2e-entrega-{Guid.NewGuid():N}";
        var clientId = await clients.CreateAsync($"E2E entrega {tag}", null, null, null, null);
        try
        {
            var foto = new FotoReservas(
                Medido: true, Motivo: "Las reservas activas se leyeron completas desde Azure.",
                Errores: [], AlertDays: 30,
                CapturadaEn: new DateTime(2026, 3, 1, 12, 30, 0, DateTimeKind.Utc),
                Reservas:
                [
                    new ReservaActiva(
                        ReservationId: $"resv-{tag}", Nombre: "Reserva de prueba", Producto: "Standard_D2s_v5",
                        Region: "eastus", Cantidad: 2, Term: "P1Y", TermLabel: "1 ano",
                        ExpiresOn: "2027-06-01", DaysRemaining: 300, Expiring: false,
                        UtilizationLast: "80%", Utilization7d: "75%",
                        Consumidores: [], UnidadesEstimadas: 2, ConsumidoresNoLeidos: false),
                ]);

            var nueva = new EntregaNueva(
                ClientId: clientId,
                PeriodStart: new DateOnly(2026, 1, 1),
                PeriodEnd: new DateOnly(2026, 2, 28),
                Corte: new DateOnly(2026, 3, 1),
                MesesParcialesForzados: ["2026-02"],
                Variante: VarianteInforme.Cliente,
                BloquesPublicados: [BloqueEconomico.GastoTotal, BloqueEconomico.CentroCosto],
                RbacOrigen: InsumosBd.OrigenBase,
                RbacCorridaFecha: new DateTime(2026, 2, 25, 3, 0, 0, DateTimeKind.Utc),
                SeguridadGestionadaExternamente: true,
                FacturacionIngestaId: 4001,
                CasosIngestaId: 4002,
                RbacIngestaId: null,
                EvolucionIngestaId: 4003,
                FotoReservas: foto,
                PlantillaVersion: "abcdef0123456789",
                BlobContainer: "contenedor-de-prueba",
                BlobName: $"informe-valor/client-{clientId}/{tag}.html",
                BlobSizeBytes: 12345,
                FileName: "Cliente-Valor-Servicio-Administrado-BIT-2026-01-a-2026-02-cliente.html",
                SummaryJson: "{\"periodo\":\"2026-01 a 2026-02\"}",
                GeneratedBy: "e2e@bit.ec");

            var entregaId = await store.RegistrarEntregaAsync(nueva, CancellationToken.None);

            var vuelta = await store.GetEntregaAsync(clientId, entregaId, CancellationToken.None);
            Assert.NotNull(vuelta);

            // El bloque que la tabla paginada muestra.
            Assert.Equal(nueva.PeriodStart, vuelta!.Resumen.PeriodStart);
            Assert.Equal(nueva.PeriodEnd, vuelta.Resumen.PeriodEnd);
            Assert.Equal(nueva.Corte, vuelta.Resumen.Corte);
            Assert.Equal("cliente", vuelta.Resumen.Variante);
            Assert.Equal(["gastoTotal", "centroCosto"], vuelta.Resumen.BloquesPublicados);
            Assert.Equal(InsumosBd.OrigenBase, vuelta.Resumen.RbacOrigen);
            Assert.Equal(nueva.FileName, vuelta.Resumen.FileName);
            Assert.Equal(12345, vuelta.Resumen.BlobSizeBytes);
            Assert.Equal("e2e@bit.ec", vuelta.Resumen.GeneratedBy);

            // Y el bloque de trazabilidad, que es el que decide si reemitir da lo mismo.
            Assert.Equal("contenedor-de-prueba", vuelta.BlobContainer);
            Assert.Equal(nueva.BlobName, vuelta.BlobName);
            Assert.Equal(["2026-02"], vuelta.MesesParcialesForzados);
            Assert.Equal(nueva.RbacCorridaFecha, vuelta.RbacCorridaFecha);
            Assert.True(vuelta.SeguridadGestionadaExternamente);
            Assert.Equal(4001, vuelta.FacturacionIngestaId);
            Assert.Equal(4002, vuelta.CasosIngestaId);
            Assert.Null(vuelta.RbacIngestaId);
            Assert.Equal(4003, vuelta.EvolucionIngestaId);
            Assert.Equal("abcdef0123456789", vuelta.PlantillaVersion);
            Assert.Equal(nueva.SummaryJson, vuelta.SummaryJson);

            Assert.NotNull(vuelta.FotoReservas);
            Assert.True(vuelta.FotoReservas!.Medido);
            Assert.Equal(30, vuelta.FotoReservas.AlertDays);
            Assert.Equal(foto.CapturadaEn, vuelta.FotoReservas.CapturadaEn);
            Assert.Equal($"resv-{tag}", Assert.Single(vuelta.FotoReservas.Reservas).ReservationId);

            // La lista usa el mismo SELECT posicional: si un índice está corrido, acá también.
            var listado = Assert.Single(await store.GetEntregasAsync(clientId, CancellationToken.None));
            Assert.Equal(entregaId, listado.EntregaId);
            Assert.Equal("cliente", listado.Variante);
            Assert.Equal(nueva.FileName, listado.FileName);

            // Otro cliente no ve esta entrega: el filtro va dentro del WHERE.
            Assert.Null(await store.GetEntregaAsync(clientId + 1_000_000, entregaId, CancellationToken.None));
        }
        finally
        {
            await clients.DeleteClientCascadeAsync(clientId);
        }
    }
}
