using System.Text.Json;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Round-trip de los cinco bloques con datos representativos (no vacíos): fija que las filas
/// posicionales serializan como arreglo JSON (no como objeto), que los <see langword="int"/>?
/// nulos (eje no medido, D9) viajan como <c>null</c> real, y que el diccionario anidado de
/// <see cref="PosturaModelo.CompromisoPorSuscripcion"/> sobrevive con una clave con espacios. No
/// prueba cálculo (eso es de las tareas 3 a 7): prueba que la FORMA declarada en la Tarea 2 se
/// serializa como se espera y se puede reconstruir de vuelta.
/// </summary>
public sealed class ModeloInformeValorSerializacionTests
{
    private static readonly OperacionModelo Operacion = new(
        Total: 10, Cumple: 8, NoCumple: 1, SinEvaluar: 1, PctCumplimiento: 88.88,
        DenominadorPctCumplimiento: 9, Cerrados: 9, MediaHoras: 4.2, MedianaHoras: 3.5, P90Horas: 8.1,
        MediaHorasDentroSla: 3.0, DuracionOriginalEnDias: false,
        Categorias: [new OperacionCategoria("Cómputo", 5, 1, 3.5)],
        SerieMensual: [["2026-01", 10, 1]],
        RachaMesesSinIncumplir: 2, RachaCasos: 4,
        Frentes: [new OperacionFrente("Solicitud de cambio", 5, false)],
        TotalFrentes: 3, FrentesReactivos: 1, FrentesProactivos: 2, CasosReactivos: 2, CasosSinSubcategoria: 0,
        PorHorario: [["Hábil", 8]],
        Desde: "2026-01-01", Hasta: "2026-01-31",
        FueraDeSla: [["RF-1", "2026-01-05", "Cómputo", "Incidente", 4m, 9.5m]],
        Detalle: [["RF-1", "2026-01-05", "Cómputo", "Incidente", 4m, 9.5m, "NO", "Hábil"]]);

    private static readonly SeguridadModelo Seguridad = new(
        Total: 20, Usuarios: 15, ServicePrincipals: 5, Identidades: 12, IdentidadesUsuarios: 10,
        IdentidadesServicePrincipals: 2,
        Suscripciones: [["Suscripción Producción", 12, 3]],
        Roles: [["Owner", 2, true]],
        RolesServicePrincipal: [["Contributor", 1, false]],
        Owner: 2, UserAccessAdministrator: 1, Contributor: 5, Privilegiados: 8,
        SinActividadSesion: null, // eje no medido (D9): tiene que viajar null, no cero
        UltimoLoginMedido: false,
        SinNombreResuelto: 0,
        CuentasDeshabilitadas: 3,
        EstadoCuentaMedido: true,
        SuscripcionTopServicePrincipal: ["Suscripción Producción", 12, 3],
        Hallazgos: [new SeguridadHallazgo("Crítica", "2 asignaciones Owner activas", "2 asignaciones", "Sustituir por Contributor.", "En remediación")],
        Criticos: 1);

    private static readonly PosturaModelo Postura = new(
        Total: 30, TiposDeRecomendacion: 8,
        Pilares: [new PosturaPilar("Seguridad", 12, 5, 4, 3)],
        Suscripciones: [new PosturaConteo("Suscripción Producción", 20)],
        TiposRecurso: [new PosturaConteo("virtualMachines", 10)],
        Top: [["Habilitar copia de seguridad", "Confiabilidad", "High", 6]],
        TopSuma: 6,
        Detalle: [["Habilitar copia de seguridad", "Confiabilidad", "High", "Suscripción Producción", 6]],
        NumRecursos: 18, RecomendacionesConRecurso: 27, Alto: 5, Medio: 10, Bajo: 15,
        AhorroBruto: 5000m, AhorroRealizable: 3200m, AhorroDescartado: 1800m, ConAhorroCuantificado: 4,
        LineasAhorro: [new PosturaLineaAhorro("Comprar reserva de 1 año", "Suscripción Producción", 2000m, "RI", true)],
        CompromisoPorSuscripcion: new Dictionary<string, PosturaCompromisoSuscripcion>
        {
            ["Suscripción Producción"] = new(Reserva: 2000m, SavingsPlan: 1500m),
        },
        Retiros: [new PosturaRetiro("API en desuso", "3/5/2026", 4, "Menos de tres meses de margen.", false, true)],
        RetirosVencidos: 0, RetirosProximosATresMeses: 1,
        RetirosMedido: true, RetirosMotivo: null,
        SeguridadGestionadaExternamente: false, SeguridadGestionadaNota: null);

