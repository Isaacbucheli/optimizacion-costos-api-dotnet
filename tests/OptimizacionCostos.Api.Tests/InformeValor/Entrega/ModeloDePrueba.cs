using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

namespace OptimizacionCostos.Api.Tests.InformeValor.Entrega;

/// <summary>
/// Un <see cref="ModeloInformeValor"/> con un valor distinto en CADA monto del modelo. Los montos
/// son marcadores irrepetibles (11xxx para consumo, 22xxx para Advisor, 33xxx para el acumulado
/// ejecutado, 34xxx para sus reservas facturadas) para que un test pueda afirmar "este monto no
/// está en el artefacto" buscando el número, sin depender de cómo se formatea ni de qué sección lo
/// dibuja.
/// </summary>
internal static class ModeloDePrueba
{
    public const string CategoriaConAcentos = "Cómputo y almacenamiento";
    public const string SuscripcionConAcentos = "Producción — núcleo";

    /// <summary>Todos los montos del modelo, con el bloque económico al que pertenecen. La lista es
    /// la que auditan los tests de recorte: si el modelo gana un monto nuevo y nadie lo agrega acá,
    /// el test de "ningún monto sobrevive con todo apagado" lo deja pasar.</summary>
    public static readonly (string Bloque, decimal Monto)[] Montos =
    [
        ("gastoTotal", 11001m),           // fact.total
        ("gastoTotal", 11002m),           // fact.prom[0][2] (promedio mensual)
        ("gastoTotal", 11003m),           // fact.prom[0][3] (total anual)
        ("serieMensual", 11101m),         // fact.meses[0][1]
        ("serieMensual", 11102m),         // fact.serie[0][4] (gasto del mes)
        ("serieMensual", 11103m),         // fact.serie[0][5] (costo retirado del mes)
        ("composicionServicio", 11201m),  // catSerie[cat][mes]
        ("composicionServicio", 11202m),  // fact.subs[0][1]
        ("composicionServicio", 11203m),  // fact.comp.filas[0][1]
        ("composicionServicio", 11204m),  // fact.comp.filas[0][2]
        ("ahorroActivo", 11301m),         // fact.ahorro.pico
        ("ahorroActivo", 11302m),         // fact.ahorro.fin
        ("ahorroActivo", 11303m),         // fact.ahorro.dif
        ("ahorroActivo", 11304m),         // fact.ahorro.anualizada
        ("ahorroActivo", 11305m),         // fact.cargaRet
        ("centroCosto", 11401m),          // fact.cc[0][1]
        ("ahorroAdvisor", 22001m),        // advisor.bruto
        ("ahorroAdvisor", 22002m),        // advisor.real
        ("ahorroAdvisor", 22003m),        // advisor.descarte
        ("ahorroAdvisor", 22004m),        // advisor.savLineas[0].monto
        ("ahorroAdvisor", 22005m),        // advisor.porSub[sub].ri
        ("ahorroAdvisor", 22006m),        // advisor.porSub[sub].sp
        ("ahorroEjecutado", 33001m),      // ejecutado.total
        ("ahorroEjecutado", 33002m),      // ejecutado.tasaVigente
        ("ahorroEjecutado", 33003m),      // ejecutado.facturado
        ("ahorroEjecutado", 33004m),      // ejecutado.estimado
        ("ahorroEjecutado", 33005m),      // ejecutado.proyeccionFin
        ("ahorroEjecutado", 33006m),      // ejecutado.serie[0][1] (tasa vigente)
        ("ahorroEjecutado", 33007m),      // ejecutado.serie[0][2] (acumulado)
        ("ahorroEjecutado", 33008m),      // ejecutado.porOportunidad[0][1]
        ("ahorroEjecutado", 33009m),      // ejecutado.catAcum[cat][mes]
        ("ahorroEjecutado", 33010m),      // ejecutado.filas[0].monto
        ("ahorroEjecutado", 33011m),      // ejecutado.proyeccion[0][1]
        ("ahorroEjecutado", 33012m),      // ejecutado.proyeccion[0][2]
        ("reservasFacturadas", 34001m),   // ejecutado.reservas.totalDemanda
        ("reservasFacturadas", 34002m),   // ejecutado.reservas.totalReserva
        ("reservasFacturadas", 34003m),   // ejecutado.reservas.totalAhorro
        ("reservasFacturadas", 34004m),   // ejecutado.reservas.ahorroAnualizado
        ("reservasFacturadas", 34005m),   // ejecutado.reservas.filas[0].demanda
        ("reservasFacturadas", 34006m),   // ejecutado.reservas.filas[0].reserva
        ("reservasFacturadas", 34007m),   // ejecutado.reservas.filas[0].ahorro
    ];

