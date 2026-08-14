using System.Globalization;
using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Tarea 8 del plan de la entrega 2b: produce el <see cref="ModeloInformeValor"/> completo a
/// partir de los cinco insumos ya leídos (nunca vuelve a tocar la base ni ningún recolector: eso
/// es responsabilidad de quien llama, ver <c>InformeValorController</c>) más el nombre del cliente
/// y el contexto ya resuelto. Función pura, estática, sin reloj: vive en <c>Calculo</c> —no en un
/// namespace propio de orquestación— justo por eso, para que <c>SinRelojDelSistemaTests</c> siga
/// cubriendo el ensamblador tal como su propio comentario de clase lo anticipaba, y para que el
/// test de determinismo de la Tarea 8 pueda llamarla dos veces sin base de datos ni mocks.
///
/// <para>Llama a los cinco bloques con la convención ya unificada (Tarea 8, punto 1 del encargo):
/// los tres que reciben <see cref="ContextoInformeValor"/> lo reciben último.</para>
///
/// <para>Resuelve D12 (las tres cifras de suscripciones se concilian) y construye
/// <see cref="InformeValorMeta.Cobertura"/>: ningún bloque individual lo hace, porque la
/// conciliación cruza facturación, RBAC y Advisor a la vez (ver los comentarios de clase de
/// <see cref="SeguridadModelo"/> y <see cref="PosturaModelo"/>, que dejan esto explícitamente para
/// acá). También construye <c>D.catSerie</c>: es una clave de nivel superior del modelo, hermana
/// de <c>D.fact</c>, no un campo dentro de <see cref="ConsumoModelo"/> — la misma razón por la que
/// la plantilla original la calcula con una función <c>catSerie()</c> separada de
/// <c>calcFact</c>, aunque lea el mismo insumo.</para>
///
/// <para><b>Tarea 5 de la entrega 2d: ensambla los tres baldes de la atribución dentro de
/// <c>fact.variacionConsumo</c></b> (ver <see cref="VariacionConsumoModelo"/> y el comentario de
/// clase de <see cref="AhorroReservasCalculador"/> para la costura de E9). El orden que deja escrito
/// el implementador de los baldes 2 y 3, respetado acá:
/// <list type="number">
/// <item>Corre <see cref="ConsumoCalculador.Calcular"/> primero y le pasa
/// <see cref="ConsumoModelo.MesesParciales"/> a <see cref="AtribucionCalculador.Calcular"/> (y a
/// <see cref="AhorroReservasCalculador.Calcular"/>): ningún bloque de la ventana fija vuelve a
/// detectar meses parciales, los reciben ya resueltos, así nunca pueden discrepar entre sí sobre qué
/// mes es parcial.</item>
/// <item>Construye el conjunto de recursos con reserva confirmada que además explica algo DENTRO
/// del período (<see cref="AhorroReservasModelo.RecursosQueExplicanElPeriodo"/>, ya filtrado por
/// E9 — nunca "confirmada" a secas) y lo pasa a <see cref="AtribucionCalculador.Calcular"/>, que
/// excluye esos recursos de los baldes 2 y 3.</item>
/// <item>La variación total del informe (<see cref="VariacionConsumoModelo.VariacionTotal"/>) es la
/// suma de los tres baldes YA redondeados (E1): balde 1 + <c>PorRecomendacion.Total</c> +
/// <c>SinAtribuir.Total</c>.</item>
/// </list>
/// Dentro del modelo el bloque cuelga de <c>fact</c>, así que solo se arma cuando hay bloque de
/// consumo (<paramref name="facturacion"/> con filas en rango): sin él no hay dónde ponerlo — mismo
/// filtro D0 que ya usa <see cref="ConsumoCalculador.Calcular"/>. <see cref="EnsamblarVariacionConsumo"/>,
/// que lo devuelve suelto, sí lo arma igual: el panel de reservas de E5 no depende de la
/// facturación.</para>
///
/// <para><b><see cref="FotoReservas"/> es IO (Tarea 1), y llega en una segunda llamada.</b> Leer las
/// reservas cuesta una llamada a Consumption POR reserva activa, en secuencia, así que
/// <c>InformeValorController.Preview</c> no la captura: devuelve el informe con el eje de reservas
/// declarado "no medido" (<see cref="FotoReservasPedidaAparte"/>) y el bloque completo de la
/// variación se vuelve a pedir aparte, contra <c>/preview/variacion-consumo</c>, que sí paga esa
/// lectura y llama a <see cref="EnsamblarVariacionConsumo"/>. Es la misma carga en dos fases que ya
/// usa la pantalla de reservas del producto (primero la lista, después la utilización). Por eso
/// <paramref name="fotoReservas"/> sigue siendo opcional: sin ella el eje sale no medido con su
/// motivo, que es un estado que este módulo ya tenía (un cliente sin credenciales de Azure activas
/// cae exactamente ahí), no uno nuevo.</para>
///
/// <para><b>Tarea 6 de la entrega 6: arma <c>D.ejecutado</c>, la octava clave (ver el comentario de
/// clase de <see cref="ModeloInformeValor"/> para por qué es de nivel superior).</b> Encadena T3→T4→T5:
/// <see cref="ReservasFacturadasCalculador"/> (Tarea 3) primero, porque <see cref="RegistroEjecutadoCalculador"/>
/// (Tarea 4) necesita su salida para atribuirle el ahorro de una VM cubierta a la reserva y no al
/// barrido/matriz; <see cref="AcumuladoCalculador"/> (Tarea 5) al final, sobre las filas y ejes que
/// produjo la Tarea 4. Se computa solo cuando hay con qué: <paramref name="registroBarrido"/> no nulo
/// (registroBarrido no nulo: la ruta intentó leer el barrido, con cualquier resultado declarado) o <paramref name="fotoReservas"/> ya medida — sin
/// ninguno de los dos insumos no hay ninguna de las tres fuentes que cruzar, y <c>Ejecutado</c> queda
/// <c>null</c>, misma semántica que los demás bloques ausentes de este método. Cuando
/// <paramref name="registroBarrido"/> llega <c>null</c> (el llamador no leyó el barrido en esta ruta,
/// p. ej. el preview liviano) se usa <see cref="RegistroBarrido.NoAutorizado"/> con un motivo propio de
/// esta ruta, nunca <see cref="RegistroBarrido.SinBarrido"/>: la ausencia acá es de LECTURA, no un
/// hecho confirmado de que el cliente nunca corrió el barrido.</para>
/// </summary>
public static class InformeValorEnsamblador
{
    public static ModeloInformeValor Ensamblar(
        IReadOnlyList<FacturacionRow> facturacion, int filasAntesDeFusionar,
        IReadOnlyList<CasoRow> casos, InsumosBd insumosBd, string nombreCliente,
        ContextoInformeValor contexto, FotoReservas? fotoReservas = null,
        RegistroBarrido? registroBarrido = null, IReadOnlyList<EvolucionRow>? evolucion = null)
    {
        var consumo = ConsumoCalculador.Calcular(facturacion, filasAntesDeFusionar, contexto);
        var operacion = OperacionCalculador.Calcular(casos, contexto);
        var seguridad = SeguridadCalculador.Calcular(insumosBd.Rbac, insumosBd.EstadoRbac.Ejes);
        var postura = PosturaCalculador.Calcular(
            insumosBd.Advisor, insumosBd.Retiros,
            insumosBd.SeguridadGestionadaExternamente, insumosBd.SeguridadGestionadaNota, contexto,
            insumosBd.CorridaBoletin);
        var roadmap = RoadmapCalculador.Calcular(insumosBd.Matriz);

        if (consumo is not null)
            consumo = consumo with
            {
                VariacionConsumo = CalcularVariacionConsumo(
                    facturacion, insumosBd.HallazgosResueltos ?? [], contexto, fotoReservas,
                    consumo.MesesParciales),
            };

        // D0: la misma definición de "en rango" que usa ConsumoCalculador (promovida a internal
        // para esto), para que la cobertura y catSerie nunca puedan discrepar de fact sobre qué
        // filas de facturación cuentan.
        var facturacionEnRango = facturacion
            .Where(f => ConsumoCalculador.EnRango(f.Year, f.Month, contexto.PeriodStart, contexto.PeriodEnd))
            .ToList();

        var meta = new InformeValorMeta(
            Cliente: nombreCliente,
            Periodo: FormatearPeriodo(contexto.PeriodStart, contexto.PeriodEnd),
            Corte: contexto.Corte.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Cobertura: CalcularCobertura(facturacionEnRango, insumosBd.Rbac, insumosBd.Advisor),
            RbacOrigen: insumosBd.RbacOrigen);

        // Tarea 6: D.ejecutado, la octava clave (ver el comentario de clase de ModeloInformeValor y
        // el de esta clase). Solo se computa cuando hay con qué: sin registroBarrido ni una foto de
        // reservas ya medida, ninguna de las tres fuentes tiene nada que cruzar y el bloque queda
        // null, misma semántica que los demás bloques ausentes de este método.
        EjecutadoModelo? ejecutado = null;
        if (registroBarrido is not null || (fotoReservas?.Medido ?? false))
        {
            var fotoParaEjecutado = fotoReservas ?? FotoReservasPedidaAparte(contexto);
            var reservasFacturadas = ReservasFacturadasCalculador.Calcular(
                fotoParaEjecutado, evolucion ?? [], facturacion, contexto);
            var (filasEjecutado, ejesEjecutado) = RegistroEjecutadoCalculador.Calcular(
                registroBarrido ?? RegistroBarrido.NoAutorizado(
                    "El barrido no se leyó en esta ruta: no es que el cliente no lo haya corrido, es " +
                    "que este llamado del ensamblador no lo pidió."),
                // Ojo: aun sin registroBarrido, la rama de matriz corre con HallazgosResueltos reales;
                // solo el eje del barrido queda suprimido hasta que el controller lo cablee (T10).
                insumosBd.HallazgosResueltos ?? [], reservasFacturadas, fotoParaEjecutado, facturacion, contexto);
            ejecutado = AcumuladoCalculador.Calcular(
                filasEjecutado, ejesEjecutado, reservasFacturadas, consumo?.Total, contexto);
        }

        return new ModeloInformeValor(
            meta, operacion, consumo, seguridad, postura, roadmap,
            CatSerie: CalcularCatSerie(facturacionEnRango),
            Ejecutado: ejecutado);
    }

