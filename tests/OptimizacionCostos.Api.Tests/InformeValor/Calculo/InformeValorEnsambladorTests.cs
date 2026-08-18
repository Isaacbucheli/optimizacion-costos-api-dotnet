using System.Globalization;
using System.Text.Json;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 8 del plan de la entrega 2b: el ensamblador (produce <see cref="ModeloInformeValor"/>
/// desde los cinco insumos crudos, resuelve D12) más los dos checks que el plan deja explícitamente
/// sin cubrir por ningún test de C# hasta esta tarea: determinismo y el contrato de nombres contra
/// la capa de dibujo (<c>render()</c> en <c>docs/Plantilla-Dashboard-BIT.html</c>).
/// </summary>
public sealed class InformeValorEnsambladorTests
{
    private static int _n;

    private static FacturacionRow Factura(
        string? subscriptionId, string? subscriptionName, decimal pvp, int anio, int mes,
        string? categoria = "Cómputo", string? rg = "rg-1", string? recurso = "vm-1") => new(
        Hash: $"h{++_n}", Tenant: null, SubscriptionName: subscriptionName, SubscriptionId: subscriptionId,
        ResourceGroup: rg, ResourceName: recurso, CostCenter: null, Category: categoria,
        Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
        Pvp: pvp, Year: (short)anio, Month: (byte)mes);

    private static CasoRow Caso(
        string caso, int anio, int mes, int dia, string cumple = "SI", decimal sla = 8m, decimal duracion = 4m,
        string categoria = "Cómputo", string subcategoria = "Consulta", string horario = "Hábil") => new(
        Hash: $"h{++_n}", Caso: caso, FechaRegistro: new DateOnly(anio, mes, dia), Estado: "Cerrado",
        SlaHoras: sla, DuracionCruda: duracion, Cumple: cumple, Categoria: categoria,
        Subcategoria: subcategoria, Horario: horario);

    private static RbacFila Rbac(
        string id, string? subscriptionId, string? subscriptionName,
        IReadOnlyList<string>? alcanza = null, IReadOnlyList<string?>? alcanzaNombres = null,
        string rol = "Reader") => new(
        PrincipalObjectId: id, Nombre: $"Persona {id}", Login: $"{id}@cliente.com", PrincipalType: "User",
        Rol: rol, RoleKey: rol.ToLowerInvariant(), Scope: $"/subscriptions/{subscriptionId}",
        ScopeLevel: "subscription", SubscriptionId: subscriptionId, SubscriptionName: subscriptionName,
        SuscripcionesAlcanzadas: alcanza ?? (subscriptionId is not null ? [subscriptionId] : []),
        SuscripcionesAlcanzadasNombres: alcanzaNombres ?? (subscriptionName is not null ? [subscriptionName] : []),
        CuentaHabilitada: true, UltimoLoginTexto: "2026-01-01T00:00:00Z", ViaGrupoId: null,
        RoleClass: null, IsCustomRole: false);

    private static AdvisorFila Advisor(
        string? subscriptionId, string subscriptionName, string recurso = "vm-1", string grupo = "rg-1",
        int pilar = 1, string nombrePilar = "Confiabilidad", int? impacto = 1, decimal? ahorro = null,
        string recomendacion = "Recomendación") => new(
        PillarNumber: pilar, Pilar: nombrePilar, ImpactNumber: impacto,
        Impacto: impacto switch { 1 => "Alto", 2 => "Medio", 3 => "Bajo", _ => "" },
        Recomendacion: recomendacion, RecomendacionEn: null, CanonicalId: 1, MatrixCode: null,
        Source: null, SubscriptionId: subscriptionId, SubscriptionName: subscriptionName,
        ResourceGroup: grupo, ResourceName: recurso, ResourceType: "Microsoft.Compute/virtualMachines",
        AhorroAnual: ahorro, MonedaAhorro: ahorro is null ? null : "USD");

    private static RetiroFila Retiro(DateOnly? fecha = null) => new(
        AnnouncementKey: "anuncio-1", Caracteristica: "Característica de prueba", FechaRetiro: fecha,
        Titulo: null, AccionRecomendada: null, RecursosAfectados: 1);

    private static MatrizFila Matriz(string ambito = "Seguridad", int avance = 50) => new(
        CanonicalId: 1, MatrixCode: null, PillarNumber: 3, Ambito: ambito, Hallazgo: "Hallazgo de prueba",
        Fecha: null, ImpactNumber: 1, Prioridad: "1", EsfuerzoTexto: null, AvancePct: avance,
        Registro: null, ResourceCount: 1, Excluida: false);

    private static InsumosBd Insumos(
        IReadOnlyList<AdvisorFila>? advisor = null, IReadOnlyList<MatrizFila>? matriz = null,
        IReadOnlyList<RbacFila>? rbac = null, IReadOnlyList<RetiroFila>? retiros = null) => new(
        Advisor: advisor ?? [], Matriz: matriz ?? [], Rbac: rbac ?? [], Retiros: retiros ?? [],
        EstadoRbac: new EstadoRbacResultado(
            DisponibilidadRbac.Completo, new EjesRbac(EstadoCuentaMedido: true, UltimoLoginMedido: true),
            FechaCorrida: new DateTime(2026, 1, 1), Motivo: "completo"),
        SeguridadGestionadaExternamente: false, SeguridadGestionadaNota: null,
        LeidoEn: new DateTime(2026, 1, 1));

    private static ContextoInformeValor Contexto(int anio, int mesInicio, int mesFin)
    {
        var finMes = DateTime.DaysInMonth(anio, mesFin);
        var corteInstante = new DateTimeOffset(anio, mesFin, finMes, 0, 0, 0, TimeSpan.Zero);
        return new(
            PeriodStart: new DateOnly(anio, mesInicio, 1),
            PeriodEnd: new DateOnly(anio, mesFin, finMes),
            Corte: Fechas.ResolverFechaEnGuayaquil(corteInstante),
            MesesParcialesForzados: []);
    }

    // ── Entrega 7, Tarea 1: opex y cronología llegan al modelo. Mismo patrón de builders que el
    // resto de la clase, pero sobre fechas explícitas "aaaa-MM-dd" (los dos insumos nuevos son
    // registros con DateOnly/DateTime, no año/mes sueltos como el resto del fixture). ──

    /// <summary>Como <see cref="Contexto"/>, pero con el rango dado por fechas explícitas: los tres
    /// tests de esta tarea necesitan controlar el corte exacto de una serie, no solo el mes.</summary>
    private static ContextoInformeValor Ctx(string inicio, string fin) => new(
        DateOnly.ParseExact(inicio, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly.ParseExact(fin, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly.ParseExact(fin, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        null);

    /// <summary>Atajo sobre <see cref="InformeValorEnsamblador.Ensamblar"/> para los tests que solo
    /// necesitan variar <see cref="InsumosBd"/> y el contexto: sin facturación ni casos, que no
    /// pesan en ninguno de los tres tests de esta tarea.</summary>
    private static ModeloInformeValor Ensamblar(InsumosBd insumosBd, ContextoInformeValor contexto) =>
        InformeValorEnsamblador.Ensamblar([], 0, [], insumosBd, "Cliente", contexto);

    private static InsumosBd ConOpex(OpexScore opex) => Insumos() with { Opex = opex };

    private static InsumosBd ConHitos(IReadOnlyList<HitoFila> hitos) => Insumos() with { Hitos = hitos };

    /// <summary>Una fila de la bitácora de tracking con autor/matrixCode/pilar fijos (no importan
    /// para los tests de esta tarea, que solo varían fecha/campo/valores).</summary>
    private static HitoFila Hito(string fecha, string campo, string? antes, string? despues) => new(
        Fecha: DateTime.ParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        Campo: campo, ValorAnterior: antes, ValorNuevo: despues, Autor: "consultor@bit.com",
        MatrixCode: "M-1", Recomendacion: "Recomendación de prueba", PillarNumber: 3);

    // ===================================================================================
    // 5a. Determinismo: mismo insumo, misma fecha de corte, dos corridas, modelos idénticos.
    // ===================================================================================

    /// <summary>
    /// Fixture deliberadamente rica en diccionarios y conjuntos (donde el plan avisa que "se
    /// escapa" el orden): tres suscripciones de facturación en RBAC (una alcanzada solo por
    /// herencia), dos categorías de facturación, dos pilares de Advisor con reserva y savings
    /// plan en la misma suscripción (fuerza <c>CompromisoPorSuscripcion</c>), dos ámbitos de
    /// matriz. Si algo en el ensamblador iterara un <c>Dictionary</c>/<c>HashSet</c> sin un orden
    /// explícito y ese orden no fuera estable entre corridas, este fixture es donde se notaría.
    /// </summary>
    private static (
        IReadOnlyList<FacturacionRow> Facturacion, int FilasAntesDeFusionar, IReadOnlyList<CasoRow> Casos,
        InsumosBd InsumosBd, string Cliente, ContextoInformeValor Contexto) FixtureRica()
    {
        var facturacion = new List<FacturacionRow>
        {
            Factura("sub-a", "Suscripción A", 1000m, 2026, 1, categoria: "Cómputo"),
            Factura("sub-a", "Suscripción A", 1100m, 2026, 2, categoria: "Cómputo"),
            Factura("sub-b", "Suscripción B", 500m, 2026, 1, categoria: "Backup", recurso: "vm-2"),
            Factura("sub-b", "Suscripción B", 480m, 2026, 2, categoria: "Backup", recurso: "vm-2"),
            Factura(null, "(sin suscripción)", 90m, 2026, 1, categoria: null, recurso: "vm-3"),
        };
        var casos = new List<CasoRow>
        {
            Caso("C-1", 2026, 1, 5, cumple: "SI", categoria: "Cómputo", subcategoria: "Consulta"),
            Caso("C-2", 2026, 1, 10, cumple: "NO", categoria: "Backup", subcategoria: "Falla de respaldo"),
            Caso("C-3", 2026, 2, 2, cumple: "SIN EVALUAR", categoria: "Cómputo", subcategoria: ""),
        };
        var rbac = new List<RbacFila>
        {
            Rbac("owner-1", "sub-a", "Suscripción A", alcanza: ["sub-a", "sub-c"],
                alcanzaNombres: ["Suscripción A", "Suscripción Heredada"], rol: "Owner"),
            Rbac("reader-1", "sub-b", "Suscripción B"),
        };
        var advisor = new List<AdvisorFila>
        {
            Advisor("sub-a", "Suscripción A", recurso: "vm-1", pilar: 1, nombrePilar: "Confiabilidad",
                ahorro: 300m, recomendacion: "Comprar una reserva de 1 año"),
            Advisor("sub-a", "Suscripción A", recurso: "vm-4", pilar: 1, nombrePilar: "Confiabilidad",
                ahorro: 200m, recomendacion: "Suscribir un Savings Plan"),
            Advisor("sub-b", "Suscripción B", recurso: "vm-2", pilar: 3, nombrePilar: "Seguridad", impacto: 2),
        };
        var matriz = new List<MatrizFila> { Matriz("Seguridad", 40), Matriz("Confiabilidad", 90) };
        var retiros = new List<RetiroFila> { Retiro(new DateOnly(2026, 6, 1)) };

        return (
            facturacion, FilasAntesDeFusionar: facturacion.Count + 3, casos,
            Insumos(advisor, matriz, rbac, retiros), "Cliente de prueba", Contexto(2026, 1, 2));
    }

    [Fact]
    public void Recalcular_con_el_mismo_insumo_y_la_misma_fecha_de_corte_da_el_mismo_modelo()
    {
        var (facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto) = FixtureRica();

        // Dos listas DISTINTAS (no la misma referencia) con el mismo contenido: si algo mutara la
        // entrada entre corridas, o si dos corridas de la MISMA lista dieran igual pero de una
        // lista "nueva" no, este test lo notaria en vez de esconderlo por reusar la instancia.
        var modelo1 = InformeValorEnsamblador.Ensamblar(
            [.. facturacion], filasAntesDeFusionar, [.. casos], insumosBd, cliente, contexto);
        var modelo2 = InformeValorEnsamblador.Ensamblar(
            [.. facturacion], filasAntesDeFusionar, [.. casos], insumosBd, cliente, contexto);

        var json1 = JsonSerializer.Serialize(modelo1, InformeValorJsonOptions.Instance);
        var json2 = JsonSerializer.Serialize(modelo2, InformeValorJsonOptions.Instance);

        // Comparar el TEXTO json, no los objetos: los records generan igualdad estructural por
        // propiedad, pero para una propiedad de tipo lista/diccionario esa igualdad cae en
        // Object.Equals (referencia), así que dos corridas que arman listas nuevas siempre
        // "difieren" aunque el contenido sea idéntico. El JSON expone el orden real de arreglos y
        // de claves de diccionario, que es exactamente lo que el plan pide vigilar.
        Assert.Equal(json1, json2);
    }

    // ===================================================================================
    // D12: las tres cifras de suscripciones se concilian (Tarea 8, punto 2 del encargo).
    // ===================================================================================

    /// <summary>
    /// Ejemplo numérico para la lista de divergencias: facturación ve 3 suscripciones (sub-a,
    /// sub-b, "(sin suscripción)"), RBAC ve 3 (sub-a directa, sub-b directa, sub-c heredada) y
    /// Advisor ve 2 (sub-a, sub-b). Hoy el informe publicaría "3 suscripciones" en Cobertura del
    /// servicio (toma solo <c>f.subs.length</c>, D12) sin mencionar que RBAC alcanza una cuarta
    /// (sub-c) que ni facturación ni Advisor ven. El conjunto unión correcto es 4: sub-a y sub-b
    /// (las tres fuentes), sub-c (solo RBAC, heredada) y "(sin suscripción)" (solo facturación).
    /// </summary>
    [Fact]
    public void La_cobertura_publica_el_conjunto_union_declarando_que_fuente_cubre_cada_suscripcion()
    {
        var (facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto) = FixtureRica();

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto);

        var cobertura = modelo.Meta.Cobertura;
        // sub-a, sub-b, sub-c y "(sin suscripción)": union de 4, no los 3 que hoy publica
        // facturación (f.subs.length) ni los 2 de la intersección de las tres fuentes.
        Assert.Equal(4, cobertura.Total);

        var subA = cobertura.Suscripciones.Single(s => s.Id == "sub-a");
        Assert.Equal("Suscripción A", subA.Nombre);
        Assert.True(subA.Facturacion); Assert.True(subA.Rbac); Assert.True(subA.Advisor);

        var subB = cobertura.Suscripciones.Single(s => s.Id == "sub-b");
        Assert.True(subB.Facturacion); Assert.True(subB.Rbac); Assert.True(subB.Advisor);

        // sub-c: alcanzada SOLO por herencia en RBAC (nunca es la suscripcion propia de ninguna
        // fila). Antes de exponer SuscripcionesAlcanzadasNombres esto hubiera mostrado el id crudo.
        var subC = cobertura.Suscripciones.Single(s => s.Id == "sub-c");
        Assert.Equal("Suscripción Heredada", subC.Nombre);
        Assert.False(subC.Facturacion); Assert.True(subC.Rbac); Assert.False(subC.Advisor);
    }

    /// <summary>
    /// La fila de facturación sin <c>subscription_id</c> SÍ tiene nombre (el sentinela
    /// "(sin suscripción)" que ya pone el recolector/parser): D12 la concilia por ese nombre, como
    /// su propia entrada, en vez de perderla o fundirla con otra.
    /// </summary>
    [Fact]
    public void Una_fila_de_facturacion_sin_id_de_suscripcion_concilia_por_su_nombre()
    {
        var (facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto) = FixtureRica();

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto);

        var sinSuscripcion = modelo.Meta.Cobertura.Suscripciones.Single(s => s.Id == "(sin suscripción)");
        Assert.True(sinSuscripcion.Facturacion);
        Assert.False(sinSuscripcion.Rbac);
        Assert.False(sinSuscripcion.Advisor);
    }

    /// <summary>
    /// Caso límite distinto del anterior: una fila que no trae NI id NI nombre de suscripción (los
    /// dos en blanco, no solo el id) no tiene ninguna clave posible para conciliar y se excluye sin
    /// romper el resto del cálculo — no se puede "adivinar" una suscripción de la nada.
    /// </summary>
    [Fact]
    public void Una_fila_sin_id_ni_nombre_de_suscripcion_se_excluye_de_la_conciliacion()
    {
        var (facturacionBase, filasAntesDeFusionar, casos, insumosBd, cliente, contexto) = FixtureRica();
        var facturacion = facturacionBase
            .Append(Factura(subscriptionId: null, subscriptionName: null, 25m, 2026, 1))
            .ToList();

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto);

        // Sigue siendo 4 (sub-a, sub-b, sub-c, "(sin suscripción)"): la fila sin id ni nombre no
        // agrega una quinta entrada ni se cuela dentro de ninguna de las cuatro existentes.
        Assert.Equal(4, modelo.Meta.Cobertura.Total);
    }

    // ===================================================================================
    // El cable de RBAC: el informe declara de dónde salió el insumo de seguridad. Mismo caso que
    // Cobertura (D12) -- render() no lo lee, existe para la vista React de la entrega 3 y para la
    // bitácora de la entrega (spec informe_valor_entrega.rbac_origen) -- así que no participa del
    // contrato de nombres de la sección 5b.
    // ===================================================================================

    [Theory]
    [InlineData(null)]
    [InlineData(InsumosBd.OrigenBase)]
    [InlineData(InsumosBd.OrigenArchivo)]
    public void Meta_declara_el_origen_de_rbac_que_trae_InsumosBd(string? origen)
    {
        var insumos = Insumos() with { RbacOrigen = origen };

        var modelo = InformeValorEnsamblador.Ensamblar([], 0, [], insumos, "Cliente", Contexto(2026, 1, 1));

        Assert.Equal(origen, modelo.Meta.RbacOrigen);
    }

    // ===================================================================================
    // 5b. Contrato de nombres contra la capa de dibujo (render() en Plantilla-Dashboard-BIT.html).
    // Extraído por lectura directa de render() y de las cinco funciones calcXxx que definen los
    // objetos que consume (Tarea 8): cada aserción de abajo es un ".campo" que render() lee en
    // alguna parte. Los que NO se pueden cubrir así están documentados al final de la clase.
    // ===================================================================================

    private static JsonElement ModeloJson()
    {
        var (facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto) = FixtureRica();
        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto);
        var json = JsonSerializer.Serialize(modelo, InformeValorJsonOptions.Instance);
        return JsonDocument.Parse(json).RootElement;
    }

    private static void ExigeCampo(JsonElement obj, string campo) =>
        Assert.True(obj.TryGetProperty(campo, out _), $"falta el campo \"{campo}\" que render() lee");

    [Fact]
    public void D_meta_expone_los_campos_que_render_lee()
    {
        var meta = ModeloJson().GetProperty("meta");
        foreach (var campo in new[] { "cliente", "periodo", "corte" }) ExigeCampo(meta, campo);
    }

    /// <summary>
    /// D.tickets (Operación): TODOS los campos que <c>render()</c> lee de <c>t</c>, salvo <c>si</c>
    /// y <c>no</c> — ver el comentario de clase, sección "No cubierto por este test".
    /// </summary>
    [Fact]
    public void D_tickets_expone_los_campos_que_render_lee()
    {
        var t = ModeloJson().GetProperty("tickets");
        foreach (var campo in new[]
        {
            "n", "pct", "cerrados", "media", "mediana", "p90", "mediaOk", "enDias", "cats", "meses",
            "racha", "rachaCasos", "frentes", "nFrentes", "nFrentesR", "casosR", "hor", "desde",
            "hasta", "fuera", "lista",
        }) ExigeCampo(t, campo);

        var cat = t.GetProperty("cats")[0];
        foreach (var campo in new[] { "n", "c", "f", "med" }) ExigeCampo(cat, campo);
        var frente = t.GetProperty("frentes")[0];
        foreach (var campo in new[] { "n", "c", "r" }) ExigeCampo(frente, campo);
    }

    /// <summary>
    /// D.fact (Consumo): TODOS los campos que <c>render()</c> lee de <c>f</c>, salvo
    /// <c>cargaAcum</c> — ver "No cubierto por este test".
    /// </summary>
    [Fact]
    public void D_fact_expone_los_campos_que_render_lee()
    {
        var f = ModeloJson().GetProperty("fact");
        foreach (var campo in new[]
        {
            "filas", "total", "meses", "ultCompleto", "parciales", "subs", "nIds", "nRg", "picoAct",
            "picoMes", "nCats", "bajasDef", "prom", "ahorro", "cargaRet", "comp", "serie", "cc",
        }) ExigeCampo(f, campo);

        // ahorro/comp de ESTE fixture (2 meses) salen null: CalcularAhorro exige 6 meses no
        // parciales y la comparativa exige que el mismo mes del año anterior este en rango. Sus
        // campos internos se verifican aparte (ver D_ConsumoAhorro/D_ConsumoComparativa), por
        // construccion directa, para no atar el contrato de nombres a que este fixture dispare esa
        // regla de negocio en particular.
    }

    /// <summary>Campos de <c>f.ahorro</c> que <c>render()</c> lee, verificados por construcción
    /// directa (D3 exige 6 meses no parciales para que el ensamblador lo calcule; no vale la pena
    /// inflar el fixture de arriba solo para disparar esa regla).</summary>
    [Fact]
    public void D_ConsumoAhorro_expone_los_campos_que_render_lee()
    {
        var ahorro = new ConsumoAhorro("Backup", 5000m, "2025-10", 1800m, "2026-01", 3200m, 3, 38400m);
        var json = JsonSerializer.Serialize(ahorro, InformeValorJsonOptions.Instance);
        using var doc = JsonDocument.Parse(json);

        foreach (var campo in new[] { "dif", "cat", "pico", "picoMes", "fin" }) ExigeCampo(doc.RootElement, campo);
    }

    /// <summary>Campos de <c>f.comp</c> que <c>render()</c> lee, mismo motivo que
    /// <see cref="D_ConsumoAhorro_expone_los_campos_que_render_lee"/>.</summary>
    [Fact]
    public void D_ConsumoComparativa_expone_los_campos_que_render_lee()
    {
        var comp = new ConsumoComparativa("2025-01", "2026-01", [["Storage", 4000m, 3500m]]);
        var json = JsonSerializer.Serialize(comp, InformeValorJsonOptions.Instance);
        using var doc = JsonDocument.Parse(json);

        foreach (var campo in new[] { "a", "b", "filas" }) ExigeCampo(doc.RootElement, campo);
    }

    [Fact]
    public void D_rbac_expone_los_campos_que_render_lee()
    {
        var rb = ModeloJson().GetProperty("rbac");
        foreach (var campo in new[]
        {
            "n", "ids", "subs", "crit", "idsU", "idsS", "nu", "ns", "priv", "sinLogin", "sinNombre",
            "find", "spTop", "roles", "owner", "uaa",
        }) ExigeCampo(rb, campo);

        var hallazgo = rb.GetProperty("find")[0];
        foreach (var campo in new[] { "s", "t", "a", "r", "e" }) ExigeCampo(hallazgo, campo);
    }

    /// <summary>
    /// D.advisor (Postura): TODOS los campos que <c>render()</c> lee de <c>ad</c>, incluidos
    /// <c>subs</c>/<c>tipos</c> como objetos con nombre (<c>sb.n</c>/<c>sb.c</c>,
    /// <c>tp.n</c>/<c>tp.c</c>: el bug que esta tarea corrigió, ver el informe). <c>savLineas</c>
    /// se verifica por nombre en vez de por posición — ver "No cubierto por este test".
    /// </summary>
    [Fact]
    public void D_advisor_expone_los_campos_que_render_lee()
    {
        var ad = ModeloJson().GetProperty("advisor");
        foreach (var campo in new[]
        {
            "n", "nRes", "high", "subs", "topSum", "descarte", "bruto", "real", "rets", "vencidos",
            "proximos", "savLineas", "porSub", "cats", "tipos_rec", "tipos", "top", "det",
        }) ExigeCampo(ad, campo);

        var pilar = ad.GetProperty("cats")[0];
        foreach (var campo in new[] { "n", "c", "h", "m", "l" }) ExigeCampo(pilar, campo);

        // sb.n/sb.c y tp.n/tp.c: antes de la Tarea 8 estos dos eran arreglos posicionales
        // [nombre, cantidad] y "sb.n" hubiera dado undefined en render() sin cambiar una sola
        // línea de la plantilla.
        var sub = ad.GetProperty("subs")[0];
        Assert.True(sub.TryGetProperty("n", out _)); Assert.True(sub.TryGetProperty("c", out _));
        var tipo = ad.GetProperty("tipos")[0];
        Assert.True(tipo.TryGetProperty("n", out _)); Assert.True(tipo.TryGetProperty("c", out _));

        var retiro = ad.GetProperty("rets")[0];
        foreach (var campo in new[] { "f", "d", "c", "est" }) ExigeCampo(retiro, campo);

        // porSub[clave].ri/.sp: la clave es el nombre de suscripcion (D13), el valor es un objeto.
        var porSub = ad.GetProperty("porSub");
        var primeraSuscripcionConCompromiso = porSub.EnumerateObject().First().Value;
        Assert.True(primeraSuscripcionConCompromiso.TryGetProperty("ri", out _));
        Assert.True(primeraSuscripcionConCompromiso.TryGetProperty("sp", out _));

        // savLineas: NUEVO contrato por nombre (D7). rec/sub/monto son el equivalente con nombre
        // de lo que el l[0]/l[1]/l[2] posicional de render() leía: ver "No cubierto" más abajo.
        var linea = ad.GetProperty("savLineas")[0];
        foreach (var campo in new[] { "rec", "sub", "monto", "tipo", "contada" }) ExigeCampo(linea, campo);
    }

    [Fact]
    public void D_matriz_expone_los_campos_que_render_lee()
    {
        var mz = ModeloJson().GetProperty("matriz");
        foreach (var campo in new[] { "n", "amb", "cerrados", "curso", "sinIniciar", "avance", "items", "horas" })
            ExigeCampo(mz, campo);

        var item = mz.GetProperty("items")[0];
        foreach (var campo in new[] { "a", "t", "f", "i", "p", "e", "v", "n", "g" }) ExigeCampo(item, campo);
        var ambito = mz.GetProperty("amb")[0];
        foreach (var campo in new[] { "n", "c", "rec", "av" }) ExigeCampo(ambito, campo);
    }

    [Fact]
    public void D_catSerie_es_un_diccionario_de_diccionarios_por_categoria_y_mes()
    {
        var catSerie = ModeloJson().GetProperty("catSerie");
        Assert.True(catSerie.TryGetProperty("Cómputo", out var porMes));
        Assert.True(porMes.TryGetProperty("2026-01", out _));
    }

    // ===================================================================================
    // Tarea 6 de la entrega 6: D.ejecutado, la octava clave. Regla dura del encargo: con los
    // insumos nuevos (registroBarrido/evolucion) en null y sin foto de reservas medida, Ejecutado
    // sale null y ningún test de arriba —escrito antes de esta tarea— cambia de comportamiento.
    // ===================================================================================

    private static BarridoResueltoFila BarridoFila(
        string checkId, string subscriptionId, string resourceGroup, string resourceName, DateTime resueltoEn,
        decimal? estimatedMonthlySavings = null) => new(
        CheckId: checkId, SubscriptionId: subscriptionId,
        AzureResourceId: $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{resourceName}",
        ResourceName: resourceName, ResourceType: "microsoft.compute/virtualmachines",
        EstimatedMonthlySavings: estimatedMonthlySavings, Currency: "USD", ResueltoEn: resueltoEn,
        ResueltoPor: "consultor@bit.com", ResolvedByKind: "manual", Notas: null);

    /// <summary>La regla dura: sin <c>registroBarrido</c> ni <c>evolucion</c>, y sin una foto de
    /// reservas ya medida (no se le pasa <c>fotoReservas</c>), <see cref="ModeloInformeValor.Ejecutado"/>
    /// sale null — misma semántica que los demás bloques ausentes, no un objeto "vacío" que simule
    /// que sí se midió.</summary>
    [Fact]
    public void Sin_registro_de_barrido_ni_foto_de_reservas_medida_ejecutado_sale_null()
    {
        var (facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto) = FixtureRica();

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto);

        Assert.Null(modelo.Ejecutado);
    }

    /// <summary>
    /// Con un <c>registroBarrido</c> mínimo (una fila resuelta, sin foto de reservas) el ensamblador
    /// encadena T4→T3→T5: <see cref="RegistroEjecutadoCalculador"/> produce la fila y
    /// <see cref="AcumuladoCalculador"/> arma la serie sobre el rango del contexto (enero-febrero de
    /// <see cref="FixtureRica"/>). El recurso resuelto es <c>vm-1</c> de <c>sub-a</c>, el mismo que
    /// factura en el fixture, pero como Enero ES su mes de ejecución no hay mes "antes" del que sacar
    /// un delta (Regla 4 de <c>RegistroEjecutadoCalculador</c>): el monto sale del estimado del
    /// barrido, no de la factura, y viaja sin fecha de fin, así que queda vigente los dos meses del
    /// rango.
    /// </summary>
    [Fact]
    public void Con_un_registro_de_barrido_minimo_ejecutado_trae_filas_y_serie_consistente()
    {
        var (facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto) = FixtureRica();
        var registroBarrido = new RegistroBarrido(true, null,
            [BarridoFila("orphaned_disks", "sub-a", "rg-1", "vm-1", new DateTime(2026, 1, 15), estimatedMonthlySavings: 50m)]);

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto, registroBarrido: registroBarrido);

        Assert.NotNull(modelo.Ejecutado);
        var ejecutado = modelo.Ejecutado!;
        Assert.True(ejecutado.Medido);
        var fila = Assert.Single(ejecutado.Filas);
        Assert.Equal("barrido", fila.Fuente);
        Assert.Equal(50m, fila.MontoMensual);
        Assert.Equal("estimado", fila.FuenteMonto);

        // Serie: un punto por mes del rango (2026-01, 2026-02), sin fecha de fin la fila queda
        // vigente en los dos y el acumulado sube 50 cada mes.
        Assert.Equal(2, ejecutado.Serie.Count);
        Assert.Equal(new object?[] { "2026-01", 50m, 50m }, ejecutado.Serie[0]);
        Assert.Equal(new object?[] { "2026-02", 50m, 100m }, ejecutado.Serie[1]);
        Assert.Equal(100m, ejecutado.AcumuladoTotal);
        Assert.Equal(50m, ejecutado.TasaVigenteCierre);
    }

    // ===================================================================================
    // Tarea 8 de la entrega 6: la conciliación entre los dos archivos de BITCOST (spec, "Reglas
    // de convivencia entre los dos archivos"). La tabla de hechos manda para identidad; el archivo
    // de evolución, para reservas. Si los totales por mes divergen más allá del umbral, el informe
    // declara la discrepancia con la cifra de cada fuente en vez de promediar o elegir en silencio.
    // ===================================================================================

    private static EvolucionRow Evolucion(
        decimal pvp, int anio, int mes, bool esReserva = false, string recurso = "vm-1",
        string? categoria = "Cómputo") => new(
        NaturalKeyHash: $"h{++_n}", Category: categoria, Subcategory: null, ResourceName: recurso,
        IsReservation: esReserva, Pvp: pvp, PeriodYear: (short)anio, PeriodMonth: (byte)mes);

    [Fact]
    public void Sin_evolucion_cargada_la_conciliacion_sale_null()
    {
        var (facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto) = FixtureRica();

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar, casos, insumosBd, cliente, contexto);

        Assert.Null(modelo.Meta.Conciliacion);
    }

    [Fact]
    public void Con_los_mismos_totales_por_mes_la_conciliacion_coincide_sin_filas()
    {
        var facturacion = new List<FacturacionRow> { Factura("sub-a", "Suscripción A", 20000m, 2026, 6) };
        var evolucion = new List<EvolucionRow> { Evolucion(20000m, 2026, 6) };
        var contexto = Contexto(2026, 6, 6);

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, 0, [], Insumos(), "Cliente", contexto, evolucion: evolucion);

        Assert.NotNull(modelo.Meta.Conciliacion);
        var conciliacion = modelo.Meta.Conciliacion!;
        Assert.True(conciliacion.Coincide);
        Assert.Empty(conciliacion.Diferencias);
    }

    [Fact]
    public void Un_mes_con_diferencia_sobre_el_umbral_se_declara_con_la_cifra_de_cada_fuente()
    {
        var facturacion = new List<FacturacionRow> { Factura("sub-a", "Suscripción A", 20000m, 2026, 6) };
        var evolucion = new List<EvolucionRow> { Evolucion(19000m, 2026, 6) };
        var contexto = Contexto(2026, 6, 6);

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, 0, [], Insumos(), "Cliente", contexto, evolucion: evolucion);

        var conciliacion = modelo.Meta.Conciliacion!;
        Assert.False(conciliacion.Coincide);
        var fila = Assert.Single(conciliacion.Diferencias);
        Assert.Equal(new object?[] { "2026-06", 20000m, 19000m, 1000m }, fila);
    }

    /// <summary>
    /// El umbral es POR MES, con piso de $1: <c>max(1.00, 0.5% del total de hechos de ESE mes)</c>.
    /// Con hechos en $100 el 0.5% da $0.50, por debajo del piso — el umbral real que se aplica es
    /// $1.00, y una diferencia de $0.80 queda debajo. Si el piso no existiera, el 0.5% puro habría
    /// dejado pasar esta misma diferencia a la lista.
    /// </summary>
    [Fact]
    public void Una_diferencia_bajo_el_umbral_no_se_declara()
    {
        var facturacion = new List<FacturacionRow> { Factura("sub-a", "Suscripción A", 100m, 2026, 6) };
        var evolucion = new List<EvolucionRow> { Evolucion(99.20m, 2026, 6) };
        var contexto = Contexto(2026, 6, 6);

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, 0, [], Insumos(), "Cliente", contexto, evolucion: evolucion);

        var conciliacion = modelo.Meta.Conciliacion!;
        Assert.True(conciliacion.Coincide);
        Assert.Empty(conciliacion.Diferencias);
    }

    /// <summary>
    /// Un mes presente SOLO en evolución (sin filas de facturación ese mes) produce una fila de
    /// diferencia: dif = hechos - evolución = 0 - 5000 = -5000. Caso espejo: un mes con hechos
    /// pero sin evolución (p. ej. 20000 en hechos, 0 en evolución) produce dif = 20000 - 0 = 20000.
    /// Ambos casos superan el umbral (que en ambos es max(1.00, 0.5% de hechos de ese mes) —
    /// cuando hechos es 0, el umbral queda en $1 y -5000 lo supera; cuando evolución es 0, 0.5%
    /// de 20000 es 100 y 20000 también lo supera) así que las dos filas entran a Diferencias y
    /// Coincide=false.
    /// </summary>
    [Fact]
    public void La_union_de_meses_de_facturacion_y_evolucion_produce_diferencias()
    {
        var facturacion = new List<FacturacionRow> { Factura("sub-a", "Suscripción A", 20000m, 2026, 6) };
        var evolucion = new List<EvolucionRow> { Evolucion(5000m, 2026, 7) };
        var contexto = Contexto(2026, 6, 7);

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, 0, [], Insumos(), "Cliente", contexto, evolucion: evolucion);

        var conciliacion = modelo.Meta.Conciliacion!;
        Assert.False(conciliacion.Coincide);
        Assert.Equal(2, conciliacion.Diferencias.Count);

        // Mes 2026-06: hechos 20000, evolución 0 → dif = 20000 − 0 = 20000
        var filaJunio = conciliacion.Diferencias[0];
        Assert.Equal("2026-06", filaJunio[0]);
        Assert.Equal(20000m, filaJunio[1]);
        Assert.Equal(0m, filaJunio[2]);
        Assert.Equal(20000m, filaJunio[3]);

        // Mes 2026-07: hechos 0, evolución 5000 → dif = 0 − 5000 = −5000
        var filaJulio = conciliacion.Diferencias[1];
        Assert.Equal("2026-07", filaJulio[0]);
        Assert.Equal(0m, filaJulio[1]);
        Assert.Equal(5000m, filaJulio[2]);
        Assert.Equal(-5000m, filaJulio[3]);
    }

    // ===================================================================================
    // Entrega 7, Tarea 1: opex y cronología llegan al modelo publicado. Antes de esta tarea
    // InsumosBd.Opex/.Hitos no llegaban a ninguna parte de ModeloInformeValor.
    // ===================================================================================

    /// <summary>La serie de Opex se recorta al rango: el recolector la trae completa (D0).</summary>
    [Fact]
    public void La_serie_de_opex_se_recorta_al_rango_del_informe()
    {
        var opex = new OpexScore(92m, new DateOnly(2026, 6, 30), "ok",
            [new OpexPunto(new DateOnly(2025, 9, 1), 59m), new OpexPunto(new DateOnly(2026, 3, 1), 80m)],
            Medido: true, Motivo: null);
        var m = Ensamblar(ConOpex(opex), Ctx("2026-01-01", "2026-06-30"));
        Assert.Single(m.Opex!.Serie);
        Assert.Equal("2026-03", m.Opex.Serie[0][0]);
    }

    /// <summary>Las notas internas y la bitácora de ejecución jamás llegan al artefacto, y lo
    /// omitido se cuenta para que una cronología corta no se lea como "no pasó nada".</summary>
    [Fact]
    public void La_cronologia_deja_fuera_los_campos_internos_y_los_cuenta()
    {
        var hitos = new[]
        {
            Hito("2026-03-10", "completion_pct", "0", "40"),
            Hito("2026-03-11", "internal_notes", null, "acordado por teléfono con el cliente"),
            Hito("2026-03-12", "execution_log", null, "corrida manual del script"),
        };
        var m = Ensamblar(ConHitos(hitos), Ctx("2026-01-01", "2026-06-30"));
        Assert.Single(m.Cronologia!.Hitos);
        Assert.Equal("completion_pct", m.Cronologia.Hitos[0].Campo);
        Assert.Equal(2, m.Cronologia.Omitidos);
    }

    /// <summary>Sin insumos nuevos, las dos claves viajan null: bloque ausente ≠ bloque vacío.</summary>
    [Fact]
    public void Sin_opex_ni_hitos_las_dos_claves_son_null()
    {
        var m = Ensamblar(Insumos(), Ctx("2026-01-01", "2026-06-30"));
        Assert.Null(m.Opex);
        Assert.Null(m.Cronologia);
    }

    // ===================================================================================
    // No cubierto por este test (según pide el punto 5 del encargo): tres nombres/formas que
    // render(), TAL CUAL ESTÁ HOY (sin el patch que le corresponde a la entrega 3), no puede leer
    // desde este modelo. Los tres son consecuencia DIRECTA de una decisión ya tomada (D2, D4, D7),
    // no un olvido de esta tarea:
    //
    // 1. "tickets.si" / "tickets.no": D2 sustituyó el binario "cumple SLA sí/no" por tres estados
    //    explícitos (cumple/noCumple/sinEvaluar) justamente porque el binario original mezclaba
    //    "no cumple" con "no se evaluó". No hay un valor único en el modelo nuevo que sea
    //    correcto publicar bajo el nombre viejo "si"/"no" sin reintroducir esa mezcla.
    // 2. "fact.cargaAcum": D4 declaró que esta cifra desaparece, no se renombra (es la segunda
    //    definición, incompatible, de "carga retirada" que traía la plantilla). No hay ningún
    //    campo del modelo nuevo al que este nombre le corresponda.
    // 3. "advisor.savLineas[].0/.1/.2" (acceso posicional): D7 cambió la forma de savLineas de
    //    arreglo a objeto con nombre para poder llevar el veredicto "contada" junto a cada línea.
    //    No es que falte un nombre: la propia noción de "nombre" no aplica al acceso posicional
    //    que hace render() hoy. rec/sub/monto (ya cubiertos arriba) son el equivalente correcto,
    //    pero llegar a ellos exige que render() cambie de l[0]/l[1]/l[2] a l.rec/l.sub/l.monto.
    //
    // Los tres son trabajo de la entrega 3 (parchear render() para consumir el modelo nuevo, no
    // reproducir el defecto viejo), documentado también en el informe de esta tarea.
    // ===================================================================================
}
