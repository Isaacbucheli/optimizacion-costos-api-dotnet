using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

/// <summary>
/// Pruebas puras (sin BD/Azure) de la lógica de mapeo de <see cref="BoletinNovedadClienteStore"/>:
/// el punto más delicado de Task 4 (nit duro del review de T3) es que
/// <c>IBoletinNovedadEvaluator.EvaluarAsync</c> devuelve la lista en ORDEN LIBRE, así que el
/// re-empareje con las novedades candidatas debe hacerse SIEMPRE por <c>FeedGuid</c>, nunca por
/// índice/posición. El flujo SQL/Azure real (inventario ARG, upsert, esquema) lo cubre el E2E
/// manual — igual que BoletinNovedadStoreTests con la whitelist del PUT global.
/// </summary>
public class BoletinNovedadClienteStoreTests
{
    private static NovedadRow Row(int id, string guid) => new(
        id, guid, "Titulo " + guid, null, "Descripcion", null, "https://azure.microsoft.com/updates/" + guid,
        "launched", "resiliencia_plataforma", "[]", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), true);

    [Fact]
    public void MapeaPorGuidAunqueElEvaluadorDevuelvaOrdenDistinto()
    {
        var candidatas = new List<NovedadRow> { Row(1, "g1"), Row(2, "g2"), Row(3, "g3") };
        // Orden deliberadamente invertido/mezclado respecto a `candidatas`: si el código mapeara por
        // índice (zip), g3 terminaría asociado a NovedadId=1 (el bug que el review de T3 documentó).
        var evaluaciones = new List<EvaluacionNovedad>
        {
            new("g3", true, "usas 5 App Service"),
            new("g1", false, null),
            new("g2", true, "usas 2 Azure SQL Database"),
        };

        var result = BoletinNovedadClientePlan.MapEvaluaciones(candidatas, evaluaciones);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.NovedadId == 3 && r.Estado == NovedadClienteEstados.Pendiente && r.PorQue == "usas 5 App Service");
        Assert.Contains(result, r => r.NovedadId == 1 && r.Estado == NovedadClienteEstados.NoAplica && r.PorQue is null);
        Assert.Contains(result, r => r.NovedadId == 2 && r.Estado == NovedadClienteEstados.Pendiente && r.PorQue == "usas 2 Azure SQL Database");
    }

    [Fact]
    public void AplicaFalseFuerzaPorQueNuloAunqueLaIaMandeTexto()
    {
        // Defensa en profundidad (mismo principio que BoletinEvaluatorParsers.ParseRespuesta): si
        // por algún motivo llega un EvaluacionNovedad con Aplica=false pero PorQue con texto (bypass
        // del parser, o un caller directo del evaluador), el mapeo igual lo descarta.
        var candidatas = new List<NovedadRow> { Row(1, "g1") };
        var evaluaciones = new List<EvaluacionNovedad> { new("g1", false, "texto que no debería sobrevivir") };

        var result = BoletinNovedadClientePlan.MapEvaluaciones(candidatas, evaluaciones);

        var row = Assert.Single(result);
        Assert.Equal(NovedadClienteEstados.NoAplica, row.Estado);
        Assert.Null(row.PorQue);
    }

    [Fact]
    public void DescartaEvaluacionesConGuidQueNoPerteneceAlLote()
    {
        // No debería ocurrir (el evaluador ya valida esto), pero un guid ajeno se descarta en vez de
        // reventar o insertarse con un NovedadId inventado.
        var candidatas = new List<NovedadRow> { Row(1, "g1") };
        var evaluaciones = new List<EvaluacionNovedad> { new("g1", true, "ok"), new("g-ajeno", true, "no debería aparecer") };

        var result = BoletinNovedadClientePlan.MapEvaluaciones(candidatas, evaluaciones);

        var row = Assert.Single(result);
        Assert.Equal(1, row.NovedadId);
    }

    [Fact]
    public void ListaVaciaDeEvaluacionesNoProduceFilas()
    {
        var candidatas = new List<NovedadRow> { Row(1, "g1") };
        var result = BoletinNovedadClientePlan.MapEvaluaciones(candidatas, []);
        Assert.Empty(result);
    }

    [Fact]
    public void LosEstadosDecidiblesSonExactamenteAprobadaRechazadaPendiente()
    {
        // no_aplica es terminal: solo lo asigna la evaluación IA, nunca un PUT del consultor.
        Assert.Equal(3, NovedadClienteEstados.DecidiblesValidos.Length);
        Assert.Contains(NovedadClienteEstados.Aprobada, NovedadClienteEstados.DecidiblesValidos);
        Assert.Contains(NovedadClienteEstados.Rechazada, NovedadClienteEstados.DecidiblesValidos);
        Assert.Contains(NovedadClienteEstados.Pendiente, NovedadClienteEstados.DecidiblesValidos);
        Assert.DoesNotContain(NovedadClienteEstados.NoAplica, NovedadClienteEstados.DecidiblesValidos);
    }

    // ---- Task 2: persistencia de recursos citados (BoletinNovedadEvaluator.Recursos, Task 1) ----

    [Fact]
    public void SerializaRecursosAJsonCuandoAplicaYHayRecursos()
    {
        var candidatas = new List<NovedadRow> { Row(1, "g1") };
        var evaluaciones = new List<EvaluacionNovedad>
        {
            new("g1", true, "usas estos recursos",
                new List<TipoRecurso> { new("Microsoft.Compute/virtualMachines", 3), new("Microsoft.Sql/servers", 1) }),
        };

        var result = BoletinNovedadClientePlan.MapEvaluaciones(candidatas, evaluaciones);

        var row = Assert.Single(result);
        Assert.Equal(
            "[{\"type\":\"Microsoft.Compute/virtualMachines\",\"cantidad\":3},{\"type\":\"Microsoft.Sql/servers\",\"cantidad\":1}]",
            row.RecursosJson);
    }

    [Fact]
    public void RecursosNuloProduceRecursosJsonNulo()
    {
        var candidatas = new List<NovedadRow> { Row(1, "g1") };
        var evaluaciones = new List<EvaluacionNovedad> { new("g1", true, "ok", null) };

        var result = BoletinNovedadClientePlan.MapEvaluaciones(candidatas, evaluaciones);

        Assert.Null(Assert.Single(result).RecursosJson);
    }

    [Fact]
    public void RecursosVacioProduceRecursosJsonNulo()
    {
        var candidatas = new List<NovedadRow> { Row(1, "g1") };
        var evaluaciones = new List<EvaluacionNovedad> { new("g1", true, "ok", new List<TipoRecurso>()) };

        var result = BoletinNovedadClientePlan.MapEvaluaciones(candidatas, evaluaciones);

        Assert.Null(Assert.Single(result).RecursosJson);
    }

    [Fact]
    public void NoAplicaSiempreProduceRecursosJsonNuloAunqueLleguenRecursos()
    {
        // Defensa en profundidad (mismo principio que AplicaFalseFuerzaPorQueNuloAunqueLaIaMandeTexto):
        // aunque llegue Recursos con datos junto a Aplica=false, RecursosJson se queda en null.
        var candidatas = new List<NovedadRow> { Row(1, "g1") };
        var evaluaciones = new List<EvaluacionNovedad>
        {
            new("g1", false, null, new List<TipoRecurso> { new("Microsoft.Compute/virtualMachines", 3) }),
        };

        var result = BoletinNovedadClientePlan.MapEvaluaciones(candidatas, evaluaciones);

        Assert.Null(Assert.Single(result).RecursosJson);
    }
}