    /// <summary>
    /// La segunda fase de <see cref="Ensamblar"/>: el MISMO bloque <c>fact.variacionConsumo</c>, pero
    /// con la <see cref="FotoReservas"/> ya capturada contra Azure. Lo llama
    /// <c>InformeValorController</c> desde <c>/preview/variacion-consumo</c> (ver el comentario de
    /// clase sobre las dos fases) y devuelve exactamente lo que <see cref="Ensamblar"/> habría puesto
    /// en <c>fact.variacionConsumo</c> si se le hubiera pasado esa misma foto: es el mismo camino de
    /// código, no una segunda implementación que pueda divergir.
    ///
    /// <para><b>Devuelve los tres baldes, no solo el de reservas</b>, aunque lo único que la fase 2
    /// agrega sea la foto. El balde 1 le SACA recursos a los baldes 2 y 3 (E3/E9: "gana la reserva"),
    /// así que publicar el balde de reservas sin recalcular los otros dos dejaría el mismo recurso
    /// contado dos veces y una variación total inflada — la invariante de E1 vale para la respuesta
    /// completa o no vale para nada. Quien consume reemplaza <c>fact.variacionConsumo</c> entero.</para>
    ///
    /// <para><paramref name="facturacion"/>/<paramref name="hallazgosResueltos"/>/<paramref name="contexto"/>
    /// tienen que ser los mismos que se le pasaron a <see cref="Ensamblar"/> para el informe que se
    /// está completando: si el consultor cambió el período entre las dos llamadas, el bloque que
    /// vuelve mide otra ventana.</para>
    ///
    /// <para><b>Recibe los hallazgos resueltos, no el <see cref="InsumosBd"/> completo</b>, porque es
    /// lo único de ese record que este bloque lee (<see cref="AtribucionCalculador"/> es su único
    /// consumidor). Pedir el record entero obligaba a quien llama a leer los cuatro insumos de base
    /// para usar un solo campo: ver <c>IInsumosBdRecolector.LeerHallazgosResueltosAsync</c>, el camino
    /// angosto que existe justo por eso.</para>
    /// </summary>
    public static VariacionConsumoModelo EnsamblarVariacionConsumo(
        IReadOnlyList<FacturacionRow> facturacion, IReadOnlyList<HallazgoResueltoFila> hallazgosResueltos,
        ContextoInformeValor contexto, FotoReservas fotoReservas)
    {
        // De todo el bloque de consumo solo se necesitan los meses parciales ya resueltos, y salen de
        // la MISMA función que los resolvió en la fase 1 (nunca de una segunda detección acá, que es
        // justo lo que ningún bloque de la ventana fija puede hacer sin arriesgarse a discrepar).
        // filasAntesDeFusionar es D14: alimenta ConsumoModelo.Filas, un conteo que este bloque no
        // publica, así que no vale pagar la lectura de la bitácora de ingesta para pasarlo.
        var consumo = ConsumoCalculador.Calcular(facturacion, filasAntesDeFusionar: 0, contexto);

        // Sin bloque de consumo (ninguna fila de facturación en rango) igual se ensambla: el panel de
        // cobertura de reservas de E5 no depende de que haya historia de facturación, solo de que
        // haya reservas que leer. Sin meses en rango no hay ventana fija, así que la atribución sale
        // null y con ella la variación total, exactamente como en la fase 1.
        return CalcularVariacionConsumo(
            facturacion, hallazgosResueltos, contexto, fotoReservas, consumo?.MesesParciales ?? []);
    }

