using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;

namespace OptimizacionCostos.Api.Tests.InformeValor.Entrega;

/// <summary>
/// F0 de la entrega 3: <b>hay dos renderizadores del mismo informe y van a divergir</b>. La CSP del
/// SWA no permite renderizar el artefacto dentro de la app ni servirlo desde la API, así que la
/// vista previa es React nativo sobre el modelo y el HTML es solo descarga. Son dos
/// implementaciones de la misma cosa, y eso no se puede eliminar. El plan no es evitar la
/// divergencia: es detectarla.
///
/// <para><b>Qué verifica.</b> Que los dos renderizadores consuman el mismo conjunto de campos del
/// modelo. Un campo que uno lee y el otro no es un hallazgo, no una diferencia aceptable: significa
/// que un lector ve un hecho que el otro no. El consultor aprueba la entrega mirando la vista React;
/// si el artefacto publica algo que esa vista no muestra, lo aprobó sin verlo, y si la vista muestra
/// algo que el artefacto no lleva, lo revisó de más.</para>
///
/// <para><b>Por qué un barrido de texto.</b> Mismo patrón que <c>SinRelojDelSistemaTests</c> y
/// <c>ColumnLimitsSchemaSyncTests</c>: se lee el código fuente, no se ejecuta. El JavaScript de la
/// plantilla no corre en esta suite y el TSX vive en otro repo. Lo que sí se puede fijar —y es lo
/// que rompía— es el CONTRATO DE NOMBRES.</para>
///
/// <para><b>Lo que este barrido NO puede ver</b>, dicho acá para que nadie lo lea como cobertura
/// completa:</para>
/// <list type="bullet">
/// <item>Las <b>filas posicionales</b> (<c>fact.meses</c>, <c>tickets.lista</c>, <c>advisor.top</c>…)
/// no tienen nombres: se comparan como un solo campo, no posición por posición. Que los dos lados
/// lean <c>f.serie</c> no prueba que lean las mismas columnas de cada fila.</item>
/// <item>Los <b>nombres de una sola letra o dos</b> (<c>n</c>, <c>c</c>, <c>av</c>…) se repiten bajo
/// muchos padres distintos y colapsan en un solo token. Si un lado lee <c>cats[].n</c> y el otro
/// <c>subs[].n</c>, el barrido los ve iguales. El error queda del lado de no reportar, nunca del de
/// reportar de más.</item>
/// <item>Que un campo se lea no dice que se lea <b>bien</b>. Eso es trabajo de los tests de cada
/// bloque.</item>
/// </list>
/// </summary>
public sealed class ContratoEntreRenderizadoresTests
{
    // ================== el contrato declarado ==================