    private static readonly RoadmapModelo Roadmap = new(
        Total: 5,
        Items: [new RoadmapItem("Seguridad", "Revisar asignaciones Owner", "2026-01-10", 1, "1", 4m, 50, 1, "En curso")],
        Ambitos: [new RoadmapAmbito("Seguridad", 5, 5, 50)],
        Cerrados: 1, EnCurso: 2, SinIniciar: 2, AvancePromedio: 42.5, HorasPendientes: 12m);

    private static readonly ConsumoModelo Consumo = new(
        // FilasEnRango y MesesParcialesInexistentes: campos agregados por la Tarea 3 despues de
        // esta fixture (D14 rotulado, spec 12.3.3); valores de ejemplo, no afectan estas pruebas.
        Filas: 14111, FilasEnRango: 500, Total: 250000m,
        SerieMensual: [["2026-01", 20000m, 0]],
        UltimoMesCompleto: "2026-01",
        MesesParciales: ["2026-02"], MesesParcialesDetectadosAuto: ["2026-02"],
        MesesParcialesInexistentes: [],
        Suscripciones: [["Suscripción Producción", 200000m]],
        NumRecursos: 120, NumIdentidades: 130, NumGruposRecursos: 15, NumCategorias: 9,
        PicoRecursosActivos: 125, MesDePicoActivos: "2026-01",
        Serie: [["2026-01", 120, 5, 2, 20000m, 300m, 0]],
        BajasDefinitivas: 8, CargaRetirada: 1200m,
        UnidadCargaRetirada: "USD, suma del último mes facturado de cada recurso dado de baja",
        PromediosPorAnio: [["2026", 1, 20000m, 20000m]],
        Ahorro: new ConsumoAhorro("Backup", 5000m, "2025-10", 1800m, "2026-01", 3200m, 3, 38400m),
        Comparativa: new ConsumoComparativa("2025-01", "2026-01", [["Storage", 4000m, 3500m]]),
        PorCentroCosto: [["TI", 100000m]]);

    private static readonly InformeValorCobertura Cobertura = new(
        Total: 2,
        Suscripciones:
        [
            new CoberturaSuscripcion("sub-1", "Suscripción Producción", Facturacion: true, Rbac: true, Advisor: true),
            new CoberturaSuscripcion("sub-2", "Suscripción Secundaria", Facturacion: true, Rbac: false, Advisor: false),
        ]);

    private static ModeloInformeValor ModeloCompleto() => new(
        new InformeValorMeta("Cliente Demo", "Enero 2026", "2026-01-31", Cobertura),
        Operacion, Consumo, Seguridad, Postura, Roadmap,
        CatSerie: new Dictionary<string, IReadOnlyDictionary<string, decimal>>
        {
            ["Backup"] = new Dictionary<string, decimal> { ["2026-01"] = 20000m },
        });

    [Fact]
    public void El_modelo_completo_serializa_sin_lanzar()
    {
        var json = JsonSerializer.Serialize(ModeloCompleto(), InformeValorJsonOptions.Instance);
        Assert.NotEmpty(json);
    }