    /// <summary>
    /// Tarea 5 (E0, E9): balde 1 (reservas, sobre la ventana del informe) + baldes 2 y 3
    /// (<see cref="AtribucionCalculador"/>), con la exclusión de E3/E9 aplicada correctamente.
    /// <see cref="AhorroReservasCalculador.Calcular"/> nunca devuelve null (siempre hay algo que
    /// publicar en el panel de reservas, aunque sea "no medido"), así que <see cref="VariacionConsumoModelo.Reservas"/>
    /// siempre viaja; <see cref="AtribucionCalculador.Calcular"/> sí puede devolver null (menos de
    /// seis meses no parciales), y en ese caso <see cref="VariacionConsumoModelo.Atribucion"/> y
    /// <see cref="VariacionConsumoModelo.VariacionTotal"/> quedan null a la vez — ver el comentario
    /// de clase de <see cref="VariacionConsumoModelo"/>.
    /// </summary>
    /// <param name="mesesParciales">Los de <see cref="ConsumoModelo.MesesParciales"/> del mismo
    /// informe, ya resueltos: los dos bloques de la ventana fija los reciben, nunca los vuelven a
    /// detectar por su cuenta.</param>
    private static VariacionConsumoModelo CalcularVariacionConsumo(
        IReadOnlyList<FacturacionRow> facturacion, IReadOnlyList<HallazgoResueltoFila> hallazgosResueltos,
        ContextoInformeValor contexto, FotoReservas? fotoReservas, IReadOnlyList<string> mesesParciales)
    {
        var foto = fotoReservas ?? FotoReservasPedidaAparte(contexto);
        var reservas = AhorroReservasCalculador.Calcular(foto, facturacion, mesesParciales, contexto);

        var atribucion = AtribucionCalculador.Calcular(
            facturacion, hallazgosResueltos, mesesParciales,
            reservas.RecursosQueExplicanElPeriodo.ToHashSet(), contexto);

        var variacionTotal = atribucion is null
            ? (decimal?)null
            : reservas.AporteAlPeriodo + atribucion.PorRecomendacion.Total + atribucion.SinAtribuir.Total;

        return new VariacionConsumoModelo(reservas, atribucion, variacionTotal);
    }