    /// <summary>
    /// Los campos que hoy NO leen los dos renderizadores, con el motivo. Una entrada acá es una
    /// deuda declarada, no una diferencia bendecida: la clave es la ruta del campo en el modelo
    /// (<c>fact.ahorro.dif</c>) o el prefijo de una rama entera (<c>fact.variacionConsumo</c>).
    ///
    /// <para><see cref="Lado"/> dice quién lo lee HOY. Si el otro renderizador empieza a leerlo, la
    /// entrada sobra y el test lo dice: una excepción que sobrevive a su motivo es la forma en que
    /// una tabla como esta empieza a mentir.</para>
    /// </summary>
    public static readonly (string Ruta, Lado Lado, string Motivo)[] Asimetrias =
    [
        // ---- solo la vista React ----
        ("meta.cobertura", Lado.React,
            "D12: la conciliación de las tres cifras de suscripciones. render() no la lee a propósito " +
            "(construye su sección de cobertura inline, sin conciliar, que es el defecto que D12 corrige) " +
            "y agregarla habría roto el contrato ya probado de las siete claves exactas de D."),
        ("meta.rbacOrigen", Lado.React,
            "De qué fuente salió el bloque de seguridad, base o archivo de respaldo. Es información " +
            "para el consultor que arma el informe, no para quien lo recibe: el artefacto del cliente " +
            "no tiene por qué declarar la plomería interna de la plataforma."),
        ("fact.variacionConsumo", Lado.React,
            "Los tres baldes de la entrega 2d. NINGUNO de los ocho bloques económicos los cubre, así " +
            "que el exportador los recorta enteros para el cliente y el artefacto interno los lleva sin " +
            "dibujarlos. Es la asimetría más grande del módulo y está abierta a propósito: la sección " +
            "necesita su propio interruptor de aprobación antes de poder viajar. Hasta entonces, el " +
            "consultor la revisa en la vista y el artefacto no la publica."),
        ("ejecutado.reservas.filas.reservationId", Lado.React,
            "La ReservaActiva que originó la fila. Colisiona con \"reservationId\" bajo " +
            "fact.variacionConsumo.reservas (AhorroPorRecurso/EstimadoPorReserva/DiscrepanciaCobertura, " +
            "verificado en VariacionConsumo.tsx líneas 111 y 162: PanelReservas y la tabla de " +
            "estimadas lo leen los dos); la tabla por VM de la Tarea 5 de esta entrega (sección " +
            "Reservas) no muestra esta columna, solo vm/sku/demanda/reserva/ahorro/vence. \"reservas\" " +
            "(la clave) y \"consumidoresNoLeidos\" ya no necesitan esta entrada -colisionaban por el " +
            "mismo límite de nombres cortos que el docstring de esta clase declara- porque la Tarea 5 " +
            "los volvió simétricos al leerlos también del lado del artefacto (ej.reservas, " +
            "rs.consumidoresNoLeidos, verificado con `grep -c reservationId` sobre la plantilla: cero " +
            "coincidencias). \"nota\" tiene su propia entrada más abajo: no es uno de esos tokens " +
            "compartidos, aunque el barrido la agrupe con esta ruta."),
        ("ejecutado.reservas.filas.nota", Lado.React,
            "Falso positivo del barrido por texto: en VariacionConsumo.tsx \"nota\" nace de un arreglo " +
            "local `mecanismos` dentro de PanelAtribucion (verificado en líneas 193 y 223: " +
            "`mecanismos: {...; nota: string}[]` y `r.nota`), una etiqueta de presentación de los " +
            "baldes de atribución sin relación con el modelo ni con la tabla de reservas por VM. La " +
            "Tarea 5 de esta entrega no dibuja la nota de la reserva en esa tabla."),
        ("opex.estado", Lado.Ninguno,
            "El estado textual del score (por ejemplo \"en riesgo\"), sin usar todavía: la tarjeta del " +
            "resumen (Tarea 3) y el gráfico de la sección Advisor (Tarea 5) leen actual/serie/medido/" +
            "motivo, no este campo."),

        // ---- lo que no lee ninguno de los dos ----
        ("fact.variacionConsumo", Lado.Ninguno,
            "La misma rama de arriba, por su otra mitad: dentro de la variación del consumo hay campos " +
            "que son insumo interno del backend (los recursos que explican cada balde, sus tarifas " +
            "antes y después, la utilización de cada reserva) y que ninguna de las dos vistas dibuja. " +
            "Se revisa cuando la sección tenga su interruptor y un renderizador que la publique."),
        // "ejecutado" ya no es una excepción de bloque entero (la Tarea 4 del HTML y la Tarea 9 de
        // React dibujan la sección titular completa: acumulado mes a mes, ranking por oportunidad y
        // la tabla de acciones -oportunidad/cat/rec/mes/monto/fuenteMonto/sinMonto- en los dos lados).
        // Lo único que sigue sin lector de los dos lados es la SERIE de la proyección mensual
        // (ejecutado.proyeccion, ver más abajo): la sección titular publica el total proyectado a fin
        // de año (proyeccionFin) pero no dibuja esa curva mes a mes. El apilado por categoría
        // (ejecutado.catAcum) sigue solo en el HTML: ver su entrada propia, abajo. "ejecutado.reservas"
        // dejó de ser una excepción con el commit f10b5ad del front (SeccionEjecutado.tsx ya dibuja
        // <SeccionReservas reservas={ej.reservas} />): sus tres columnas colisionadas siguen
        // declaradas por su cuenta (reservationId, nota), pero la tabla en sí ya la leen los dos lados.
        ("ejecutado.filas.rg", Lado.Ninguno,
            "El grupo de recursos de cada acción ejecutada: la tabla de la sección titular (Tarea 4 de " +
            "la entrega 7) identifica el recurso por su nombre (rec), no por su grupo. Ninguno de los " +
            "dos renderizadores lo publica."),
        ("ejecutado.filas.autoria", Lado.Ninguno,
            "Si la acción quedó con autoría declarada, automática o indeterminada: la tabla de la " +
            "sección titular (Tarea 4 de la entrega 7) no dibuja esta columna. El conteo de " +
            "indeterminadas sí se declara, pero por ejecutado.ejes.indeterminadas, no por esta " +
            "columna fila por fila."),
        ("ejecutado.proyeccion", Lado.Ninguno,
            "La proyección mensual del acumulado a fin de año, mes a mes: la sección titular (Tarea 4 " +
            "de la entrega 7) publica el TOTAL proyectado (proyeccionFin) en una tarjeta, pero no " +
            "dibuja esta curva mensual. Pendiente de un renderizador que la grafique."),
        ("ejecutado.catAcum", Lado.Html,
            "El apilado por categoría del acumulado ejecutado: el segundo gráfico de la sección " +
            "titular (Tarea 4 de la entrega 7, la PPT de referencia). La Tarea 9 de esta entrega (la " +
            "vista React) solo agregó el acumulado mes a mes y el ranking por oportunidad a " +
            "SeccionEjecutado.tsx -no este apilado por categoría-: verificado con grep para \"catAcum\" " +
            "en ese archivo, sin resultados."),
        ("meta.conciliacion", Lado.Html,
            "El panel \"Los dos archivos de facturación\" (Tarea 7 de la entrega 7) lee M.conciliacion " +
            "(M=D.meta) directamente y, cuando el nodo llega poblado, sus campos coincide y difs. La " +
            "Tarea 9 de esta entrega (la vista React) no incluye este panel en su alcance -ver la tabla " +
            "de archivos del plan de la entrega 7, que asigna meta.conciliacion a la Tarea 7 y no a la " +
            "9-: verificado contra innovacion-CDC (src/components/informe-valor/informe, " +
            "src/lib/informeValor.ts) que la vista React todavía no lo lee. `grep` para " +
            "\"conciliacion\"/\"coincide\"/\"difs\" da dos coincidencias en esos dos lugares y ninguna " +
            "es una lectura del modelo: \"conciliacion\" en un fixture de InformeVista.test.tsx (archivo " +
            "de test, fuera del alcance de FuentesReact) y \"coincide\" en un comentario de " +
            "informeValor.ts:24 que describe una lista sin relación con meta.conciliacion (Normalizar() " +
            "descarta comentarios, así que tampoco lo ve el barrido). Queda declarada hasta que un " +
            "renderizador React lo dibuje."),
        ("meta.conciliacion.umbralTasa", Lado.Ninguno,
            "La tasa del 0.5% que decide, mes a mes, qué filas entran a difs (InformeValorEnsamblador." +
            "CalcularConciliacion). El panel de la Tarea 7 publica el veredicto ya aplicado -coincide y " +
            "difs, con la cifra de cada fuente- y no necesita reconstruir el cálculo mostrando la tasa " +
            "que lo produjo; ninguna de las dos vistas la dibuja."),
        ("meta.cobertura.suscripciones.id", Lado.Ninguno,
            "El identificador con el que D12 normaliza y concilia las tres fuentes. Es la clave del " +
            "cruce, no una columna: la tabla publica el nombre, y cuando ninguna fuente trajo nombre " +
            "para ese id el propio id viaja como nombre, así que nunca se pierde la fila."),
        ("fact.nRecursos", Lado.Ninguno,
            "Recursos contados por nombre global, la identidad que D11 rechaza (dos homónimos en " +
            "suscripciones distintas cuentan como uno). Se calcula porque ConsumoCalculador porta la " +
            "asimetría de calcFact tal cual, pero ningún renderizador lo publica: el conteo de recursos " +
            "de los dos lados es fact.nIds, la terna suscripción+grupo+nombre."),
        ("advisor.porSub", Lado.Ninguno,
            "Existía para que la capa de dibujo recalculara el veredicto de cada línea de ahorro, que " +
            "es justo lo que D7 sacó de ahí (ahora viene en savLineas[].contada, del mismo cálculo que " +
            "produce el total). Queda en el modelo como la descomposición reserva/savings plan por " +
            "suscripción; ninguna vista la dibuja."),
        ("matriz.items.g", Lado.Ninguno,
            "El registro de origen del hallazgo de la matriz. Ninguna de las dos tablas de hallazgos " +
            "publica esa columna."),
        // "opex" y "cronologia" ya no son excepciones de bloque entero: la tarjeta OPEX del resumen
        // (Tarea 3 del HTML) y el gráfico de la sección Advisor (Tarea 5) tienen su espejo en React
        // desde la Tarea 9 (la cabecera de cuatro tarjetas y el nuevo panel de SeccionPostura), y la
        // línea de tiempo de "cronologia" tiene su SeccionCronologia.tsx desde la misma tarea. Lo
        // único que sigue sin lector de los dos lados en este bloque es opex.estado (ver la entrada
        // declarada arriba).
        ("cronologia.hitos.pilar", Lado.Ninguno,
            "El pilar WAF del hallazgo asociado al hito. La línea de tiempo del HTML (Tarea 5) y " +
            "SeccionCronologia.tsx (Tarea 9) agrupan los hitos por fecha, no por pilar, así que ninguno " +
            "de los dos lo dibuja: verificado con grep para \"pilar\" en SeccionCronologia.tsx, sin " +
            "resultados."),
    ];

