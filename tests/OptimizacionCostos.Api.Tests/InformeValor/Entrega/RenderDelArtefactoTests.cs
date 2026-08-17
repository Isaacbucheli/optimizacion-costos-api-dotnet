using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;

namespace OptimizacionCostos.Api.Tests.InformeValor.Entrega;

/// <summary>
/// Lo que el artefacto le IMPRIME al cliente, ejecutando su capa de dibujo de verdad (ver
/// <see cref="RenderDeArtefacto"/> para el motor y para el gate por <c>INFORME_VALOR_NODE</c>).
///
/// <para>Cada test de acá es un escenario que la revisión adversarial encontró publicado o roto en
/// el archivo entregado y correcto en la vista React que el consultor aprueba. Son los dos defectos
/// de fondo del módulo en su forma más caracterizada: el cero ambiguo (una cifra vacía que se lee
/// como un hecho del negocio) y las dos piezas que tratan el mismo concepto con definiciones
/// distintas, cada una coherente consigo misma.</para>
/// </summary>
public sealed class RenderDelArtefactoTests
{
    // ================================================================================
    // Andamio
    // ================================================================================

    /// <summary>Sin esto, cualquier test de abajo podría estar pasando por no ejecutar nada.</summary>
    [Fact]
    public void El_artefacto_se_dibuja_completo_con_el_modelo_de_prueba()
    {
        var r = RenderDeArtefacto.Correr(ModeloDePrueba.Crear(), VarianteInforme.Interna);
        if (r is null) return;

        r.ExigirQueDibujeCompleto();
        Assert.Contains("Cliente de prueba", r.Nodo("h-cliente").Todo, StringComparison.Ordinal);
        Assert.NotEqual("", r.Nodo("body-eficiencia").Html);
        Assert.NotEqual("", r.Nodo("body-roadmap").Html);
        Assert.NotEqual("", r.Nodo("body-advisor").Html);
    }

    // ================================================================================
    // Ningún caso con el SLA evaluado
    // ================================================================================