    /// <summary>
    /// El eje de reservas cuando <see cref="Ensamblar"/> se llama sin <see cref="FotoReservas"/>, o
    /// sea en la fase 1 (ver el comentario de clase): mismo <see cref="FotoReservas.Medido"/> en
    /// <c>false</c> que <c>ReservasRecolector.CapturarAsync</c> devuelve sin credenciales activas, así
    /// que <see cref="AhorroReservasCalculador.Calcular"/> lo trata exactamente igual, sin un estado
    /// nuevo además de "no medido".
    ///
    /// <para><b>El motivo dice que el dato se pide aparte</b>, y es lo único que distingue este caso
    /// del cliente que no tiene ninguna reserva. Los dos publican el balde en cero, y esa cifra
    /// ambigua es exactamente lo que este módulo corrige en todos sus bloques: quien lea la respuesta
    /// de la fase 1 tiene que poder saber que falta una llamada, no concluir que no hay reservas.</para>
    ///
    /// <para>Sin reloj (vive en <c>Calculo</c>): <see cref="FotoReservas.CapturadaEn"/> no lo lee
    /// ningún cálculo de este módulo (es un dato para la foto que persistirá la entrega 3), así que un
    /// valor fijo derivado del propio <paramref name="contexto"/> alcanza.</para>
    /// </summary>
    private static FotoReservas FotoReservasPedidaAparte(ContextoInformeValor contexto) => new(
        Medido: false,
        Motivo: "Las reservas de Azure se leen en una llamada aparte y todavía no se pidió: este eje " +
                "no se midió acá. No significa que el cliente no tenga reservas.",
        Errores: [], AlertDays: ReservasRecolector.AlertDaysPorDefecto,
        CapturadaEn: contexto.Corte.ToDateTime(TimeOnly.MinValue), Reservas: []);