    public enum Lado { Html, React, Ninguno }

    // ================== los tres tests ==================

    /// <summary>
    /// El test de F0. Todo campo del modelo lo leen los dos renderizadores, o su asimetría está
    /// declarada arriba con el motivo.
    /// </summary>
    [Fact]
    public void Los_dos_renderizadores_consumen_el_mismo_conjunto_de_campos()
    {
        // xunit 2.5 no tiene salto dinámico, así que este es un no-op cuando el entorno DECLARA que no
        // tiene el otro repo (INFORME_VALOR_FRONT=ninguno). Sin esa declaración, no encontrar la vista
        // React es un fallo con instrucciones: un guardia apagado en silencio no protege nada.
        var fuentes = FuentesReact.Resolver();
        if (fuentes.Texto is null) return;

        var html = Normalizar(CapaDeDibujoHtml());
        var react = Normalizar(fuentes.Texto);

        var problemas = new List<string>();
        foreach (var campo in Inventario())
        {
            var enHtml = Lee(html, campo.Token);
            var enReact = Lee(react, campo.Token);
            var esperado = enHtml == enReact
                ? (enHtml ? (Lado?)null : Lado.Ninguno)
                : (enHtml ? Lado.Html : Lado.React);

            if (esperado is null) continue; // lo leen los dos: no hay nada que declarar

            var motivo = MotivoDeclarado(campo, esperado.Value);
            if (motivo is null)
                problemas.Add(esperado.Value switch
                {
                    Lado.Html => $"{campo.Rutas} lo lee SOLO la plantilla embebida: el cliente ve ese " +
                                 "hecho y el consultor que aprueba la entrega no.",
                    Lado.React => $"{campo.Rutas} lo lee SOLO la vista React: el consultor lo revisa y " +
                                  "el artefacto que recibe el cliente no lo publica.",
                    _ => $"{campo.Rutas} no lo lee ninguno de los dos: el modelo lo calcula y lo " +
                         "serializa para nadie.",
                });
        }

        Assert.True(problemas.Count == 0,
            $"Los dos renderizadores del informe divergen en {problemas.Count} campo(s). Cada uno se " +
            "arregla leyéndolo del lado que falta, o se declara en ContratoEntreRenderizadoresTests." +
            $"Asimetrias con el motivo:{Environment.NewLine}  - " +
            string.Join(Environment.NewLine + "  - ", problemas));
    }

