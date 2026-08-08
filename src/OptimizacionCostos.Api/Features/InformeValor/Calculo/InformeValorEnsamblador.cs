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
/// </summary>
public static class InformeValorEnsamblador
{
    public static ModeloInformeValor Ensamblar(
        IReadOnlyList<FacturacionRow> facturacion, int filasAntesDeFusionar,
        IReadOnlyList<CasoRow> casos, InsumosBd insumosBd, string nombreCliente,
        ContextoInformeValor contexto)
    {
        var consumo = ConsumoCalculador.Calcular(facturacion, filasAntesDeFusionar, contexto);
        var operacion = OperacionCalculador.Calcular(casos, contexto);
        var seguridad = SeguridadCalculador.Calcular(insumosBd.Rbac, insumosBd.EstadoRbac.Ejes);
        var postura = PosturaCalculador.Calcular(
            insumosBd.Advisor, insumosBd.Retiros,
            insumosBd.SeguridadGestionadaExternamente, insumosBd.SeguridadGestionadaNota, contexto);
        var roadmap = RoadmapCalculador.Calcular(insumosBd.Matriz);

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
            Cobertura: CalcularCobertura(facturacionEnRango, insumosBd.Rbac, insumosBd.Advisor));

        return new ModeloInformeValor(
            meta, operacion, consumo, seguridad, postura, roadmap,
            CatSerie: CalcularCatSerie(facturacionEnRango));
    }

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
