using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 6 del plan de la entrega 2b sobre <see cref="PosturaCalculadora"/>: D7 (la tabla de
/// criterio técnico suma su propio total), D8 (categoría e impacto salen de los campos numéricos),
/// D11 (la identidad de un recurso es suscripción + grupo + nombre) y D13 (fechas, claves de
/// diccionario), más la seguridad gestionada externamente agregada tras la revisión del encargo
/// (no una de las cuatro decisiones originales). Casos armados a mano (spec §12.2, verificación
/// híbrida): las decisiones existen justamente para que la calculadora NO reproduzca lo que hace
/// la plantilla en estos casos, así que la plantilla no sirve de referencia para ellos.
/// </summary>
public sealed class PosturaCalculadoraTests
{
    private static readonly DateOnly Corte = new(2026, 4, 1);

    private static AdvisorFila Fila(
        int pilar = 1, string nombrePilar = "Confiabilidad", int? impacto = 1, string? textoImpacto = null,
        string recomendacion = "Recomendación de prueba", string suscripcion = "Sub 1", string grupo = "rg-1",
        string? recurso = "vm-1", string tipoRecurso = "Microsoft.Compute/virtualMachines",
        decimal? ahorro = null, int canonicalId = 1) => new(
        PillarNumber: pilar, Pilar: nombrePilar, ImpactNumber: impacto,
        Impacto: textoImpacto ?? (impacto switch { 1 => "Alto", 2 => "Medio", 3 => "Bajo", _ => "" }),
        Recomendacion: recomendacion, RecomendacionEn: null, CanonicalId: canonicalId, MatrixCode: null,
        Source: null, SubscriptionId: null, SubscriptionName: suscripcion, ResourceGroup: grupo,
        ResourceName: recurso, ResourceType: tipoRecurso, AhorroAnual: ahorro,
        MonedaAhorro: ahorro is null ? null : "USD");

    private static RetiroFila Retiro(
        string clave = "anuncio-1", string? caracteristica = "Característica de prueba",
        DateOnly? fecha = null, int recursos = 1) => new(
        AnnouncementKey: clave, Caracteristica: caracteristica, FechaRetiro: fecha, Titulo: null,
        AccionRecomendada: null, RecursosAfectados: recursos);

    private static ContextoInformeValor Contexto(DateOnly corte) =>
        new(PeriodStart: corte, PeriodEnd: corte, Corte: corte, MesesParcialesForzados: null);

    /// <summary>
    /// Envoltorio de <see cref="PosturaCalculadora.Calcular"/> con los dos parámetros de seguridad
    /// gestionada externamente por defecto (false/null, el caso "no gestiona aparte"): la mayoría
    /// de los casos de este archivo no le importan a esa señal, así que la fijan solo los tests de
    /// la sección dedicada.
    /// </summary>
    private static PosturaModelo? Calcular(
        IReadOnlyList<AdvisorFila> advisor, IReadOnlyList<RetiroFila> retiros, ContextoInformeValor contexto,
        bool seguridadGestionadaExternamente = false, string? seguridadGestionadaNota = null) =>
        PosturaCalculadora.Calcular(advisor, retiros, seguridadGestionadaExternamente, seguridadGestionadaNota, contexto);

    // ================= D7: la tabla de criterio técnico suma su propio total =================

    [Fact]
    public void Bruto_no_deduplica_pero_el_realizable_es_lo_que_suman_las_lineas_visibles()
    {
        var filas = new[]
        {
            Fila(recomendacion: "Optimizar el tamaño de la VM", recurso: "vm-1", ahorro: 500m),
            // fila duplicada exacta: Advisor la repite en additional_info bajo mas de una forma
            Fila(recomendacion: "Optimizar el tamaño de la VM", recurso: "vm-1", ahorro: 500m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(1000m, modelo.AhorroBruto);      // suma las DOS filas, sin deduplicar
        Assert.Equal(500m, modelo.AhorroRealizable);   // la unica linea visible es 500
        Assert.Equal(500m, modelo.AhorroDescartado);   // la diferencia, nombrada
        Assert.Single(modelo.LineasAhorro);
        Assert.Equal(modelo.AhorroRealizable, modelo.LineasAhorro.Where(l => l.Contada).Sum(l => l.Monto));
    }

    [Fact]
    public void Con_tres_recomendaciones_de_reserva_en_la_misma_suscripcion_ninguna_se_descarta()
    {
        // El bug que describe el plan: hoy se compara el monto de UNA linea contra la SUMA de
        // las de reserva de la suscripcion, asi que con 2+ recomendaciones ninguna la iguala y
        // las tres salen "descartadas" aunque las tres cuentan para el total.
        var filas = new[]
        {
            Fila(recomendacion: "Comprar una reserva de 1 año para cómputo", recurso: "vm-1", ahorro: 100m),
            Fila(recomendacion: "Comprar una reserva de 1 año para SQL", recurso: "vm-2", ahorro: 150m),
            Fila(recomendacion: "Comprar una reserva de 3 años para almacenamiento", recurso: "vm-3", ahorro: 250m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.All(modelo.LineasAhorro, l => Assert.True(l.Contada));
        Assert.Equal(500m, modelo.AhorroRealizable);
        Assert.Equal(0m, modelo.AhorroDescartado);
    }

    [Fact]
    public void Reserva_y_savings_plan_de_la_misma_suscripcion_solo_cuenta_el_mayor()
    {
        var filas = new[]
        {
            Fila(recomendacion: "Comprar una reserva de 1 año", recurso: "vm-1", ahorro: 400m),
            Fila(recomendacion: "Suscribir un Savings Plan de cómputo", recurso: "vm-2", ahorro: 700m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        var reserva = modelo.LineasAhorro.Single(l => l.Tipo == "RI");
        var savingsPlan = modelo.LineasAhorro.Single(l => l.Tipo == "SP");
        Assert.False(reserva.Contada);
        Assert.True(savingsPlan.Contada);
        Assert.Equal(700m, modelo.AhorroRealizable); // el mayor de los dos, no la suma (1100)
        Assert.Equal(400m, modelo.AhorroDescartado);
    }

    [Fact]
    public void Empate_entre_reserva_y_savings_plan_lo_resuelve_la_reserva()
    {
        // Con una sola linea por tipo y montos iguales, la plantilla marcaria las DOS como "ok"
        // (cada l[2] individual coincide con Math.max(ri,sp)) aunque el total solo cuenta una vez.
        // La calculadora elige un solo tipo por suscripcion de forma explicita: empate lo gana RI.
        var filas = new[]
        {
            Fila(recomendacion: "Comprar una reserva de 1 año", recurso: "vm-1", ahorro: 300m),
            Fila(recomendacion: "Suscribir un Savings Plan de cómputo", recurso: "vm-2", ahorro: 300m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.True(modelo.LineasAhorro.Single(l => l.Tipo == "RI").Contada);
        Assert.False(modelo.LineasAhorro.Single(l => l.Tipo == "SP").Contada);
        Assert.Equal(300m, modelo.AhorroRealizable); // no 600: es un empate, se cuenta una sola vez
    }

    [Fact]
    public void Reserva_y_savings_plan_de_distintas_suscripciones_no_compiten_entre_si()
    {
        var filas = new[]
        {
            Fila(recomendacion: "Comprar una reserva de 1 año", suscripcion: "Sub A", recurso: "vm-1", ahorro: 200m),
            Fila(recomendacion: "Suscribir un Savings Plan", suscripcion: "Sub B", recurso: "vm-2", ahorro: 500m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.All(modelo.LineasAhorro, l => Assert.True(l.Contada));
        Assert.Equal(700m, modelo.AhorroRealizable);
    }

    [Theory]
    [InlineData("Comprar una Reserved Instance de 3 años", "RI")]
    [InlineData("Comprar una reserva de 1 año para cómputo", "RI")]
    [InlineData("Suscribir un Savings Plan de cómputo", "SP")]
    [InlineData("Optimizar el tamaño de la máquina virtual", "OTRO")]
    public void El_tipo_de_linea_reconoce_reserva_en_espanol_e_ingles_y_savings_plan(
        string recomendacion, string tipoEsperado)
    {
        var modelo = Calcular(
            [Fila(recomendacion: recomendacion, ahorro: 100m)], [], Contexto(Corte))!;

        Assert.Equal(tipoEsperado, modelo.LineasAhorro.Single().Tipo);
    }

    [Fact]
    public void Las_filas_sin_ahorro_positivo_no_entran_a_la_tabla_ni_al_bruto()
    {
        var filas = new[]
        {
            Fila(recurso: "vm-1", ahorro: null),
            Fila(recurso: "vm-2", ahorro: 0m),
            Fila(recurso: "vm-3", ahorro: -50m),
            Fila(recurso: "vm-4", ahorro: 100m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(1, modelo.ConAhorroCuantificado);
        Assert.Equal(100m, modelo.AhorroBruto);
        Assert.Single(modelo.LineasAhorro);
    }

    [Fact]
    public void ConAhorroCuantificado_cuenta_filas_crudas_no_lineas_deduplicadas()
    {
        var filas = new[]
        {
            Fila(recomendacion: "Rec", recurso: "vm-1", ahorro: 500m),
            Fila(recomendacion: "Rec", recurso: "vm-1", ahorro: 500m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(2, modelo.ConAhorroCuantificado); // dos filas
        Assert.Single(modelo.LineasAhorro);             // una linea deduplicada
    }

    [Fact]
    public void Las_lineas_de_ahorro_se_ordenan_de_mayor_a_menor_monto()
    {
        var filas = new[]
        {
            Fila(recomendacion: "A", recurso: "vm-1", ahorro: 100m),
            Fila(recomendacion: "B", recurso: "vm-2", ahorro: 900m),
            Fila(recomendacion: "C", recurso: "vm-3", ahorro: 400m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal([900m, 400m, 100m], modelo.LineasAhorro.Select(l => l.Monto));
    }

    [Fact]
    public void El_compromiso_por_suscripcion_solo_incluye_suscripciones_con_reserva_o_savings_plan()
    {
        var filas = new[]
        {
            Fila(recomendacion: "Optimizar el tamaño de la VM", suscripcion: "Sub Sin Compromiso",
                recurso: "vm-1", ahorro: 100m),
            Fila(recomendacion: "Comprar una reserva de 1 año", suscripcion: "Sub Con Reserva",
                recurso: "vm-2", ahorro: 200m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.False(modelo.CompromisoPorSuscripcion.ContainsKey("Sub Sin Compromiso"));
        Assert.Equal(200m, modelo.CompromisoPorSuscripcion["Sub Con Reserva"].Reserva);
        Assert.Equal(0m, modelo.CompromisoPorSuscripcion["Sub Con Reserva"].SavingsPlan);
    }

    // ============ D8: categoría e impacto salen de los campos numéricos, nunca de texto ============

    [Fact]
    public void Alto_medio_y_bajo_se_cuentan_por_impact_number_nunca_por_el_texto()
    {
        // Impacto (texto) queda deliberadamente "mal puesto": si el conteo comparara contra el
        // texto en vez del numero, este caso daria high=1 (por el texto "High") en vez de bajo=1
        // (por ImpactNumber=3, que es la fuente de verdad).
        var fila = Fila(impacto: 3, textoImpacto: "High");

        var modelo = Calcular([fila], [], Contexto(Corte))!;

        Assert.Equal(0, modelo.Alto);
        Assert.Equal(0, modelo.Medio);
        Assert.Equal(1, modelo.Bajo);
    }

    [Fact]
    public void Un_export_en_espanol_no_deja_los_tres_contadores_en_cero()
    {
        // El defecto que describe D8: un export en español (impacto textual "Alto"/"Medio"/"Bajo",
        // nunca "High"/"Medium"/"Low") deja high=medium=low=0 si se compara contra literales en
        // ingles, con el total en positivo. Aca el texto ESTA en español y el conteo sale correcto
        // porque nunca mira el texto.
        var filas = new[]
        {
            Fila(impacto: 1, textoImpacto: "Alto", recurso: "r1"),
            Fila(impacto: 2, textoImpacto: "Medio", recurso: "r2"),
            Fila(impacto: 3, textoImpacto: "Bajo", recurso: "r3"),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(1, modelo.Alto);
        Assert.Equal(1, modelo.Medio);
        Assert.Equal(1, modelo.Bajo);
        Assert.Equal(3, modelo.Total);
    }

    [Fact]
    public void Pilares_agrupa_por_pillar_number_y_desglosa_alto_medio_bajo_por_impact_number()
    {
        var filas = new[]
        {
            Fila(pilar: 3, nombrePilar: "Seguridad", impacto: 1, recurso: "r1"),
            Fila(pilar: 3, nombrePilar: "Seguridad", impacto: 1, recurso: "r2"),
            Fila(pilar: 3, nombrePilar: "Seguridad", impacto: 2, recurso: "r3"),
            Fila(pilar: 4, nombrePilar: "Confiabilidad", impacto: 3, recurso: "r4"),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        var seguridad = modelo.Pilares.Single(p => p.Nombre == "Seguridad");
        Assert.Equal(3, seguridad.Cantidad);
        Assert.Equal(2, seguridad.Alto);
        Assert.Equal(1, seguridad.Medio);
        Assert.Equal(0, seguridad.Bajo);

        var confiabilidad = modelo.Pilares.Single(p => p.Nombre == "Confiabilidad");
        Assert.Equal(1, confiabilidad.Cantidad);
        Assert.Equal(1, confiabilidad.Bajo);
    }

    [Fact]
    public void Una_fila_sin_impact_number_suma_a_la_cantidad_pero_no_a_ningun_balde_de_impacto()
    {
        var fila = Fila(impacto: null, textoImpacto: "");

        var modelo = Calcular([fila], [], Contexto(Corte))!;

        Assert.Equal(1, modelo.Total);
        Assert.Equal(1, modelo.Pilares.Single().Cantidad);
        Assert.Equal(0, modelo.Alto + modelo.Medio + modelo.Bajo);
    }

    [Fact]
    public void Los_pilares_se_ordenan_por_cantidad_descendente()
    {
        var filas = new[]
        {
            Fila(pilar: 1, nombrePilar: "Rendimiento", recurso: "r1"),
            Fila(pilar: 2, nombrePilar: "Excelencia operacional", recurso: "r2"),
            Fila(pilar: 2, nombrePilar: "Excelencia operacional", recurso: "r3"),
            Fila(pilar: 2, nombrePilar: "Excelencia operacional", recurso: "r4"),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal("Excelencia operacional", modelo.Pilares[0].Nombre);
        Assert.Equal("Rendimiento", modelo.Pilares[1].Nombre);
    }

    // ============ D11: la identidad de un recurso es suscripción + grupo + nombre ============

    [Fact]
    public void NumRecursos_distingue_homonimos_en_suscripciones_distintas()
    {
        var filas = new[]
        {
            Fila(suscripcion: "Sub 1", grupo: "rg-app", recurso: "vm1"),
            Fila(suscripcion: "Sub 1", grupo: "rg-app", recurso: "vm1"),
            Fila(suscripcion: "Sub 2", grupo: "rg-shared", recurso: "vm1"),
            Fila(suscripcion: "Sub 2", grupo: "rg-shared", recurso: "vm1"),
            Fila(suscripcion: "Sub 2", grupo: "rg-shared", recurso: "vm1"),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(5, modelo.Total);
        Assert.Equal(2, modelo.NumRecursos); // no 1: son dos recursos "vm1" distintos
    }

    [Fact]
    public void NumRecursos_distingue_homonimos_en_grupos_distintos_de_la_misma_suscripcion()
    {
        var filas = new[]
        {
            Fila(suscripcion: "Sub 1", grupo: "rg-a", recurso: "vm1"),
            Fila(suscripcion: "Sub 1", grupo: "rg-b", recurso: "vm1"),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(2, modelo.NumRecursos);
    }

    [Fact]
    public void NumRecursos_no_cuenta_las_filas_sin_nombre_de_recurso()
    {
        var filas = new[]
        {
            Fila(recurso: null),
            Fila(recurso: "  "),
            Fila(recurso: "vm1"),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(3, modelo.Total);       // las tres cuentan como recomendacion activa
        Assert.Equal(1, modelo.NumRecursos); // pero solo una tiene recurso
    }

    /// <summary>
    /// D11 completo (ya no una limitación documentada: RecomendacionesConRecurso es un campo del
    /// contrato desde la revisión del encargo). Total sigue contando TODAS las filas —headline "N
    /// recomendaciones activas" y coherencia con Alto+Medio+Bajo, que tampoco se restringen—, pero
    /// RecomendacionesConRecurso es el numerador correcto para "cada recurso acumula X
    /// recomendaciones en promedio": D11 pide explícitamente restringirlo a filas con recurso.
    /// </summary>
    [Fact]
    public void Total_cuenta_todas_las_filas_pero_RecomendacionesConRecurso_se_restringe_a_las_que_tienen_recurso()
    {
        var filas = new[] { Fila(recurso: null, impacto: 1), Fila(recurso: "vm1", impacto: 2) };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(2, modelo.Total);
        Assert.Equal(1, modelo.NumRecursos);
        Assert.Equal(1, modelo.RecomendacionesConRecurso); // no 2: la fila sin recurso queda afuera
        // las DOS filas cuentan en el desglose de impacto (una Alto, una Medio): la fila sin
        // recurso participa en Alto/Medio/Bajo igual que las demas, solo queda afuera del numerador.
        Assert.Equal(2, modelo.Alto + modelo.Medio);
    }

    /// <summary>
    /// El ejemplo completo de D11: 6 filas, 5 con recurso repartidas en 2 recursos distintos (por
    /// la terna, D11) y 1 sin recurso (alcance de suscripción, no de un recurso concreto).
    /// Total/NumRecursos daría 6/2=3.0 (sobreestimado, cuenta la fila sin recurso en el
    /// numerador). RecomendacionesConRecurso/NumRecursos da 5/2=2.5, el promedio real.
    /// </summary>
    [Fact]
    public void RecomendacionesConRecurso_dividido_NumRecursos_da_el_promedio_correcto()
    {
        var filas = new[]
        {
            Fila(suscripcion: "Sub 1", grupo: "rg-app", recurso: "vm1"),
            Fila(suscripcion: "Sub 1", grupo: "rg-app", recurso: "vm1"),
            Fila(suscripcion: "Sub 2", grupo: "rg-shared", recurso: "vm1"),
            Fila(suscripcion: "Sub 2", grupo: "rg-shared", recurso: "vm1"),
            Fila(suscripcion: "Sub 2", grupo: "rg-shared", recurso: "vm1"),
            Fila(recurso: null),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(6, modelo.Total);
        Assert.Equal(2, modelo.NumRecursos);
        Assert.Equal(5, modelo.RecomendacionesConRecurso);
        Assert.Equal(2.5, (double)modelo.RecomendacionesConRecurso / modelo.NumRecursos);
        Assert.Equal(3.0, (double)modelo.Total / modelo.NumRecursos); // el numerador incorrecto, para contraste
    }

    // ============ D13: fechas resueltas contra el corte, nunca contra el reloj ============

    [Fact]
    public void Los_retiros_se_clasifican_contra_el_corte_declarado_no_contra_el_reloj()
    {
        // El corte esta deliberadamente en 2031 (lejos del "hoy" real) y la fecha de retiro es 31
        // dias ANTES de ese corte: con el parametro correcto, el retiro esta VENCIDO. Si el calculo
        // usara el reloj del sistema (hoy real, ~2026), esa misma fecha de retiro estaria a mas de
        // 1500 dias en el FUTURO y saldria "Plazo largo": el resultado opuesto.
        var corteLejano = new DateOnly(2031, 1, 1);
        var fechaDeRetiro = new DateOnly(2030, 12, 1);

        var modelo = Calcular([], [Retiro(fecha: fechaDeRetiro)], Contexto(corteLejano))!;

        Assert.True(modelo.Retiros.Single().Vencido);
    }

    [Fact]
    public void Un_retiro_sin_fecha_declarada_no_esta_vencido_ni_proximo()
    {
        var modelo = Calcular([], [Retiro(fecha: null)], Contexto(Corte))!;

        var retiro = modelo.Retiros.Single();
        Assert.Null(retiro.FechaRetiro);
        Assert.Equal("Sin fecha declarada.", retiro.Situacion);
        Assert.False(retiro.Vencido);
        Assert.False(retiro.ProximoATresMeses);
    }

    [Fact]
    public void Retiro_vencido_cuando_la_fecha_es_anterior_al_corte()
    {
        var modelo = Calcular([], [Retiro(fecha: Corte.AddDays(-1))], Contexto(Corte))!;

        var retiro = modelo.Retiros.Single();
        Assert.True(retiro.Vencido);
        Assert.False(retiro.ProximoATresMeses);
        Assert.StartsWith("VENCIDO", retiro.Situacion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Divergencia deliberada frente a la plantilla (con su ejemplo numerico en el informe de la
    /// Tarea 6): el JavaScript compara una marca de tiempo UTC-medianoche contra el INSTANTE actual
    /// (<c>Date.now()</c>), asi que una fecha de retiro de "hoy" sale VENCIDA casi siempre (la
    /// medianoche ya paso salvo que se ejecute a las 00:00:00.000 exactas). La calculadora compara
    /// DATE-ONLY contra DATE-ONLY: un retiro fechado exactamente en el corte todavia no vencio.
    /// </summary>
    [Fact]
    public void Retiro_en_la_fecha_de_corte_no_esta_vencido_todavia()
    {
        var modelo = Calcular([], [Retiro(fecha: Corte)], Contexto(Corte))!;

        var retiro = modelo.Retiros.Single();
        Assert.False(retiro.Vencido);
        Assert.True(retiro.ProximoATresMeses);
    }

    [Fact]
    public void Retiro_a_91_dias_cuenta_como_proximo_a_tres_meses()
    {
        var modelo = Calcular([], [Retiro(fecha: Corte.AddDays(91))], Contexto(Corte))!;

        Assert.True(modelo.Retiros.Single().ProximoATresMeses);
    }

    [Fact]
    public void Retiro_a_92_dias_exactos_ya_no_cuenta_como_proximo_a_tres_meses()
    {
        var modelo = Calcular([], [Retiro(fecha: Corte.AddDays(92))], Contexto(Corte))!;

        var retiro = modelo.Retiros.Single();
        Assert.False(retiro.ProximoATresMeses);
        Assert.Equal("Menos de un año de margen.", retiro.Situacion);
    }

    [Fact]
    public void Retiro_a_366_dias_exactos_ya_es_plazo_largo()
    {
        var modelo = Calcular([], [Retiro(fecha: Corte.AddDays(366))], Contexto(Corte))!;

        Assert.Equal("Plazo largo. Se planifica con el ciclo de renovación.", modelo.Retiros.Single().Situacion);
    }

    [Fact]
    public void Los_retiros_sin_fecha_se_ordenan_primero_y_luego_por_fecha_ascendente()
    {
        var retiros = new[]
        {
            Retiro(clave: "c", fecha: Corte.AddDays(30)),
            Retiro(clave: "a", fecha: null),
            Retiro(clave: "b", fecha: Corte.AddDays(10)),
        };

        var modelo = Calcular([], retiros, Contexto(Corte))!;

        Assert.Equal(
            [null, Corte.AddDays(10).ToString("yyyy-MM-dd"), Corte.AddDays(30).ToString("yyyy-MM-dd")],
            modelo.Retiros.Select(r => r.FechaRetiro));
    }

    [Fact]
    public void RetirosVencidos_y_proximos_cuentan_sobre_la_clasificacion_ya_resuelta()
    {
        var retiros = new[]
        {
            Retiro(clave: "vencido", fecha: Corte.AddDays(-5)),
            Retiro(clave: "proximo", fecha: Corte.AddDays(10)),
            Retiro(clave: "lejano", fecha: Corte.AddDays(400)),
            Retiro(clave: "sin-fecha", fecha: null),
        };

        var modelo = Calcular([], retiros, Contexto(Corte))!;

        Assert.Equal(1, modelo.RetirosVencidos);
        Assert.Equal(1, modelo.RetirosProximosATresMeses);
    }

    [Fact]
    public void El_diccionario_de_compromiso_usa_el_nombre_de_suscripcion_tal_cual_como_clave()
    {
        var filas = new[]
        {
            Fila(recomendacion: "Comprar una reserva de 1 año", suscripcion: "Suscripción Producción",
                recurso: "vm-1", ahorro: 100m),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.True(modelo.CompromisoPorSuscripcion.ContainsKey("Suscripción Producción"));
    }

    /// <summary>D0 no esta entre las decisiones de la Tarea 6: Advisor y los retiros de Azure son
    /// estado ACTIVO (una foto de ahora), no un evento historico dentro de un rango. El contexto
    /// trae PeriodStart/PeriodEnd para los bloques que si filtran por periodo; este bloque los
    /// ignora a proposito.</summary>
    [Fact]
    public void Los_retiros_y_advisor_no_se_filtran_por_el_periodo_del_informe()
    {
        var contexto = new ContextoInformeValor(
            PeriodStart: new DateOnly(2020, 1, 1), PeriodEnd: new DateOnly(2020, 1, 31),
            Corte: Corte, MesesParcialesForzados: null);
        var fechaFueraDelPeriodo = Corte.AddDays(10); // muy fuera de enero 2020

        var modelo = Calcular([Fila()], [Retiro(fecha: fechaFueraDelPeriodo)], contexto)!;

        Assert.Equal(1, modelo.Total);
        Assert.Single(modelo.Retiros);
    }

    // ============ Seguridad gestionada externamente (agregado tras revisión del encargo) ============

    /// <summary>
    /// La ambigüedad que describe el encargo: un pilar de Seguridad en cero porque el cliente no
    /// tiene hallazgos de seguridad, y un pilar en cero porque pidió no verlos (Gestión de
    /// Vulnerabilidades) y AdvisorRecolector.Sql() ya los excluyó, se dibujan EXACTAMENTE igual si
    /// solo se mira Pilares: en los dos casos no hay ninguna entrada de Seguridad ahí. Estos dos
    /// campos, pasados tal cual desde InsumosBd, son la única señal que distingue los dos casos.
    /// </summary>
    [Fact]
    public void La_bandera_es_la_unica_senal_que_distingue_seguridad_en_cero_de_seguridad_oculta()
    {
        var filas = new[] { Fila(pilar: 4, nombrePilar: "Confiabilidad", recurso: "r1") }; // nunca hay pilar 3, en ninguno de los dos casos

        var sinHallazgosDeSeguridad = Calcular(filas, [], Contexto(Corte))!;
        var seguridadGestionadaAparte = Calcular(filas, [], Contexto(Corte),
            seguridadGestionadaExternamente: true, seguridadGestionadaNota: "Gestionado por Gestión de Vulnerabilidades")!;

        Assert.DoesNotContain(sinHallazgosDeSeguridad.Pilares, p => p.Nombre == "Seguridad");
        Assert.DoesNotContain(seguridadGestionadaAparte.Pilares, p => p.Nombre == "Seguridad");

        Assert.False(sinHallazgosDeSeguridad.SeguridadGestionadaExternamente);
        Assert.Null(sinHallazgosDeSeguridad.SeguridadGestionadaNota);

        Assert.True(seguridadGestionadaAparte.SeguridadGestionadaExternamente);
        Assert.Equal("Gestionado por Gestión de Vulnerabilidades", seguridadGestionadaAparte.SeguridadGestionadaNota);
    }

    // ============ Forma general: paridad con la plantilla donde no hay decision de por medio ============

    [Fact]
    public void Calcular_devuelve_null_cuando_no_hay_advisor_ni_retiros()
    {
        Assert.Null(Calcular([], [], Contexto(Corte)));
    }

    /// <summary>
    /// En la plantilla, retiros y hallazgos de Advisor vienen del MISMO archivo, asi que este caso
    /// no podia existir. En la entrega 2a, retiros sale de boletin_retirement (modulo Boletin) y
    /// Advisor de waf_resource_finding: son independientes, y un cliente puede tener retiros
    /// pendientes con el backlog de Advisor en cero.
    /// </summary>
    [Fact]
    public void Calcular_devuelve_el_bloque_de_retiros_aunque_no_haya_hallazgos_de_advisor()
    {
        var modelo = Calcular([], [Retiro()], Contexto(Corte))!;

        Assert.Equal(0, modelo.Total);
        Assert.Equal(0, modelo.NumRecursos);
        Assert.Single(modelo.Retiros);
    }

    [Fact]
    public void Calcular_devuelve_el_bloque_de_advisor_aunque_no_haya_retiros()
    {
        var modelo = Calcular([Fila()], [], Contexto(Corte))!;

        Assert.Equal(1, modelo.Total);
        Assert.Empty(modelo.Retiros);
    }

    [Fact]
    public void Suscripciones_se_agrupan_por_nombre_y_se_ordenan_de_mayor_a_menor()
    {
        var filas = new[]
        {
            Fila(suscripcion: "Sub A", recurso: "r1"),
            Fila(suscripcion: "Sub B", recurso: "r2"),
            Fila(suscripcion: "Sub B", recurso: "r3"),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(new object?[] { "Sub B", 2 }, modelo.Suscripciones[0]);
        Assert.Equal(new object?[] { "Sub A", 1 }, modelo.Suscripciones[1]);
    }

    [Fact]
    public void TiposRecurso_agrupa_mas_de_14_tipos_bajo_otros_tipos()
    {
        var filas = Enumerable.Range(0, 16)
            .Select(i => Fila(recurso: $"r{i}", tipoRecurso: $"Microsoft.Tipo{i}"))
            .ToArray();

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(15, modelo.TiposRecurso.Count); // 14 + "Otros tipos"
        Assert.Equal("Otros tipos", modelo.TiposRecurso[^1][0]);
        Assert.Equal(2, modelo.TiposRecurso[^1][1]); // los 2 que sobraron de 16
    }

    [Fact]
    public void Top_se_limita_a_15_recomendaciones_y_excluye_la_de_menor_conteo()
    {
        // 16 recomendaciones distintas con conteos 1..16 (136 filas en total). Top-15 se queda con
        // las de conteo 2..16 (suma 135) y deja afuera la de conteo 1.
        var filas = new List<AdvisorFila>();
        for (var i = 1; i <= 16; i++)
            for (var j = 0; j < i; j++)
                filas.Add(Fila(recomendacion: $"Recomendación {i}", recurso: $"r{i}-{j}"));

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(15, modelo.Top.Count);
        Assert.DoesNotContain(modelo.Top, t => (string)t[0]! == "Recomendación 1");
        Assert.Equal(135, modelo.TopSuma);
        Assert.Equal(modelo.Top.Sum(t => (int)t[3]!), modelo.TopSuma);
    }

    [Fact]
    public void Las_recomendaciones_largas_se_truncan_a_102_caracteres_mas_puntos_suspensivos()
    {
        var textoLargo = new string('x', 120);
        var modelo = Calcular([Fila(recomendacion: textoLargo, recurso: "r1")], [], Contexto(Corte))!;

        var recomendacionEnTop = (string)modelo.Top.Single()[0]!;
        Assert.Equal(105, recomendacionEnTop.Length); // 102 + "..."
        Assert.EndsWith("...", recomendacionEnTop, StringComparison.Ordinal);
    }

    [Fact]
    public void Detalle_agrupa_por_recomendacion_y_suscripcion_sin_limite_de_filas()
    {
        var filas = new[]
        {
            Fila(recomendacion: "Rec", suscripcion: "Sub A", recurso: "r1"),
            Fila(recomendacion: "Rec", suscripcion: "Sub B", recurso: "r2"),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(2, modelo.Detalle.Count); // misma recomendacion, dos suscripciones: dos filas
    }

    [Fact]
    public void TiposDeRecomendacion_cuenta_titulos_distintos()
    {
        var filas = new[]
        {
            Fila(recomendacion: "A", recurso: "r1"),
            Fila(recomendacion: "A", recurso: "r2"),
            Fila(recomendacion: "B", recurso: "r3"),
        };

        var modelo = Calcular(filas, [], Contexto(Corte))!;

        Assert.Equal(2, modelo.TiposDeRecomendacion);
    }
}