    /// <summary>
    /// La mitad del contrato que no necesita el otro repo, así que corre siempre (también en el CI de
    /// esta API, que solo clona este repo): ningún campo declarado como "solo React" o "no lo lee
    /// nadie" puede aparecer en la capa de dibujo del artefacto. Si alguien lo empieza a dibujar, la
    /// declaración de arriba quedó vieja y hay que rehacer la comparación con los dos lados.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeclaradosFueraDelHtml))]
    public void La_plantilla_no_lee_los_campos_declarados_fuera_de_ella(string ruta, Lado lado)
    {
        var html = Normalizar(CapaDeDibujoHtml());

        // TODAS las rutas de ese token, no cualquiera: un nombre corto vive bajo varios padres
        // (`advisor` es la clave de nivel superior Y una columna de meta.cobertura.suscripciones), y
        // exigir que la plantilla no lea el token cuando solo una de sus rutas está declarada
        // prohibiría de paso la otra. Es la misma limitación de colapso que declara la clase.
        foreach (var campo in Inventario().Where(c => c.Rutas.Split(", ").All(r => Cubre(ruta, r))))
            Assert.False(Lee(html, campo.Token),
                $"'{ruta}' está declarado como {lado} en ContratoEntreRenderizadoresTests.Asimetrias, " +
                $"pero la capa de dibujo lee '{campo.Token}'. Si la plantilla ahora lo publica, la " +
                "entrada sobra o cambió de lado: hay que rehacer la comparación entre los dos " +
                "renderizadores, no editar el motivo.");
    }