    /// <summary>
    /// Con la columna "Cumple SLA" en un vocabulario que <c>ClasificarSla</c> no reconoce
    /// ("CUMPLE", "Dentro", "1", "TRUE"), o vacía en todos los casos del período, el modelo publica
    /// <c>denominadorPct = 0</c> y <c>pct = 0</c> — <c>Division.Porcentaje</c> devuelve 0 sin
    /// denominador. El artefacto publicaba entonces "0.00 %" de cumplimiento en el hero y en el
    /// resumen, "Casos fuera del acuerdo: 0" con la bajada "100.00% de los casos con SLA evaluado",
    /// una barra roja al 100% y, en el mismo documento, el titular "La operación no registró un solo
    /// incumplimiento". El mismo archivo afirmaba cero cumplimiento y cero incumplimientos a la vez.
    ///
    /// <para><c>SeccionOperacion.tsx</c> ya resolvía exactamente este caso con "Sin medir", así que
    /// el consultor aprobaba una entrega que declara el hueco y el cliente recibía la que publica el
    /// cero.</para>
    /// </summary>
    [Fact]
    public void Sin_ningun_caso_con_sla_evaluado_no_se_publica_un_cumplimiento_de_cero()
    {
        var r = RenderDeArtefacto.Correr(SinSlaEvaluado(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        var hero = r.Nodo("hero-kpis").Html;
        Assert.DoesNotContain("data-v=\"0.00\"", hero, StringComparison.Ordinal);
        Assert.Contains("Sin medir", hero, StringComparison.Ordinal);

        // El resumen ejecutivo publicaba la misma cifra en su tarjeta de "Cumplimiento de SLA".
        Assert.DoesNotContain(">0.00<", r.Nodo("body-resumen").Html, StringComparison.Ordinal);

        var operacion = r.Nodo("body-operacion").Html;
        Assert.DoesNotContain("0.00% de cumplimiento", operacion, StringComparison.Ordinal);
        Assert.DoesNotContain("100.00%", operacion, StringComparison.Ordinal);
        Assert.Contains("Sin medir", operacion, StringComparison.Ordinal);
    }

    /// <summary>La otra mitad del mismo defecto: el titular afirmaba que no hubo un solo
    /// incumplimiento porque <c>noCumple === 0</c>, sin mirar si alguien evaluó algo.</summary>
    [Fact]
    public void Sin_ningun_caso_con_sla_evaluado_el_titular_no_afirma_que_no_hubo_incumplimientos()
    {
        var r = RenderDeArtefacto.Correr(SinSlaEvaluado(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        Assert.DoesNotContain("no registró un solo incumplimiento",
            r.Nodo("h2-operacion").Todo, StringComparison.Ordinal);
        Assert.DoesNotContain("Ningún incumplimiento de SLA en todo el período",
            r.Nodo("sub-operacion").Todo, StringComparison.Ordinal);
    }

    /// <summary>Y la guarda no puede tapar el caso normal: con casos evaluados el porcentaje se
    /// publica igual que siempre. Un test que solo prueba el hueco deja pasar un arreglo que apaga
    /// la cifra para todos.</summary>
    [Fact]
    public void Con_casos_evaluados_el_cumplimiento_se_publica_normalmente()
    {
        var r = RenderDeArtefacto.Correr(ModeloDePrueba.Crear(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        // 3 de 4 casos evaluados = 75.00 %.
        Assert.Contains("data-v=\"75.00\"", r.Nodo("hero-kpis").Html, StringComparison.Ordinal);
        Assert.Contains("75.00% de cumplimiento", r.Nodo("body-operacion").Html, StringComparison.Ordinal);
    }

    // ================================================================================
    // Advisor sin recomendaciones y con retiros
    // ================================================================================

    /// <summary>
    /// <c>PosturaCalculador</c> solo devuelve null si Advisor Y retiros están vacíos, y los dos
    /// salen de tablas independientes (<c>waf_resource_finding</c> y <c>boletin_retirement</c>). Un
    /// cliente con el Boletín sincronizado y cero hallazgos activos de Advisor —nunca sincronizado,
    /// todo resuelto, o el único pilar con hallazgos era Seguridad y se gestiona por fuera— produce
    /// un bloque de postura NO nulo con <c>cats</c>/<c>subs</c>/<c>tipos</c> vacíos.
    ///
    /// <para>La plantilla leía <c>cats[0]</c>, <c>subs[0]</c> y <c>tipos[0]</c> sin guarda (en cuatro
    /// lugares, incluido el de próximos pasos) y <c>render()</c> reventaba: el artefacto salía con
    /// los contadores del hero en su "0" literal —le afirma al cliente 0 % de SLA y 0 asignaciones
    /// auditadas— y sin Eficiencia financiera, Roadmap ni Trazabilidad. En la plantilla original las
    /// dos mitades venían del mismo CSV, así que este estado era imposible; lo abrió la decisión de
    /// traer los retiros de una tabla propia.</para>
    /// </summary>
    [Fact]
    public void Con_advisor_vacio_y_retiros_cargados_el_artefacto_se_dibuja_completo()
    {
        var r = RenderDeArtefacto.Correr(SinRecomendacionesDeAdvisor(), VarianteInforme.Cliente, []);
        if (r is null) return;

        r.ExigirQueDibujeCompleto();

        Assert.NotEqual("", r.Nodo("body-advisor").Html);
        Assert.NotEqual("", r.Nodo("body-eficiencia").Html);
        Assert.NotEqual("", r.Nodo("body-roadmap").Html);
        // Trazabilidad y próximos pasos viven dentro del cuerpo del roadmap.
        Assert.Contains("Trazabilidad de las cifras", r.Nodo("body-roadmap").Html, StringComparison.Ordinal);
        // El retiro es la razón por la que el bloque existe: tiene que verse.
        Assert.Contains("Clásico", r.Nodo("body-advisor").Html, StringComparison.Ordinal);
    }

    /// <summary>Sin recomendaciones no hay backlog que concentrar: ese 0 % es falta de denominador,
    /// no una postura sin deuda. La vista React ya lo declaraba con "Sin medir".</summary>
    [Fact]
    public void Sin_recomendaciones_la_concentracion_del_backlog_no_se_publica_como_cero()
    {
        var r = RenderDeArtefacto.Correr(SinRecomendacionesDeAdvisor(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        var advisor = r.Nodo("body-advisor").Html;
        Assert.DoesNotContain("Las quince recomendaciones más repetidas suman 0", advisor, StringComparison.Ordinal);
        Assert.Contains("Sin medir", advisor, StringComparison.Ordinal);
    }

    // ================================================================================
    // El titular de Eficiencia financiera
    // ================================================================================

    /// <summary>
    /// El titular comparaba los dos promedios mensuales (<c>fact.prom[último][2] &gt; fact.prom[0][2]</c>),
    /// y el recorte del bloque "Gasto total" los anula los dos. En JavaScript <c>null &gt; null</c> es
    /// false, así que la variante del cliente titulaba siempre "El gobierno del gasto dio resultado"
    /// —la conclusión favorable al proveedor— sobre los montos que el consultor decidió no publicar,
    /// mientras la variante interna, con los mismos datos, titulaba lo contrario.
    ///
    /// <para>Suprimir un monto no puede invertir una afirmación. Las otras dos lecturas de
    /// <c>prom</c> de esa misma sección ya estaban guardadas contra null; esta no.</para>
    /// </summary>
    [Fact]
    public void Con_el_gasto_total_apagado_el_titular_de_eficiencia_no_concluye_sobre_montos_suprimidos()
    {
        var r = RenderDeArtefacto.Correr(ConGastoQueCrecio(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        var titular = r.Nodo("h2-eficiencia").Todo;
        Assert.DoesNotContain("El gobierno del gasto dio resultado", titular, StringComparison.Ordinal);
        Assert.DoesNotContain("El gasto creció", titular, StringComparison.Ordinal);
    }

    /// <summary>Y con el bloque aprobado el titular sí concluye: la guarda no puede volverse una
    /// mordaza para el caso en que los dos promedios existen.</summary>
    [Fact]
    public void Con_el_gasto_total_aprobado_el_titular_dice_que_el_gasto_crecio()
    {
        var r = RenderDeArtefacto.Correr(
            ConGastoQueCrecio(), VarianteInforme.Cliente, [BloqueEconomico.GastoTotal]);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        Assert.Contains("El gasto creció", r.Nodo("h2-eficiencia").Todo, StringComparison.Ordinal);
    }

    // ================================================================================
    // La columna Esfuerzo del roadmap
    // ================================================================================

    /// <summary>
    /// <c>RoadmapCalculador</c> pone <c>Esfuerzo: null</c> en todos los ítems a propósito (la columna
    /// numérica llega en la entrega 4) y el modelo documenta que null es "no medido", nunca "cero
    /// esfuerzo". El artefacto mantenía el encabezado "Esfuerzo" y dibujaba un guion en el 100 % de
    /// las filas, sin ninguna frase que dijera por qué. La vista React lo declara tres veces.
    /// </summary>
    [Fact]
    public void La_columna_de_esfuerzo_del_roadmap_dice_que_no_esta_medida()
    {
        var r = RenderDeArtefacto.Correr(ConRoadmapSinEsfuerzo(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        var roadmap = r.Nodo("body-roadmap").Html;
        Assert.Contains("Esfuerzo", roadmap, StringComparison.Ordinal);
        Assert.Contains("Sin medir", roadmap, StringComparison.Ordinal);
        Assert.Contains("texto libre", roadmap, StringComparison.Ordinal);
    }

    // ================================================================================
    // "0 retiros" y su fuente
    // ================================================================================

    /// <summary>
    /// Los retiros salen del módulo Boletín, que se sincroniza a mano y por cliente y nace denegado
    /// en permisos. Con la tabla vacía el artefacto publicaba la tarjeta "0 retiros" afirmando que
    /// "el export no reporta características en proceso de retiro sobre este parque": afirma un hecho
    /// que nadie midió y le atribuye la sección a un export de Advisor que no es su fuente, así que
    /// el lector no puede ni preguntar por la correcta.
    /// </summary>
    [Fact]
    public void Sin_corrida_del_boletin_el_artefacto_no_afirma_que_no_hay_retiros()
    {
        var r = RenderDeArtefacto.Correr(SinRetirosNiCorridaDelBoletin(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        var advisor = r.Nodo("body-advisor").Html;
        Assert.DoesNotContain("El export no reporta", advisor, StringComparison.Ordinal);
        Assert.Contains("Sin medir", advisor, StringComparison.Ordinal);
        Assert.Contains("todavía no sincronizó", advisor, StringComparison.Ordinal);
    }

    /// <summary>La trazabilidad tiene que nombrar al Boletín como insumo propio: su única fila
    /// atribuía toda la sección al "Export de Azure Advisor", que en este módulo no existe (Advisor
    /// llega por la sincronización de la plataforma) y que además no es de donde salen los
    /// retiros.</summary>
    [Fact]
    public void La_trazabilidad_nombra_al_boletin_como_fuente_de_los_retiros()
    {
        var r = RenderDeArtefacto.Correr(SinRetirosNiCorridaDelBoletin(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        var roadmap = r.Nodo("body-roadmap").Html;
        Assert.Contains("Trazabilidad de las cifras", roadmap, StringComparison.Ordinal);
        Assert.Contains("Boletín de Azure", roadmap, StringComparison.Ordinal);
        Assert.DoesNotContain("Export de Azure Advisor", roadmap, StringComparison.Ordinal);
    }

    /// <summary>Con la corrida cerrada el cero es un hecho y se publica como tal: la guarda no puede
    /// convertir todo cero en un "sin medir".</summary>
    [Fact]
    public void Con_la_corrida_del_boletin_completa_el_cero_de_retiros_se_publica()
    {
        var modelo = SinRetirosNiCorridaDelBoletin();
        modelo = modelo with { Postura = modelo.Postura! with { RetirosMedido = true, RetirosMotivo = null } };

        var r = RenderDeArtefacto.Correr(modelo, VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        Assert.Contains("no registra ninguna característica en proceso de retiro",
            r.Nodo("body-advisor").Html, StringComparison.Ordinal);
    }

    // ================================================================================
    // El frente residual y la proporción de trabajo proactivo
    // ================================================================================

    /// <summary>
    /// D1 agrega un frente residual "(sin subcategoría)" para que la suma cierre, y no es reactivo:
    /// con <c>nFrentes - nFrentesR</c> caía del lado proactivo. Un export sin la columna
    /// Subcategoría poblada daba entonces un solo frente, cero reactivos y 100 % de trabajo
    /// proactivo — con el titular "Casi todo el trabajo nació antes del problema", una barra
    /// rotulada "1 frentes (100.0%)" cuyo tooltip decía "0 casos", y en la misma nota "medido en
    /// volumen de casos la proporción es del 0.0%". Dos definiciones del mismo concepto, y el
    /// titular quedándose con la halagüeña.
    /// </summary>
    [Fact]
    public void Con_todos_los_casos_sin_subcategoria_no_se_publica_trabajo_proactivo_al_cien()
    {
        var r = RenderDeArtefacto.Correr(SinSubcategorias(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        Assert.DoesNotContain("100.0%", r.Nodo("body-proactiva").Html, StringComparison.Ordinal);
        Assert.DoesNotContain("nació antes del problema", r.Nodo("h2-proactiva").Todo, StringComparison.Ordinal);
        Assert.DoesNotContain(">100.0<", r.Nodo("body-resumen").Html, StringComparison.Ordinal);
        Assert.Contains("Sin medir", r.Nodo("body-resumen").Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// El caso parcial, que es el que va a pasar de verdad: el residual sumaba +1 al numerador de
    /// frentes proactivos. Con 3 reactivos, 6 proactivos y el residual publicaba 7/10 = 70,0 % —
    /// justo cruzando el umbral del titular— en vez de 6/9 = 66,7 %. Y el titular ahora sale del
    /// volumen, que es la regla escrita.
    /// </summary>
    [Fact]
    public void El_residual_no_infla_la_proporcion_por_frentes()
    {
        var r = RenderDeArtefacto.Correr(ConFrenteResidual(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();

        var proactiva = r.Nodo("body-proactiva").Html;
        Assert.Contains("66.7%", proactiva, StringComparison.Ordinal);
        Assert.DoesNotContain("70.0%", proactiva, StringComparison.Ordinal);
        // El titular usa el volumen (60 de 100 casos = 60.0 %), que no llega al umbral del 70 %.
        Assert.DoesNotContain("nació antes del problema", r.Nodo("h2-proactiva").Todo, StringComparison.Ordinal);
    }

    // ================================================================================
    // Las cuatro tarjetas del resumen (reunión del 2026-08-13)
    // ================================================================================

    /// <summary>Las cuatro tarjetas del resumen, en el orden que pidió la reunión del 2026-08-13:
    /// optimización, opex, SLA en tercer lugar, avance de remediación. RBAC ya no está: su detalle
    /// vive en la sección de seguridad.</summary>
    [Fact]
    public void El_hero_lleva_las_cuatro_tarjetas_en_el_orden_de_la_reunion()
    {
        var r = RenderDeArtefacto.Correr(ModeloDePrueba.Crear(), VarianteInforme.Interna);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();
        var hero = r.Nodo("hero-kpis").Todo;
        var iOpt = hero.IndexOf("OPTIMIZACIÓN", StringComparison.Ordinal);
        var iOpex = hero.IndexOf("OPEX", StringComparison.Ordinal);
        var iSla = hero.IndexOf("OPERACIÓN", StringComparison.Ordinal);
        var iEvo = hero.IndexOf("EVOLUCIÓN", StringComparison.Ordinal);
        Assert.True(iOpt >= 0 && iOpex > iOpt && iSla > iOpex && iEvo > iSla,
            "orden esperado optimización < opex < operación < evolución, salió: " + hero);
        Assert.DoesNotContain("Asignaciones de acceso auditadas", hero, StringComparison.Ordinal);
    }

    /// <summary>La tarjeta de optimización habla en PORCENTAJE, no en dinero (decisión 2026-08-13):
    /// así viaja siempre, incluso en la variante del cliente sin bloques aprobados.</summary>
    [Fact]
    public void La_tarjeta_de_optimizacion_publica_el_porcentaje_aun_sin_bloques()
    {
        var r = RenderDeArtefacto.Correr(ModeloDePrueba.Crear(), VarianteInforme.Cliente, []);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();
        var hero = r.Nodo("hero-kpis").Todo;
        Assert.Contains("OPTIMIZACIÓN", hero, StringComparison.Ordinal);
        Assert.DoesNotContain("No publicado", hero.Substring(0, hero.IndexOf("OPEX", StringComparison.Ordinal)),
            StringComparison.Ordinal);
    }

    /// <summary>Sin snapshot de Advisor la tarjeta dice "sin medición" con su motivo, jamás 0%.</summary>
    [Fact]
    public void Sin_score_de_opex_la_tarjeta_declara_en_vez_de_publicar_cero()
    {
        var r = RenderDeArtefacto.Correr(SinOpexMedido(), VarianteInforme.Interna);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();
        var hero = r.Nodo("hero-kpis").Todo;
        Assert.Contains("Sin medición", hero, StringComparison.Ordinal);
        Assert.DoesNotContain("0%", hero, StringComparison.Ordinal);
    }

    /// <summary>Regla de copy de la reunión: cada cifra aparece una sola vez. Con el denominador
    /// completo y todos cumpliendo, el texto dice "todos", no repite el número tres veces.</summary>
    [Fact]
    public void El_sla_perfecto_dice_todos_en_vez_de_repetir_la_cifra()
    {
        var r = RenderDeArtefacto.Correr(ConSlaPerfecto(330), VarianteInforme.Interna);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();
        var hero = r.Nodo("hero-kpis").Todo;
        Assert.Contains("330 casos registrados, todos con SLA evaluado y dentro del acuerdo", hero, StringComparison.Ordinal);
    }

    /// <summary>RBAC baja del resumen al detalle técnico (observación 2), no se elimina.</summary>
    [Fact]
    public void El_detalle_de_rbac_sigue_en_la_seccion_de_seguridad()
    {
        var r = RenderDeArtefacto.Correr(ModeloDePrueba.Crear(), VarianteInforme.Interna);
        if (r is null) return;
        r.ExigirQueDibujeCompleto();
        Assert.Contains("Asignaciones RBAC directas", r.Nodo("body-seguridad").Todo, StringComparison.Ordinal);
    }

    // ================================================================================
    // Escenarios
    // ================================================================================

    /// <summary>El export trae la columna "Cumple SLA" en un vocabulario que el clasificador no
    /// reconoce: los diez casos quedan sin evaluar y el denominador del porcentaje es cero.</summary>
    private static ModeloInformeValor SinSlaEvaluado()
    {
        var modelo = ModeloDePrueba.Crear();
        return modelo with
        {
            Operacion = modelo.Operacion! with
            {
                Cumple = 0, NoCumple = 0, SinEvaluar = 10,
                PctCumplimiento = Division.Porcentaje(0, 0),
                DenominadorPctCumplimiento = 0,
                MediaHorasDentroSla = 0d,
                Categorias = [new OperacionCategoria("Cómputo", 10, 0, 3d)],
                SerieMensual = [["2026-01", 10, 0]],
                FueraDeSla = [],
                Detalle = [["C-1", "2026-01-05", "Cómputo", "Mejora", 4m, 2m, "SIN EVALUAR", "Horario laboral"]],
            },
        };
    }

    /// <summary>Boletín sincronizado (hay un retiro vigente) y cero hallazgos activos de Advisor.</summary>
    private static ModeloInformeValor SinRecomendacionesDeAdvisor()
    {
        var modelo = ModeloDePrueba.Crear();
        return modelo with
        {
            Postura = modelo.Postura! with
            {
                Total = 0, TiposDeRecomendacion = 0,
                Pilares = [], Suscripciones = [], TiposRecurso = [],
                Top = [], TopSuma = 0, Detalle = [],
                NumRecursos = 0, RecomendacionesConRecurso = 0,
                Alto = 0, Medio = 0, Bajo = 0,
                AhorroBruto = 0m, AhorroRealizable = 0m, AhorroDescartado = 0m,
                ConAhorroCuantificado = 0, LineasAhorro = [],
                CompromisoPorSuscripcion = new Dictionary<string, PosturaCompromisoSuscripcion>(),
            },
        };
    }

    /// <summary>Período que cruza dos años calendario y promedio mensual que subió de uno al otro:
    /// el caso que el titular de Eficiencia financiera tiene que poder afirmar, o callar.</summary>
    private static ModeloInformeValor ConGastoQueCrecio()
    {
        var modelo = ModeloDePrueba.Crear();
        return modelo with
        {
            Consumo = modelo.Consumo! with
            {
                PromediosPorAnio = [[2025, 12, 9000m, 108000m], [2026, 6, 15000m, 90000m]],
            },
        };
    }

    /// <summary>Cliente con hallazgos de Advisor y sin ninguna corrida del Boletín: la tabla de
    /// retiros está vacía porque nadie la llenó.</summary>
    private static ModeloInformeValor SinRetirosNiCorridaDelBoletin()
    {
        var modelo = ModeloDePrueba.Crear();
        var (medido, motivo) = PosturaCalculador.EstadoDeLosRetiros([], null);
        return modelo with
        {
            Postura = modelo.Postura! with
            {
                Retiros = [], RetirosVencidos = 0, RetirosProximosATresMeses = 0,
                RetirosMedido = medido, RetirosMotivo = motivo,
            },
        };
    }

    /// <summary>Un export de mesa de servicio sin la columna Subcategoría poblada: todos los casos
    /// caen en el frente residual y no hay nada clasificado.</summary>
    private static ModeloInformeValor SinSubcategorias()
    {
        var modelo = ModeloDePrueba.Crear();
        return modelo with
        {
            Operacion = modelo.Operacion! with
            {
                Frentes = [new OperacionFrente("(sin subcategoría)", 10, false)],
                TotalFrentes = 1, FrentesReactivos = 0, FrentesProactivos = 0,
                CasosReactivos = 0, CasosSinSubcategoria = 10,
            },
        };
    }

    /// <summary>El caso parcial: 6 frentes proactivos, 3 reactivos y el residual. Por frentes
    /// clasificados son 6 de 9 (66,7 %); contando el residual del lado proactivo salían 7 de 10
    /// (70,0 %), justo el umbral del titular.</summary>
    private static ModeloInformeValor ConFrenteResidual()
    {
        var modelo = ModeloDePrueba.Crear();
        var frentes = new List<OperacionFrente>();
        for (var i = 0; i < 6; i++) frentes.Add(new OperacionFrente($"Mantenimiento {i}", 10, false));
        for (var i = 0; i < 3; i++) frentes.Add(new OperacionFrente($"Falla {i}", 10, true));
        frentes.Add(new OperacionFrente("(sin subcategoría)", 10, false));

        return modelo with
        {
            Operacion = modelo.Operacion! with
            {
                Total = 100, Frentes = frentes,
                TotalFrentes = 10, FrentesReactivos = 3, FrentesProactivos = 6,
                CasosReactivos = 30, CasosSinSubcategoria = 10,
            },
        };
    }

    /// <summary>Matriz con hallazgos abiertos y el esfuerzo sin medir, que es el estado de hoy para
    /// todos los clientes.</summary>
    private static ModeloInformeValor ConRoadmapSinEsfuerzo()
    {
        var modelo = ModeloDePrueba.Crear();
        return modelo with
        {
            Roadmap = new RoadmapModelo(
                Total: 3,
                Items:
                [
                    new RoadmapItem("Costo", "Apagar recursos sin uso", "2026-01-10", 1, "1", null, 0, 4, null),
                    new RoadmapItem("Costo", "Revisar discos huérfanos", null, 2, "2", null, 40, 2, null),
                    new RoadmapItem("Fiabilidad", "Activar respaldo", null, 1, "1", null, 100, 1, null),
                ],
                Ambitos: [new RoadmapAmbito("Costo", 2, 6, 20), new RoadmapAmbito("Fiabilidad", 1, 1, 100)],
                Cerrados: 1, EnCurso: 1, SinIniciar: 1, AvancePromedio: 46.7d, HorasPendientes: null),
        };
    }

    /// <summary>Sin snapshot de Azure Advisor para el pilar de costos: la tarjeta OPEX tiene que
    /// declarar el hueco, nunca publicar 0%.</summary>
    private static ModeloInformeValor SinOpexMedido()
    {
        var modelo = ModeloDePrueba.Crear();
        return modelo with
        {
            Opex = modelo.Opex! with
            {
                Medido = false, Motivo = "No hay snapshot de Azure Advisor para este cliente.",
                Actual = null, Serie = [],
            },
        };
    }

    /// <summary>Todos los casos registrados quedaron con SLA evaluado y todos dentro del acuerdo:
    /// el denominador completo coincide con el total y con los que cumplen.</summary>
    private static ModeloInformeValor ConSlaPerfecto(int n)
    {
        var modelo = ModeloDePrueba.Crear();
        return modelo with
        {
            Operacion = modelo.Operacion! with
            {
                Total = n, Cumple = n, NoCumple = 0, SinEvaluar = 0,
                PctCumplimiento = 100d, DenominadorPctCumplimiento = n,
                FueraDeSla = [],
            },
        };
    }
}