    [Fact]
    public void Las_filas_posicionales_serializan_como_arreglo_no_como_objeto()
    {
        var json = JsonSerializer.Serialize(ModeloCompleto(), InformeValorJsonOptions.Instance);
        using var doc = JsonDocument.Parse(json);

        var serieMensual = doc.RootElement.GetProperty("fact").GetProperty("meses")[0];
        Assert.Equal(JsonValueKind.Array, serieMensual.ValueKind);
        Assert.Equal("2026-01", serieMensual[0].GetString());
        Assert.Equal(20000m, serieMensual[1].GetDecimal());
        Assert.Equal(0, serieMensual[2].GetInt32());
    }

    /// <summary>D9: el eje no medido viaja como null JSON real, nunca como 0.</summary>
    [Fact]
    public void Sin_actividad_de_sesion_nula_viaja_como_null_json()
    {
        var json = JsonSerializer.Serialize(ModeloCompleto(), InformeValorJsonOptions.Instance);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("rbac").GetProperty("sinLogin").ValueKind);
        Assert.False(doc.RootElement.GetProperty("rbac").GetProperty("ultimoLoginMedido").GetBoolean());
    }

    /// <summary>D12 (Tarea 8): la cobertura vive DENTRO de <c>meta</c>, no como una octava clave de
    /// nivel superior (ver el comentario de clase de <see cref="ModeloInformeValor"/>), y publica
    /// qué fuente cubre cada suscripción del conjunto unión.</summary>
    [Fact]
    public void La_cobertura_vive_dentro_de_meta_y_declara_que_fuente_cubre_cada_suscripcion()
    {
        var json = JsonSerializer.Serialize(ModeloCompleto(), InformeValorJsonOptions.Instance);
        using var doc = JsonDocument.Parse(json);

        var cobertura = doc.RootElement.GetProperty("meta").GetProperty("cobertura");
        Assert.Equal(2, cobertura.GetProperty("total").GetInt32());
        var subs = cobertura.GetProperty("suscripciones");
        var sub2 = subs.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "sub-2");
        Assert.Equal("Suscripción Secundaria", sub2.GetProperty("nombre").GetString());
        Assert.True(sub2.GetProperty("facturacion").GetBoolean());
        Assert.False(sub2.GetProperty("rbac").GetBoolean());
        Assert.False(sub2.GetProperty("advisor").GetBoolean());
    }

    /// <summary>El diccionario de compromiso por suscripción (D13, misma clase de riesgo que
    /// catSerie) sobrevive con una clave con espacios y acento.</summary>
    [Fact]
    public void Compromiso_por_suscripcion_preserva_la_clave()
    {
        var json = JsonSerializer.Serialize(ModeloCompleto(), InformeValorJsonOptions.Instance);
        using var doc = JsonDocument.Parse(json);

        var porSub = doc.RootElement.GetProperty("advisor").GetProperty("porSub");
        Assert.True(porSub.TryGetProperty("Suscripción Producción", out var compromiso));
        Assert.Equal(2000m, compromiso.GetProperty("ri").GetDecimal());
        Assert.Equal(1500m, compromiso.GetProperty("sp").GetDecimal());
    }

    /// <summary>D7: el veredicto por fila (contada) viaja junto a la línea, no se re-deriva en la
    /// capa de dibujo comparando contra el diccionario de compromiso.</summary>
    [Fact]
    public void La_linea_de_ahorro_lleva_su_propio_veredicto_contada()
    {
        var json = JsonSerializer.Serialize(ModeloCompleto(), InformeValorJsonOptions.Instance);
        using var doc = JsonDocument.Parse(json);

        var linea = doc.RootElement.GetProperty("advisor").GetProperty("savLineas")[0];
        Assert.True(linea.GetProperty("contada").GetBoolean());
    }

    // ── El bloque de variación del consumo tiene UNA sola forma, salga por donde salga ──

    /// <summary>Un <c>fact.variacionConsumo</c> con los dos sub-bloques poblados y el MISMO recurso
    /// en los dos (confirmado por reserva en el balde 1, anotado como excluido en la atribución):
    /// es el cruce que E3/E9 obliga a hacer para conciliar, y el que se rompe si cada lado escribe
    /// la terna con una grafía distinta.</summary>
    private static VariacionConsumoModelo VariacionConsumoDePrueba() => new(
        Reservas: new AhorroReservasModelo(
            Medido: true, Motivo: "Las reservas activas se leyeron completas desde Azure.",
            Errores: [], AlertDays: 30, AhorroConfirmado: 120m,
            Confirmados:
            [
                new AhorroPorRecurso(
                    ResourceName: "vm-1", ResourceGroup: "rg-1", SubscriptionId: "sub-1",
                    ReservationId: "resv-1", ReservationName: "Reserva de prueba", Term: "P1Y",
                    InicioReserva: "2026-04-10", UsedHours: 700, UtilizationLast: "80%",
                    Utilization7d: "75%", Expiring: false, TarifaAntesPorHora: 1m,
                    TarifaDespuesPorHora: 0.5m, Ahorro: 120m, MotivoSinCalcular: null,
                    ExplicaElPeriodo: true, AporteAlPeriodo: 250m),
            ],
            Estimados: [new EstimadoPorReserva("resv-1", "Reserva de prueba", "Standard_D2s_v5", "eastus", "P1Y", 1, false)],
            Discrepancias: [],
            AporteAlPeriodo: 250m, RecursosQueExplicanElPeriodo: ["sub-1|rg-1|vm-1"],
            ReservasConConsumidoresNoLeidos: 0),
        Atribucion: new AtribucionModelo(
            PorRecomendacion: new AtribucionBalde(0m, 0, []),
            SinAtribuir: new SinAtribuirModelo(
                new AtribucionBalde(0m, 0, []), new AtribucionBalde(0m, 0, []),
                new AtribucionBalde(0m, 0, []), new AtribucionBalde(0m, 0, []), 0m),
            Crecimiento: 0m, VariacionTotal: 0m,
            ExcluidosPorReserva:
            [
                // Valores sin acentos a propósito: los dos juegos de opciones usan encoders
                // distintos (Instance relaja el escapado), y este fixture compara NOMBRES DE CLAVE,
                // no cómo se escapa el contenido.
                new AtribucionRecurso("sub-1", "Suscripcion de prueba", "rg-1", "vm-1", 400m, 150m, 250m, []),
            ]),
        VariacionTotal: 250m);

    /// <summary>
    /// El bloque tiene que salir con las MISMAS claves con las dos opciones de serialización que
    /// existen en el módulo: la política global de <c>Program.cs</c> (snake_case, la que usa
    /// <c>/preview</c>) y <see cref="InformeValorJsonOptions.Instance"/> (sin política, la que el
    /// artefacto HTML de la entrega 3 tiene que usar sí o sí). Solo se cumple si cada campo declara
    /// su <c>[JsonPropertyName]</c>: sin atributos, el mismo bloque sale en snake_case por un lado y
    /// en PascalCase por el otro, o sea dos nombres para el mismo dato según por dónde salga.
    /// </summary>
    [Fact]
    public void La_variacion_del_consumo_serializa_igual_con_las_dos_opciones_del_modulo()
    {
        var opcionesGlobalesDelRepo = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        var bloque = VariacionConsumoDePrueba();

        var conInstance = JsonSerializer.Serialize(bloque, InformeValorJsonOptions.Instance);
        var conLaGlobal = JsonSerializer.Serialize(bloque, opcionesGlobalesDelRepo);

        Assert.Equal(conInstance, conLaGlobal);
    }

    /// <summary>La otra mitad del mismo contrato: la terna del recurso se escribe igual en el balde
    /// de reservas y en <c>excluidosPorReserva</c>, que es contra lo que hay que cruzarla (E3/E9).
    /// Antes convivían <c>resource_name</c> y <c>resourceName</c> en la misma respuesta.</summary>
    [Fact]
    public void La_terna_del_recurso_se_escribe_igual_en_los_dos_sub_bloques()
    {
        var json = JsonSerializer.Serialize(VariacionConsumoDePrueba(), InformeValorJsonOptions.Instance);
        using var doc = JsonDocument.Parse(json);

        var confirmado = doc.RootElement.GetProperty("reservas").GetProperty("confirmados")[0];
        var excluido = doc.RootElement.GetProperty("atribucion").GetProperty("excluidosPorReserva")[0];

        Assert.Equal("vm-1", confirmado.GetProperty("resourceName").GetString());
        Assert.Equal("vm-1", excluido.GetProperty("resourceName").GetString());
        Assert.Equal("rg-1", confirmado.GetProperty("resourceGroup").GetString());
        Assert.Equal("sub-1", confirmado.GetProperty("subscriptionId").GetString());
        // El resto del bloque de reservas, en la misma convención camelCase que su hermano.
        var reservas = doc.RootElement.GetProperty("reservas");
        Assert.Equal(120m, reservas.GetProperty("ahorroConfirmado").GetDecimal());
        Assert.Equal(30, reservas.GetProperty("alertDays").GetInt32());
        Assert.Equal(250m, reservas.GetProperty("aporteAlPeriodo").GetDecimal());
        Assert.Equal(0, reservas.GetProperty("reservasConConsumidoresNoLeidos").GetInt32());
    }

    /// <summary>El bloque cuelga de <c>fact</c> en el modelo completo, así que también viaja por el
    /// artefacto de la entrega 3, no solo por el endpoint de vista previa.</summary>
    [Fact]
    public void La_variacion_del_consumo_viaja_dentro_de_fact_en_el_modelo_completo()
    {
        var modelo = ModeloCompleto();
        modelo = modelo with { Consumo = modelo.Consumo! with { VariacionConsumo = VariacionConsumoDePrueba() } };

        var json = JsonSerializer.Serialize(modelo, InformeValorJsonOptions.Instance);
        using var doc = JsonDocument.Parse(json);

        var reservas = doc.RootElement.GetProperty("fact").GetProperty("variacionConsumo").GetProperty("reservas");
        Assert.True(reservas.GetProperty("medido").GetBoolean());
        Assert.Equal("vm-1", reservas.GetProperty("confirmados")[0].GetProperty("resourceName").GetString());
    }

    /// <summary>El modelo se puede reconstruir de vuelta desde su propio JSON: no es solo una
    /// serialización de ida, un futuro lector (p. ej. releer una entrega archivada) puede
    /// deserializar con las mismas opciones y estas mismas clases.</summary>
    [Fact]
    public void El_modelo_completo_hace_round_trip()
    {
        var original = ModeloCompleto();
        var json = JsonSerializer.Serialize(original, InformeValorJsonOptions.Instance);

        var vuelta = JsonSerializer.Deserialize<ModeloInformeValor>(json, InformeValorJsonOptions.Instance);

        Assert.NotNull(vuelta);
        Assert.Equal(original.Meta.Cliente, vuelta!.Meta.Cliente);
        Assert.Equal(original.Consumo!.Total, vuelta.Consumo!.Total);
        Assert.Equal(original.Seguridad!.SinActividadSesion, vuelta.Seguridad!.SinActividadSesion);
        Assert.Equal(
            original.Postura!.CompromisoPorSuscripcion["Suscripción Producción"].Reserva,
            vuelta.Postura!.CompromisoPorSuscripcion["Suscripción Producción"].Reserva);
        Assert.Equal(original.Roadmap!.Items[0].Hallazgo, vuelta.Roadmap!.Items[0].Hallazgo);
        Assert.Equal(original.CatSerie!["Backup"]["2026-01"], vuelta.CatSerie!["Backup"]["2026-01"]);
    }
}