    public static TheoryData<string, Lado> DeclaradosFueraDelHtml()
    {
        var d = new TheoryData<string, Lado>();
        foreach (var (ruta, lado, _) in Asimetrias.Where(a => a.Lado != Lado.Html)) d.Add(ruta, lado);
        return d;
    }

    /// <summary>
    /// Toda entrada de <see cref="Asimetrias"/> apunta a un campo que existe. Una ruta mal escrita, o
    /// que quedó de un campo renombrado, excusa un campo que nadie está mirando: la excepción sigue
    /// verde mientras la divergencia real pasa sin reportarse.
    /// </summary>
    [Fact]
    public void Toda_asimetria_declarada_apunta_a_un_campo_del_modelo()
    {
        var rutas = Inventario().SelectMany(c => c.Rutas.Split(", ")).ToList();
        Assert.NotEmpty(rutas);

        foreach (var (ruta, _, motivo) in Asimetrias)
        {
            Assert.True(rutas.Any(r => Cubre(ruta, r)),
                $"'{ruta}' no corresponde a ningún campo de ModeloInformeValor. O se renombró el campo, " +
                "o la entrada sobra: mientras esté acá, excusa una divergencia que nadie mira.");
            Assert.True(motivo.Length >= 60,
                $"La asimetría de '{ruta}' no tiene motivo. Un campo que un renderizador lee y el otro " +
                "no es un hallazgo; lo único que lo vuelve aceptable es la razón escrita.");
        }
    }

    /// <summary>Guarda del propio barrido: si el inventario sale vacío, o la capa de dibujo no se
    /// encuentra, los tests de arriba pasarían solos y fingirían cobertura.</summary>
    [Fact]
    public void El_barrido_mira_algo()
    {
        Assert.True(Inventario().Count > 50, "el inventario del modelo salió sospechosamente corto");
        Assert.Contains("D.meta", CapaDeDibujoHtml(), StringComparison.Ordinal);
    }

