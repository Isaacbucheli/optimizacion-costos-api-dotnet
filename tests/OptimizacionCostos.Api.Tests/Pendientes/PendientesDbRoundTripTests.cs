using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Pendientes;

namespace OptimizacionCostos.Api.Tests.Pendientes;

/// <summary>
/// Round-trip REAL contra la BD del tablero (Seguimiento CDC). Gateado: solo corre con
/// BIT_INTEGRATION_PENDIENTES=1 y las SQL_*2 en el entorno; si no, es no-op.
///
/// Existe por un bug concreto: el token de concurrencia (`Pendiente.Actualizado`, datetime2(7)) se
/// enviaba como `datetime` (~3 ms de precisión) porque `new SqlParameter(nombre, valor)` infiere el
/// tipo del valor. El WHERE no calzaba nunca y TODA edición respondía 409. Con store fake no se ve:
/// hace falta el tipo real de la columna.
///
/// ⚠️ Escribe en la base del tablero: crea un pendiente de prueba y lo borra al final. La gate está
/// apagada por defecto justamente porque esa base es la de producción del equipo.
/// </summary>
public class PendientesDbRoundTripTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("BIT_INTEGRATION_PENDIENTES") == "1";

    private static IPendientesStore NewStore()
    {
        var config = AppConfig.FromConfiguration(new ConfigurationBuilder().Build());
        return new SqlPendientesStore(
            new SeguimientoSqlConnectionFactory(config, NullLogger<SeguimientoSqlConnectionFactory>.Instance));
    }

    [Fact]
    public async Task RoundTrip_de_escrituras_contra_la_BD_del_tablero()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var store = NewStore();
        const string area = PendientesArea.Cdc;

        // Un cliente existente del área: el store valida la integridad en código (no hay FK).
        var payload = await store.GetAreaAsync(area);
        Assert.NotEmpty(payload.Clientes);
        var clienteNum = payload.Clientes[0].Num;

        string? id = null;
        try
        {
            // 1. Alta
            id = await store.CreateItemAsync(area, new PendienteWrite
            {
                ClienteNum = clienteNum,
                Descripcion = "Round-trip de integración (borrar si queda)",
                Tipo = "PENDIENTE",
                Prioridad = "BAJA",
                Estado = "ABIERTO",
            });
            var creado = await store.GetItemAsync(area, id);
            Assert.NotNull(creado);
            Assert.Equal("ABIERTO", creado!.Estado);
            Assert.Empty(creado.Historial);

            // 2. Notas: Orden = MAX+1 arrancando en 0
            var hist1 = await store.AddNotaAsync(area, id, new NotaWrite { Nota = "primera" }, "Integración");
            var hist2 = await store.AddNotaAsync(area, id, new NotaWrite { Nota = "segunda" }, null);
            Assert.NotNull(hist1);
            Assert.NotNull(hist2);

            var conNotas = await store.GetItemAsync(area, id);
            Assert.Equal([0, 1], conNotas!.Historial.Select(n => n.Orden).ToArray());
            Assert.Equal("Integración", conNotas.Historial[0].Autor);
            Assert.Null(conNotas.Historial[1].Autor);

            // 3. Concurrencia optimista: el token que traía el alta ya venció (la nota movió Actualizado).
            var stale = await store.UpdateItemAsync(area, id, new PendienteWrite
            {
                ClienteNum = clienteNum, Descripcion = "no debe pasar", Tipo = "PENDIENTE",
                Prioridad = "BAJA", Estado = "ABIERTO", Actualizado = creado.Actualizado,
            });
            Assert.Equal(WriteOutcome.Conflict, stale);

            // 4. Con el token vigente sí actualiza (esto es lo que estaba roto).
            var ok = await store.UpdateItemAsync(area, id, new PendienteWrite
            {
                ClienteNum = clienteNum, Descripcion = "editado por el round-trip", Tipo = "PENDIENTE",
                Prioridad = "ALTA", Estado = "EN_PROGRESO", Actualizado = conNotas.Actualizado,
            });
            Assert.Equal(WriteOutcome.Ok, ok);

            var editado = await store.GetItemAsync(area, id);
            Assert.Equal("EN_PROGRESO", editado!.Estado);
            Assert.Equal("ALTA", editado.Prioridad);
            Assert.Equal(2, editado.Historial.Count); // la edición no toca el historial

            // 5. Borrar una nota no borra la otra, y el HistId ajeno no aplica.
            Assert.False(await store.DeleteNotaAsync(area, id, -1));
            Assert.True(await store.DeleteNotaAsync(area, id, hist2!.Value));
            Assert.Single((await store.GetItemAsync(area, id))!.Historial);

            // 6. Borrado: se va con sus notas.
            Assert.True(await store.DeleteItemAsync(area, id));
            Assert.Null(await store.GetItemAsync(area, id));
            id = null;
        }
        finally
        {
            // Si algo falló a mitad, no dejar basura en el tablero del equipo.
            if (id is not null) await store.DeleteItemAsync(area, id);
        }
    }
}