    /// <summary>El porcentaje de la tarjeta 1 (<c>ejecutado.pctGasto</c>): viaja SIEMPRE, aprobado o
    /// no el bloque <c>ahorroEjecutado</c> (decisión 2026-08-13), así que no entra en
    /// <see cref="Montos"/> — esa lista audita lo que SÍ tiene que desaparecer.</summary>
    public const decimal PctGastoDePrueba = 47.3m;

    public static ModeloInformeValor Crear(string cliente = "Cliente de prueba") => new(
        Meta: new InformeValorMeta(
            Cliente: cliente,
            Periodo: "2026-01 a 2026-02",
            Corte: "2026-03-01",
            Cobertura: new InformeValorCobertura(1, [new CoberturaSuscripcion("sub-1", SuscripcionConAcentos, true, false, true)]),
            RbacOrigen: "base"),
        Operacion: new OperacionModelo(
            Total: 10, Cumple: 3, NoCumple: 1, SinEvaluar: 6, PctCumplimiento: 75d,
            DenominadorPctCumplimiento: 4, Cerrados: 8, MediaHoras: 4d, MedianaHoras: 3d, P90Horas: 9d,
            MediaHorasDentroSla: 3d, DuracionOriginalEnDias: false,
            Categorias: [new OperacionCategoria("Cómputo", 10, 1, 3d)],
            SerieMensual: [["2026-01", 10, 1]],
            RachaMesesSinIncumplir: 0, RachaCasos: 0,
            Frentes: [new OperacionFrente("Mantenimiento", 10, false)],
            TotalFrentes: 1, FrentesReactivos: 0, FrentesProactivos: 1, CasosReactivos: 0, CasosSinSubcategoria: 0,
            PorHorario: [["Horario laboral", 10]],
            Desde: "2026-01-05", Hasta: "2026-02-20",
            FueraDeSla: [["C-1", "2026-01-05", "Cómputo", "Falla", 4m, 9m]],
            Detalle:
            [
                ["C-1", "2026-01-05", "Cómputo", "Falla", 4m, 9m, "NO", "Horario laboral"],
                ["C-2", "2026-01-06", "Cómputo", "Mejora", 4m, 2m, "SI", "Horario laboral"],
                ["C-3", "2026-01-07", "Cómputo", "Mejora", 4m, 2m, "SIN EVALUAR", "Horario laboral"],
            ]),
        Consumo: new ConsumoModelo(
            Filas: 500, FilasEnRango: 400, Total: 11001m,
            SerieMensual: [["2026-01", 11101m, 0], ["2026-02", 900m, 1]],
            UltimoMesCompleto: "2026-01",
            MesesParciales: ["2026-02"], MesesParcialesDetectadosAuto: ["2026-02"],
            MesesParcialesInexistentes: [],
            Suscripciones: [[SuscripcionConAcentos, 11202m]],
            NumRecursos: 12, NumIdentidades: 12, NumGruposRecursos: 3, NumCategorias: 2,
            PicoRecursosActivos: 12, MesDePicoActivos: "2026-01",
            Serie: [["2026-01", 12, 3, 1, 11102m, 11103m, 0]],
            BajasDefinitivas: 1, CargaRetirada: 11305m, UnidadCargaRetirada: "USD por mes",
            PromediosPorAnio: [[2026, 1, 11002m, 11003m]],
            Ahorro: new ConsumoAhorro(
                Categoria: CategoriaConAcentos, LineaBase: 11301m, BaseDesdeMes: "2026-01",
                Fin: 11302m, FinHastaMes: "2026-02", TasaMensual: 11303m, MesesSostenido: 4,
                Anualizada: 11304m),
            Comparativa: new ConsumoComparativa("2025-01", "2026-01", [["Storage", 11203m, 11204m]]),
            PorCentroCosto: [["Finanzas", 11401m]],
            // unitario/mom (Tarea 7, entrega 6): Lado.Ninguno en ContratoEntreRenderizadoresTests,
            // ningún renderizador los lee todavía, así que no llevan marcador de la lista de Montos
            // de arriba (esa lista audita fugas hacia los dos renderizadores existentes) ni
            // reutilizan ninguno de esos marcadores, para no ensuciar esa auditoría por accidente.
            CostoUnitario: [["2026-01", 12, 660m, 55m, 0]],
            VariacionMoM: [["2026-02", 40m, 15m, 25m]]),
        Seguridad: null,
        Postura: new PosturaModelo(
            Total: 8, TiposDeRecomendacion: 4,
            Pilares: [new PosturaPilar("Costo", 8, 3, 3, 2)],
            Suscripciones: [new PosturaConteo(SuscripcionConAcentos, 8)],
            TiposRecurso: [new PosturaConteo("Virtual machines", 8)],
            Top: [["Comprar reserva", "Costo", "High", 8]], TopSuma: 8,
            Detalle: [["Comprar reserva", "Costo", "High", SuscripcionConAcentos, 8]],
            NumRecursos: 4, RecomendacionesConRecurso: 8, Alto: 3, Medio: 3, Bajo: 2,
            AhorroBruto: 22001m, AhorroRealizable: 22002m, AhorroDescartado: 22003m,
            ConAhorroCuantificado: 1,
            LineasAhorro: [new PosturaLineaAhorro("Comprar reserva", SuscripcionConAcentos, 22004m, "RI", true)],
            CompromisoPorSuscripcion: new Dictionary<string, PosturaCompromisoSuscripcion>
            { [SuscripcionConAcentos] = new(22005m, 22006m) },
            Retiros: [new PosturaRetiro("Clásico", "2026-06-30", 2, "Menos de tres meses", false, true)],
            RetirosVencidos: 0, RetirosProximosATresMeses: 1,
            RetirosMedido: true, RetirosMotivo: null,
            SeguridadGestionadaExternamente: false, SeguridadGestionadaNota: null),
        Roadmap: null,
        CatSerie: new Dictionary<string, IReadOnlyDictionary<string, decimal>>
        {
            [CategoriaConAcentos] = new Dictionary<string, decimal> { ["2026-01"] = 11201m },
        },
        Ejecutado: new EjecutadoModelo(
            Medido: true, Motivo: null,
            Filas:
            [
                new AccionEjecutada(
                    Fuente: "barrido", Oportunidad: "Apagar VM ociosa", Categoria: CategoriaConAcentos,
                    SubscriptionId: null, ResourceGroup: null, ResourceName: null, MesEjecucion: "2026-01",
                    MesFin: null, MontoMensual: 33010m, FuenteMonto: "facturado", MotivoSinMonto: null,
                    Autoria: "declarada"),
            ],
            Serie: [["2026-01", 33006m, 33007m]],
            PorOportunidad: [["Apagar VM ociosa", 33008m]],
            PorCategoria: new Dictionary<string, IReadOnlyDictionary<string, decimal>>
            {
                [CategoriaConAcentos] = new Dictionary<string, decimal> { ["2026-01"] = 33009m },
            },
            AcumuladoTotal: 33001m, TasaVigenteCierre: 33002m, PctGastoPeriodo: PctGastoDePrueba,
            MontoFacturado: 33003m, MontoEstimado: 33004m, FilasSinMonto: 0,
            Proyeccion: [["2026-03", 33011m, 33012m]], ProyeccionFinDeAnio: 33005m,
            Reservas: new ReservasFacturadasModelo(
                Medido: true, Motivo: null,
                Filas:
                [
                    new ReservaVmFila(
                        ReservationId: "res-1", Vm: "vm-1", Sku: "D2s_v3",
                        PorDemandaMes: 34005m, ReservaMes: 34006m, AhorroMes: 34007m,
                        Compartida: false, Vence: "2027-01-01", PorVencer: false, Nota: null),
                ],
                TotalDemanda: 34001m, TotalReserva: 34002m, TotalAhorro: 34003m, AhorroAnualizado: 34004m,
                SinLineaEnEvolucion: [], ConsumidoresNoLeidos: 0),
            Ejes: new RegistroEjes(
                BarridoMedido: true, BarridoMotivo: null, ReservasMedidas: true, ReservasMotivo: null,
                Indeterminadas: 0)));
}