    /// <summary>
    /// I4 de la revisión final de la entrega 7: <see cref="Los_dos_renderizadores_consumen_el_mismo_conjunto_de_campos"/>
    /// no puede detectar SOBREDECLARACIÓN. Ese test hace <c>if (esperado is null) continue;</c> ANTES
    /// de consultar <see cref="Asimetrias"/>: un campo que los dos lados ya leen simplemente no se
    /// visita, así que una entrada que dejó de ser cierta -como <c>ejecutado.reservas</c>, falsa desde
    /// el commit f10b5ad del front, un commit entero antes de este fix- queda ahí para siempre,
    /// pasando en verde. Este test recorre <see cref="Asimetrias"/> AL REVÉS: para cada entrada
    /// declarada como un campo hoja exacto, recalcula la asimetría real con el mismo <c>Lee()</c>/
    /// <c>esperado</c> del test de arriba y exige que siga existiendo y del mismo lado.
    ///
    /// <para><b>Por qué algunas entradas quedan exentas.</b> Varias filas de <see cref="Asimetrias"/>
    /// no son un campo hoja: son el PREFIJO de una rama entera (mismo mecanismo de colapso de nombres
    /// cortos que <see cref="Lee"/> ya documenta), y hasta declaran el MISMO literal de ruta dos veces
    /// con Lado distinto (<c>fact.variacionConsumo</c>: una vez React, una vez Ninguno) porque cada
    /// hijo de esa rama tiene su propio Lado real. Preguntarle a la ruta del contenedor "¿cuál es TU
    /// esperado?" no tiene una única respuesta verificable ahí: se exime con el motivo puntual, nunca
    /// en silencio.</para>
    /// </summary>
    [Fact]
    public void Toda_asimetria_declarada_sigue_siendo_una_asimetria_real()
    {
        var fuentes = FuentesReact.Resolver();
        if (fuentes.Texto is null) return; // mismo guardia que el F0: necesita los dos repos.

        var html = Normalizar(CapaDeDibujoHtml());
        var react = Normalizar(fuentes.Texto);
        var inventario = Inventario();

        // Exención por rama entera: solo para el literal de ruta que el propio array declara DOS
        // veces con Lado distinto (una para unos hijos, otra para otros). Ahí no hay un único
        // "esperado" que pedirle, porque una de las dos iteraciones fallaría siempre.
        //
        // Nada más entra acá, y la razón importa: este test existe para cazar la sobredeclaración
        // que el test principal no puede ver (hace `continue` antes de consultar la tabla). Cada
        // ruta que se exente es una entrada que puede envejecer en silencio, que es el defecto
        // que ya apareció una vez en esta misma tabla. Rutas distintas —aunque una sea prefijo de
        // la otra, como meta.conciliacion y meta.conciliacion.umbralTasa— NO necesitan exención:
        // el emparejamiento es por igualdad exacta y el inventario agrupa por token, así que cada
        // una tiene su propio "esperado" computable y se verifica sola.
        var exentosPorRamaEntera = new HashSet<string>(StringComparer.Ordinal)
        {
            "fact.variacionConsumo", // declarado dos veces (Lado.React y Lado.Ninguno) para hijos distintos
        };

        foreach (var (ruta, ladoDeclarado, motivo) in Asimetrias)
        {
            if (exentosPorRamaEntera.Contains(ruta)) continue;

            // Solo entradas cuya ruta es EXACTAMENTE una de las rutas de algún campo (no un prefijo
            // que solo cubra hijos): esas sí tienen un token propio con un esperado computable.
            var campo = inventario.FirstOrDefault(c => c.Rutas.Split(", ").Contains(ruta));
            Assert.True(campo is not null,
                $"'{ruta}' no es un campo hoja exacto del inventario (Toda_asimetria_declarada_apunta_a_" +
                "un_campo_del_modelo ya cubre el caso de una ruta mal escrita). Si es a propósito el " +
                "prefijo de una rama con Lado heterogéneo entre sus hijos, agregalo a " +
                "exentosPorRamaEntera con el motivo -- no lo dejes caer en silencio.");

            var enHtml = Lee(html, campo!.Token);
            var enReact = Lee(react, campo.Token);
            var esperado = enHtml == enReact
                ? (enHtml ? (Lado?)null : Lado.Ninguno)
                : (enHtml ? Lado.Html : Lado.React);

            Assert.True(esperado == ladoDeclarado,
                $"'{ruta}' está declarada como {ladoDeclarado} en Asimetrias, pero la asimetría real de " +
                $"hoy es {(esperado is null ? "ninguna: los dos renderizadores ya lo leen" : esperado.Value.ToString())}. " +
                $"El motivo que la declaró (\"{motivo}\") describía otra realidad: si los dos lados ya " +
                "lo leen, borrá la entrada; si cambió de lado, corregí el Lado.");
        }
    }

    // ================== inventario del modelo ==================

    /// <summary>Un campo del modelo: el nombre con el que viaja en el JSON y todas las rutas que lo
    /// producen (un mismo nombre corto vive bajo varios padres).</summary>
    public sealed record Campo(string Token, string Rutas);

    private static List<Campo>? _inventario;

    /// <summary>
    /// Los nombres JSON de <see cref="ModeloInformeValor"/>, agrupados por token. Sale por reflexión y
    /// no de una lista a mano: un campo nuevo del modelo entra solo al contrato y hay que decir quién
    /// lo lee. Una lista a mano se habría quedado corta en la primera entrega siguiente.
    /// </summary>
    public static List<Campo> Inventario()
    {
        if (_inventario is not null) return _inventario;

        var rutas = new List<(string Token, string Ruta)>();
        Recorrer(typeof(ModeloInformeValor), "", rutas, []);

        _inventario = rutas
            .GroupBy(r => r.Token, StringComparer.Ordinal)
            .Select(g => new Campo(g.Key, string.Join(", ", g.Select(x => x.Ruta).Distinct().Order(StringComparer.Ordinal))))
            .OrderBy(c => c.Token, StringComparer.Ordinal)
            .ToList();
        return _inventario;
    }

