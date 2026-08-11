using System.Globalization;
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
/// Solo se intenta cuando hay bloque de consumo (<paramref name="facturacion"/> con filas en rango):
/// los tres baldes miden variación de facturación, y sin eso no hay nada que descomponer — mismo
/// filtro D0 que ya usa <see cref="ConsumoCalculador.Calcular"/>.</para>
///
/// <para><see cref="FotoReservas"/> es IO (Tarea 1) capturada por quien llama, fuera de este método
/// puro: <paramref name="fotoReservas"/> es <c>null</c> por defecto porque, hasta que un llamador
/// conecte esa lectura en vivo contra Azure (pendiente, ver el informe de la Tarea 5), se trata como
/// "eje no medido" — el mismo estado que <c>ReservasRecolector.CapturarAsync</c> devuelve sin
/// credenciales activas — en vez de bloquear la compilación de <c>InformeValorController.Preview</c>
/// con un parámetro que hoy nadie puede llenar sin tocar Azure real.</para>
/// </summary>
public static class InformeValorEnsamblador
{
    public static ModeloInformeValor Ensamblar(
        IReadOnlyList<FacturacionRow> facturacion, int filasAntesDeFusionar,
        IReadOnlyList<CasoRow> casos, InsumosBd insumosBd, string nombreCliente,
        ContextoInformeValor contexto, FotoReservas? fotoReservas = null)
    {
        var consumo = ConsumoCalculador.Calcular(facturacion, filasAntesDeFusionar, contexto);
        var operacion = OperacionCalculador.Calcular(casos, contexto);
        var seguridad = SeguridadCalculador.Calcular(insumosBd.Rbac, insumosBd.EstadoRbac.Ejes);
        var postura = PosturaCalculador.Calcular(
            insumosBd.Advisor, insumosBd.Retiros,
            insumosBd.SeguridadGestionadaExternamente, insumosBd.SeguridadGestionadaNota, contexto);
        var roadmap = RoadmapCalculador.Calcular(insumosBd.Matriz);

        if (consumo is not null)
            consumo = consumo with
            {
                VariacionConsumo = CalcularVariacionConsumo(facturacion, insumosBd, contexto, fotoReservas, consumo),
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

        return new ModeloInformeValor(
            meta, operacion, consumo, seguridad, postura, roadmap,
            CatSerie: CalcularCatSerie(facturacionEnRango));
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
    private static VariacionConsumoModelo CalcularVariacionConsumo(
        IReadOnlyList<FacturacionRow> facturacion, InsumosBd insumosBd, ContextoInformeValor contexto,
        FotoReservas? fotoReservas, ConsumoModelo consumo)
    {
        var foto = fotoReservas ?? FotoReservasNoConectada(contexto);
        var reservas = AhorroReservasCalculador.Calcular(foto, facturacion, consumo.MesesParciales, contexto);

        var atribucion = AtribucionCalculador.Calcular(
            facturacion, insumosBd.HallazgosResueltos ?? [], consumo.MesesParciales,
            reservas.RecursosQueExplicanElPeriodo.ToHashSet(), contexto);

        var variacionTotal = atribucion is null
            ? (decimal?)null
            : reservas.AporteAlPeriodo + atribucion.PorRecomendacion.Total + atribucion.SinAtribuir.Total;

        return new VariacionConsumoModelo(reservas, atribucion, variacionTotal);
    }

    /// <summary>
    /// Placeholder de "eje no medido" para cuando <see cref="Ensamblar"/> se llama sin
    /// <see cref="FotoReservas"/> (ver el comentario de clase): mismo <see cref="FotoReservas.Medido"/>
    /// en <c>false</c> que <c>ReservasRecolector.CapturarAsync</c> devuelve sin credenciales activas,
    /// así que <see cref="AhorroReservasCalculador.Calcular"/> lo trata exactamente igual, sin un
    /// camino especial para "no conectado" además de "no medido". Sin reloj (vive en <c>Calculo</c>):
    /// <see cref="FotoReservas.CapturadaEn"/> no lo lee ningún cálculo de este módulo (es un dato para
    /// la foto que persistirá la entrega 3), así que un valor fijo derivado del propio
    /// <paramref name="contexto"/> alcanza.
    /// </summary>
    private static FotoReservas FotoReservasNoConectada(ContextoInformeValor contexto) => new(
        Medido: false,
        Motivo: "Este informe todavía no conecta la lectura en vivo de reservas de Azure.",
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