    private static string FormatearPeriodo(DateOnly inicio, DateOnly fin) =>
        inicio == fin
            ? inicio.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : $"{inicio.ToString("yyyy-MM", CultureInfo.InvariantCulture)} a {fin.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// D12. Se normaliza por <c>subscription_id</c> donde exista: la clave de conciliación es el
    /// id cuando la fila lo trae, y el nombre solo cuando esa fila en particular no tiene id (no
    /// hay una tercera fuente que sepa "este nombre es en realidad tal id"). Para RBAC se usa el
    /// conjunto COMPLETO de suscripciones alcanzadas de cada fila
    /// (<see cref="RbacFila.SuscripcionesAlcanzadas"/>/<see cref="RbacFila.SuscripcionesAlcanzadasNombres"/>),
    /// no solo la suscripción primaria: es la misma vista que ya usa <see cref="SeguridadModelo.Suscripciones"/>
    /// internamente. La matriz no participa: no tiene columna de suscripción.
    /// </summary>
    private static InformeValorCobertura CalcularCobertura(
        IReadOnlyList<FacturacionRow> facturacionEnRango,
        IReadOnlyList<RbacFila> rbac,
        IReadOnlyList<AdvisorFila> advisor)
    {
        var vistos = new List<string>();
        var nombrePorClave = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deFacturacion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deRbac = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deAdvisor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? Registrar(string? id, string? nombre)
        {
            var clave = !string.IsNullOrWhiteSpace(id) ? id : nombre;
            if (string.IsNullOrWhiteSpace(clave)) return null; // sin id ni nombre no hay nada que conciliar
            if (!nombrePorClave.ContainsKey(clave))
            {
                vistos.Add(clave);
                // Si no hay nombre todavia, se publica el id crudo como nombre (nunca se pierde la
                // fila por falta de nombre): igual que hace SeguridadCalculador.CalcularSuscripciones
                // para el mismo caso.
                nombrePorClave[clave] = !string.IsNullOrWhiteSpace(nombre) ? nombre! : clave;
            }
            return clave;
        }

        foreach (var f in facturacionEnRango)
        {
            var clave = Registrar(f.SubscriptionId, f.SubscriptionName);
            if (clave is not null) deFacturacion.Add(clave);
        }

        foreach (var r in rbac)
        {
            var ids = r.SuscripcionesAlcanzadas;
            var nombres = r.SuscripcionesAlcanzadasNombres;
            for (var i = 0; i < ids.Count; i++)
            {
                var nombre = i < nombres.Count ? nombres[i] : null;
                var clave = Registrar(ids[i], nombre);
                if (clave is not null) deRbac.Add(clave);
            }
        }

        foreach (var a in advisor)
        {
            var clave = Registrar(a.SubscriptionId, a.SubscriptionName);
            if (clave is not null) deAdvisor.Add(clave);
        }

        var filas = vistos
            .Select(clave => new CoberturaSuscripcion(
                Id: clave,
                Nombre: nombrePorClave[clave],
                Facturacion: deFacturacion.Contains(clave),
                Rbac: deRbac.Contains(clave),
                Advisor: deAdvisor.Contains(clave)))
            .ToList();

        return new InformeValorCobertura(filas.Count, filas);
    }

    /// <summary>
    /// <c>D.catSerie</c>: serie mensual por categoría de facturación (<c>catSerie()</c> en la
    /// plantilla), restringida al mismo rango D0 que <see cref="ConsumoModelo"/> — divergencia
    /// deliberada: la plantilla nunca filtra <c>catSerie</c> por período (consume el archivo
    /// entero, igual que hacía <c>calcFact</c> antes de D0), así que fuera de este rango podía
    /// mostrar meses que ninguna otra sección del informe reconoce. <c>null</c> cuando no queda
    /// ninguna fila en rango, igual que <see cref="ConsumoCalculador.Calcular"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>>? CalcularCatSerie(
        IReadOnlyList<FacturacionRow> facturacionEnRango)
    {
        if (facturacionEnRango.Count == 0) return null;

        var vistos = new List<string>();
        var acumulado = new Dictionary<string, Dictionary<string, decimal>>();
        foreach (var f in facturacionEnRango)
        {
            var cat = string.IsNullOrWhiteSpace(f.Category) ? ConsumoCalculador.SinCategoria : f.Category!;
            if (!acumulado.TryGetValue(cat, out var porMes))
            {
                porMes = [];
                acumulado[cat] = porMes;
                vistos.Add(cat);
            }
            var mes = ConsumoCalculador.Ym(f.Year, f.Month);
            porMes[mes] = porMes.GetValueOrDefault(mes) + f.Pvp;
        }

        return vistos.ToDictionary(
            cat => cat,
            cat => (IReadOnlyDictionary<string, decimal>)acumulado[cat]
                .ToDictionary(kv => kv.Key, kv => Redondeo.ComoJs(kv.Value)),
            StringComparer.Ordinal);
    }
}