    private static void Recorrer(Type tipo, string prefijo, List<(string, string)> salida, HashSet<Type> enCamino)
    {
        if (!enCamino.Add(tipo)) return; // recursión de tipos: no debería pasar, pero no cuelga si pasa

        foreach (var p in tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var nombre = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            Assert.False(nombre is null,
                $"{tipo.Name}.{p.Name} no declara [JsonPropertyName]. El nombre con el que viaja al " +
                "JSON queda a merced de la política de serialización, y los dos renderizadores lo leen " +
                "por nombre.");

            var ruta = prefijo.Length == 0 ? nombre! : $"{prefijo}.{nombre}";
            salida.Add((nombre!, ruta));

            foreach (var hijo in Descender(p.PropertyType))
                Recorrer(hijo, ruta, salida, enCamino);
        }

        enCamino.Remove(tipo);
    }

    /// <summary>
    /// Los tipos con nombres propios que hay que abrir dentro de una propiedad. Las listas de
    /// <c>object?</c> son las filas posicionales del modelo: no tienen nombres, así que no hay nada que
    /// comparar (y es la limitación que el docstring de la clase declara). Los diccionarios se abren
    /// por su valor: su CLAVE es un dato del cliente, no un campo.
    /// </summary>
    private static IEnumerable<Type> Descender(Type tipo)
    {
        var t = Nullable.GetUnderlyingType(tipo) ?? tipo;

        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(object)) yield break;

        if (typeof(IEnumerable).IsAssignableFrom(t) && t.IsGenericType)
        {
            // IReadOnlyDictionary<K,V> -> V; IReadOnlyList<T> -> T.
            var args = t.GetGenericArguments();
            foreach (var hijo in Descender(args[^1])) yield return hijo;
            yield break;
        }

        if (t.Namespace?.StartsWith("OptimizacionCostos.Api.Features.InformeValor", StringComparison.Ordinal) == true)
            yield return t;
    }

    // ================== los dos textos fuente ==================

    /// <summary>
    /// La capa de dibujo de la plantilla embebida: desde <c>render()</c> hasta la sección de carga de
    /// archivos, más los helpers de formato que están justo antes (<c>anualizado()</c> lee
    /// <c>ahorro.anualizada</c> y <c>ahorro.mesesSostenido</c> por su cuenta). Más arriba del archivo
    /// siguen las funciones <c>calcXxx</c> del generador manual, que producen los nombres VIEJOS: son
    /// código muerto en este camino y buscarlas daría falsos positivos.
    /// </summary>
    internal static string CapaDeDibujoHtml()
    {
        var t = InformeValorHtmlExporter.Plantilla;
        var ini = t.IndexOf("function gastoUltCompleto(f){", StringComparison.Ordinal);
        Assert.True(ini > 0, "no se encontró el inicio de la capa de dibujo de la plantilla embebida");
        var fin = t.IndexOf("7. CARGA DE ARCHIVOS", ini, StringComparison.Ordinal);
        Assert.True(fin > ini, "no se encontró el final de la capa de dibujo");
        return t[ini..fin];
    }

    /// <summary>Los archivos de la vista React que dibujan el modelo, concatenados.</summary>
    internal static class FuentesReact
    {
        /// <summary>Carpeta de la vista del informe y helper suelto que consume el modelo. Los tests
        /// quedan fuera: un campo que solo aparece en un test no lo ve ningún lector. La pestaña de
        /// entrega también queda fuera: dibuja el formulario de publicación, no el informe.</summary>
        private static readonly string[] Rutas =
        [
            "src/components/informe-valor/informe",
            "src/lib/informeValor.ts",
        ];

        public static (string? Texto, string? PorQueNo) Resolver()
        {
            var raiz = Environment.GetEnvironmentVariable("INFORME_VALOR_FRONT");
            if (string.Equals(raiz, "ninguno", StringComparison.OrdinalIgnoreCase))
                return (null, "INFORME_VALOR_FRONT=ninguno: este entorno declara que no tiene el repo " +
                              "del front, así que la comparación entre los dos renderizadores no corre. " +
                              "La mitad que sí corre es La_plantilla_no_lee_los_campos_declarados_fuera_de_ella.");

            raiz ??= Path.GetFullPath(Path.Combine(RaizDeEsteRepo(), "..", "innovacion-CDC"));

            var archivos = new List<string>();
            foreach (var r in Rutas)
            {
                var p = Path.Combine(raiz, r.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(p))
                    archivos.AddRange(Directory.GetFiles(p, "*.ts*", SearchOption.AllDirectories));
                else if (File.Exists(p))
                    archivos.Add(p);
            }
            archivos.RemoveAll(a => a.Contains(".test.", StringComparison.Ordinal));

            Assert.True(archivos.Count > 0,
                $"No se encontró la vista React del informe en '{raiz}'. El test de contrato de F0 " +
                "necesita los dos renderizadores. Opciones: cloná innovacion-CDC como carpeta hermana " +
                "de este repo, apuntá INFORME_VALOR_FRONT a su raíz, o poné INFORME_VALOR_FRONT=ninguno " +
                "para declarar que este entorno solo tiene un repo (y perder esa mitad del test).");

            var sb = new StringBuilder();
            foreach (var a in archivos.Order(StringComparer.Ordinal)) sb.AppendLine(File.ReadAllText(a));
            return (sb.ToString(), null);
        }

        private static string RaizDeEsteRepo([CallerFilePath] string archivoDeEstaPrueba = "")
        {
            // <raiz>/tests/OptimizacionCostos.Api.Tests/InformeValor/Entrega/<este archivo>
            return Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(archivoDeEstaPrueba)!, "..", "..", "..", ".."));
        }
    }

    // ================== el barrido ==================

    /// <summary>
    /// Saca los comentarios. Los dos lados documentan sus decisiones citando nombres de campo
    /// (<c>l.contada</c> en la plantilla, <c>fact.ahorro.dif</c> y <c>advisor.porSub</c> en los
    /// docstrings del TSX), y un campo citado en prosa no lo lee nadie: sin este paso, la mitad de las
    /// asimetrías quedaría tapada por su propia documentación.
    /// </summary>
    internal static string Normalizar(string fuente)
    {
        var sinBloques = Regex.Replace(fuente, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(sinBloques, @"(?<!:)//[^\r\n]*", " ");
    }

    /// <summary>
    /// Si un renderizador lee ese nombre: acceso por miembro (<c>f.cargaRet</c>) o desestructurado de
    /// una asignación (<c>const { meta } = modelo</c>), que es como la vista React toma alguna clave de
    /// nivel superior.
    ///
    /// <para>El desestructurado exige la llave Y el igual. Con solo "el nombre precedido de coma"
    /// alcanzaba, cualquier PARÁMETRO de función homónimo contaba como lectura: un helper
    /// <c>function eje(v,medido,…)</c> hacía pasar por leído a <c>reservas.medido</c>, que ningún
    /// renderizador dibuja. Un falso positivo acá no molesta: tapa una divergencia.</para>
    /// </summary>
    internal static bool Lee(string fuenteNormalizada, string token)
    {
        var t = Regex.Escape(token);
        return Regex.IsMatch(fuenteNormalizada, $@"\.{t}(?![\w$])")
            || Regex.IsMatch(fuenteNormalizada, $@"\{{[^{{}}\r\n]*(?<![\w$.]){t}(?![\w$])[^{{}}\r\n]*\}}\s*=");
    }

    /// <summary>Si una entrada de <see cref="Asimetrias"/> cubre esa ruta: la ruta exacta o cualquier
    /// rama debajo.</summary>
    private static bool Cubre(string declarada, string ruta) =>
        ruta.Equals(declarada, StringComparison.Ordinal)
        || ruta.StartsWith(declarada + ".", StringComparison.Ordinal);

    /// <summary>
    /// El motivo declarado para ese campo, o <c>null</c> si no hay ninguno. Un token corto puede venir
    /// de varias rutas: se excusa solo si TODAS están declaradas del mismo lado. Con una sola ruta sin
    /// declarar, el campo se reporta — el error queda del lado de reportar de más, no de tapar.
    /// </summary>
    private static string? MotivoDeclarado(Campo campo, Lado lado)
    {
        var rutas = campo.Rutas.Split(", ");
        var motivos = new List<string>();
        foreach (var ruta in rutas)
        {
            var d = Asimetrias.FirstOrDefault(a => a.Lado == lado && Cubre(a.Ruta, ruta));
            if (d.Motivo is null) return null;
            motivos.Add(d.Motivo);
        }
        return string.Join(" / ", motivos.Distinct());
    }
}
