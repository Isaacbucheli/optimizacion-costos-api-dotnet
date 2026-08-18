using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;

namespace OptimizacionCostos.Api.Features.InformeValor.Entrega;

/// <summary>El artefacto HTML autocontenido ya armado, con lo que hay que archivar de él.</summary>
public sealed record ArtefactoInforme(
    byte[] Contenido,
    string FileName,
    /// <summary>Los bloques económicos que este artefacto REALMENTE publica. Para la variante
    /// interna son los ocho; para la del cliente, los aprobados. Se archiva esto y no lo que pidió
    /// quien llamó, para que la bitácora diga lo que el archivo hace y no lo que se intentó.</summary>
    IReadOnlyList<BloqueEconomico> BloquesPublicados,
    string PlantillaVersion);

/// <summary>
/// Arma el artefacto HTML del informe de valor: la plantilla embebida con el modelo inyectado
/// (F3). La capa de dibujo de la plantilla se reusa tal cual —arranca por
/// <c>if(EMBEDDED){ D=EMBEDDED; render(); }</c>— así que este exportador no dibuja nada: inyecta,
/// recorta y saca la zona de carga.
///
/// <para><b>Tres cosas que este exportador tiene que hacer bien o el artefacto miente.</b></para>
///
/// <para><b>1. El escapado no es opcional.</b> El JSON viaja dentro de un <c>&lt;script&gt;</c>. Un
/// nombre de recurso con <c>&lt;/script&gt;</c> cierra el bloque a mitad del JSON y rompe el
/// documento entero; uno con <c>&lt;!--</c> mete al parser en estado de comentario. Los nombres de
/// recurso los elige el cliente. <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c>, que usa
/// todo el módulo de informes, NO escapa ninguno de los dos, y el <c>exportar()</c> de la plantilla
/// original tampoco. Lo hace <see cref="EscaparParaScript"/>, acá.</para>
///
/// <para><b>2. Los nombres del modelo van tal cual</b> (<see cref="InformeValorJsonOptions"/>, D13).
/// La política global del repo transforma a snake_case tanto propiedades como CLAVES DE
/// DICCIONARIO: con ella, <c>D.catSerie["Virtual Machines"]</c> no encontraría nada y el gráfico
/// dibujaría ceros bajo un título que afirma que hubo ahorro.</para>
///
/// <para><b>3. Un bloque económico apagado no es un cero</b> (F1). Para la variante del cliente,
/// los montos de los bloques no aprobados <b>se sacan del JSON</b> —no alcanza con no dibujarlos:
/// quien abre el archivo puede leer la variable— y viajan como <c>null</c>. La capa de dibujo
/// escribe "No publicado" donde iría cada uno. El modelo se calcula completo siempre: recortar acá
/// y no en el cálculo es lo que hace que las dos variantes del mismo informe sigan siendo
/// comparables y que aprobar un bloque después no obligue a recalcular.</para>
/// </summary>
public static class InformeValorHtmlExporter
{
    private const string Recurso =
        "OptimizacionCostos.Api.Features.InformeValor.Entrega.Templates.Plantilla-Dashboard-BIT.html";

    // Marcadores de la plantilla. Si alguno deja de existir, el exportador FALLA en vez de producir
    // un artefacto a medias: un informe de cliente con la zona de carga adentro es un archivo que
    // invita a arrastrarle los Excel del cliente encima.
    private const string DataAbre = "<script id=\"data\">";
    private const string DataCierra = "</script>";
    private const string CargaAbre = "<section id=\"carga\">";
    private const string CargaCierra = "</section>";
    private const string EnlaceCarga = "<a href=\"#carga\" id=\"lnk-carga\">Insumos</a>";

    private static readonly Lazy<string> PlantillaCache = new(LeerPlantilla);
    private static readonly Lazy<string> VersionCache = new(() => Huella(PlantillaCache.Value));

    /// <summary>
    /// Huella de la plantilla embebida (SHA-256, 16 hex). Se archiva con la entrega: la plantilla
    /// cambia con el repo y no con los datos, así que dos emisiones idénticas que se ven distintas
    /// se explican mirando esta columna en vez de investigando las cifras.
    /// </summary>
    public static string PlantillaVersion => VersionCache.Value;

    /// <summary>El texto de la plantilla embebida. Internal para que el test de contrato pueda
    /// auditar la capa de dibujo sin volver a abrir el recurso por su cuenta (y sin que el nombre
    /// del recurso quede escrito en dos lugares).</summary>
    internal static string Plantilla => PlantillaCache.Value;

    public static ArtefactoInforme Exportar(
        ModeloInformeValor modelo,
        VarianteInforme variante,
        IReadOnlyCollection<BloqueEconomico>? bloquesAprobados = null)
    {
        // La variante interna publica todo; la del cliente, solo lo aprobado. Los bloques aprobados
        // no se miran siquiera en la interna: pedir la interna es pedir el informe completo.
        var publicados = variante == VarianteInforme.Interna
            ? BloqueEconomicoExtensions.Todos.ToList()
            : (bloquesAprobados ?? []).Distinct().OrderBy(b => (int)b).ToList();

        var raiz = JsonSerializer.SerializeToNode(modelo, InformeValorJsonOptions.Instance)!.AsObject();
        if (variante == VarianteInforme.Cliente) Recortar(raiz, publicados);

        var json = EscaparParaScript(raiz.ToJsonString(InformeValorJsonOptions.Instance));
        var publicacion = EscaparParaScript(
            JsonSerializer.Serialize(Publicacion(variante, publicados), InformeValorJsonOptions.Instance));

        var html = PlantillaCache.Value;
        html = ReemplazarBloqueDeDatos(html, $"var EMBEDDED={json};var PUBLICACION={publicacion};");
        html = QuitarZonaDeCarga(html);

        return new ArtefactoInforme(
            // Sin BOM: la plantilla ya declara <meta charset="utf-8"> y un BOM delante del DOCTYPE
            // deja a algunos lectores en quirks mode.
            Contenido: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(html),
            FileName: NombreArchivo(modelo.Meta.Cliente, modelo.Meta.Periodo, variante),
            BloquesPublicados: publicados,
            PlantillaVersion: PlantillaVersion);
    }

    /// <summary>
    /// Lo que la capa de dibujo consulta para saber qué puede mostrar. Los ocho bloques viajan
    /// SIEMPRE, con <c>true</c> o <c>false</c> explícito: una clave ausente se leería como
    /// "publicado" (el default de <c>pub()</c>, pensado para el informe interno sin declaración) y
    /// un bloque no aprobado saldría publicado por omisión.
    /// </summary>
    private static object Publicacion(VarianteInforme variante, IReadOnlyCollection<BloqueEconomico> publicados)
    {
        var bloques = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var b in BloqueEconomicoExtensions.Todos) bloques[b.Clave()] = publicados.Contains(b);
        return new { variante = variante.Clave(), bloques };
    }

    /// <summary>
    /// Saca del JSON los montos de los bloques no aprobados. Se recorta el DATO, no solo el dibujo:
    /// el artefacto viaja al cliente y cualquiera puede leer <c>EMBEDDED</c> desde el navegador.
    ///
    /// <para>Cada monto del modelo pertenece a exactamente un bloque. Los que no se dibujan
    /// (<c>fact.subs</c>, por ejemplo) se recortan igual: no aparecer en pantalla no es lo mismo que
    /// no estar en el archivo.</para>
    /// </summary>
    private static void Recortar(JsonObject raiz, IReadOnlyCollection<BloqueEconomico> publicados)
    {
        var fact = raiz["fact"] as JsonObject;
        var advisor = raiz["advisor"] as JsonObject;

        if (!publicados.Contains(BloqueEconomico.GastoTotal))
        {
            Anular(fact, "total");
            AnularEnFilas(fact?["prom"], 2, 3);
        }

        if (!publicados.Contains(BloqueEconomico.SerieMensual))
        {
            AnularEnFilas(fact?["meses"], 1);
            AnularEnFilas(fact?["serie"], 4, 5);
            // fact.unitario (Tarea 7 de la entrega 6) deriva de fact.serie: los mismos índices 1 y 4
            // releídos, no recalculados (ver el docstring de ConsumoModelo.CostoUnitario). Mismo
            // bloque que cubre esa serie. Los índices 2 y 3 son montos (el gasto del mes y el costo
            // por recurso); el índice 1 (recursos activos) no es dinero y sigue viajando.
            AnularEnFilas(fact?["unitario"], 2, 3);
        }

        if (!publicados.Contains(BloqueEconomico.ComposicionServicio))
        {
            raiz["catSerie"] = null;
            AnularEnFilas(fact?["subs"], 1);
            AnularEnFilas((fact?["comp"] as JsonObject)?["filas"], 1, 2);
            // fact.mom (Tarea 7 de la entrega 6) deriva de los deltas de categoría de facturación:
            // mismo bloque que cubre esa composición. El mes (índice 0) y el flag de mes parcial
            // (índice 4, I4 del review final de la entrega 6) siguen viajando: ninguno de los dos es
            // dinero. Reducciones, incrementos y neto (índices 1 a 3) son montos.
            AnularEnFilas(fact?["mom"], 1, 2, 3);
        }

        if (!publicados.Contains(BloqueEconomico.AhorroActivo))
        {
            Anular(fact?["ahorro"] as JsonObject, "pico", "fin", "dif", "anualizada");
            Anular(fact, "cargaRet");
        }

        if (!publicados.Contains(BloqueEconomico.CentroCosto)) AnularEnFilas(fact?["cc"], 1);

        if (!publicados.Contains(BloqueEconomico.AhorroAdvisor))
        {
            Anular(advisor, "bruto", "real", "descarte");
            if (advisor?["savLineas"] is JsonArray lineas)
                foreach (var l in lineas) Anular(l as JsonObject, "monto");
            if (advisor?["porSub"] is JsonObject porSub)
                foreach (var kv in porSub) Anular(kv.Value as JsonObject, "ri", "sp");
        }

        // fact.variacionConsumo (los tres baldes de la entrega 2d) no lo cubre ninguno de los ocho
        // bloques del spec y ningún renderizador lo dibuja todavía, así que en la variante del
        // cliente no viaja. Recortarlo entero es la única opción honesta: dejarlo pasar sería
        // publicar por descuido montos que nadie aprobó, y nulearlo campo por campo simularía una
        // sección que el consultor nunca eligió publicar. Cuando esa sección tenga que llegar a un
        // cliente, necesita su propio interruptor de aprobación.
        Anular(fact, "variacionConsumo");

        // meta.conciliacion (Tarea 8 de la entrega 6, dibujado por la Tarea 7 de la entrega 7) lleva
        // los totales MENSUALES de BITCOST y del archivo de evolución: exactamente la clase de
        // cifra que SerieMensual/GastoTotal protegen detrás de su propio interruptor. La sección
        // publica sus montos SOLO cuando los dos están aprobados; si falta cualquiera de los dos,
        // el nodo entero se anula (no campo por campo, mismo criterio que
        // fact.variacionConsumo arriba) y el dibujo escribe su propia nota de "no publicado".
        if (!publicados.Contains(BloqueEconomico.GastoTotal) || !publicados.Contains(BloqueEconomico.SerieMensual))
            Anular(raiz["meta"] as JsonObject, "conciliacion");

        // ejecutado (Tarea 9 de la entrega 6): el titular del informe (decisión 2026-08-13), octava
        // clave de nivel superior desde la Tarea 6. Sin recorte propio viajaría intacto en la
        // variante del cliente y filtraría todo el acumulado a quien nadie se lo aprobó: por eso el
        // recorte nace en el mismo commit-set que el campo.
        var ejecutado = raiz["ejecutado"] as JsonObject;
        if (!publicados.Contains(BloqueEconomico.AhorroEjecutado))
        {
            // Montos fuera; conteos, porcentaje y ejes se quedan (mismo criterio que los demás
            // bloques). "declarado" (entrega 8) es dinero y se recorta con sus dos hermanos.
            Anular(ejecutado, "total", "tasaVigente", "facturado", "estimado", "declarado", "proyeccionFin");
            AnularEnFilas(ejecutado?["serie"], 1, 2);
            AnularEnFilas(ejecutado?["porOportunidad"], 1);
            AnularEnFilas(ejecutado?["proyeccion"], 1, 2);
            if (ejecutado?["catAcum"] is not null) ejecutado["catAcum"] = null;
            if (ejecutado?["filas"] is JsonArray filasEj)
                foreach (var f in filasEj.OfType<JsonObject>()) Anular(f, "monto");
            // pctGasto NO se anula: es porcentaje y viaja siempre (tarjeta 1, decisión 2026-08-13).
        }
        if (!publicados.Contains(BloqueEconomico.ReservasFacturadas))
        {
            var res = ejecutado?["reservas"] as JsonObject;
            Anular(res, "totalDemanda", "totalReserva", "totalAhorro", "ahorroAnualizado");
            if (res?["filas"] is JsonArray filasRes)
                foreach (var f in filasRes.OfType<JsonObject>()) Anular(f, "demanda", "reserva", "ahorro");
            // El respaldo desde el archivo (entrega 8) lleva los mismos montos por otra vía:
            // mismo interruptor, mismo recorte.
            if (res?["respaldo"] is JsonObject respaldo)
            {
                Anular(respaldo, "totalCargo", "totalAhorro");
                if (respaldo["filas"] is JsonArray filasResp)
                    foreach (var f in filasResp.OfType<JsonObject>()) Anular(f, "cargo", "ahorro");
            }
        }
    }

    private static void Anular(JsonObject? obj, params string[] propiedades)
    {
        if (obj is null) return;
        foreach (var p in propiedades)
            if (obj.ContainsKey(p)) obj[p] = null;
    }

    /// <summary>Anula posiciones dentro de una lista de filas posicionales (<c>fact.meses</c>,
    /// <c>fact.serie</c>...). Los índices son los que documenta <c>ConsumoModelo</c>.</summary>
    private static void AnularEnFilas(JsonNode? filas, params int[] indices)
    {
        if (filas is not JsonArray arr) return;
        foreach (var fila in arr)
        {
            if (fila is not JsonArray f) continue;
            foreach (var i in indices)
                if (i < f.Count) f[i] = null;
        }
    }

    /// <summary>
    /// Deja el JSON a salvo de irse del <c>&lt;script&gt;</c> que lo envuelve.
    ///
    /// <para>Reemplaza <c>&lt;</c>, <c>&gt;</c> y <c>&amp;</c> por su escape <c>\uXXXX</c>, que
    /// cubre de una sola vez <c>&lt;/script&gt;</c>, <c>&lt;!--</c> y <c>--&gt;</c> sin depender de
    /// reconocer secuencias. Es seguro hacerlo sobre el texto completo: en JSON esos tres
    /// caracteres solo pueden aparecer DENTRO de una cadena (la sintaxis estructural es
    /// <c>{}[]:,</c> más literales y números), y ahí <c>\uXXXX</c> es un escape válido que el
    /// parser devuelve como el carácter original. El dato no cambia; cambia cómo se escribe.</para>
    ///
    /// <para>U+2028 y U+2029 van también: son válidos dentro de una cadena JSON, pero el navegador
    /// no está leyendo JSON sino código JavaScript, y hasta ES2019 eran terminadores de línea que
    /// partían la cadena en dos. Pueden venir en una celda de Excel.</para>
    /// </summary>
    internal static string EscaparParaScript(string json)
    {
        var sb = new StringBuilder(json.Length + 64);
        foreach (var c in json)
        {
            switch (c)
            {
                case '<': sb.Append("\\u003c"); break;
                case '>': sb.Append("\\u003e"); break;
                case '&': sb.Append("\\u0026"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string ReemplazarBloqueDeDatos(string html, string contenido)
    {
        var ini = html.IndexOf(DataAbre, StringComparison.Ordinal);
        if (ini < 0) throw new InvalidOperationException(
            $"La plantilla embebida no tiene el bloque {DataAbre}: sin él no hay dónde inyectar el modelo.");
        var desde = ini + DataAbre.Length;
        var fin = html.IndexOf(DataCierra, desde, StringComparison.Ordinal);
        if (fin < 0) throw new InvalidOperationException(
            "La plantilla embebida tiene el bloque de datos sin cerrar.");
        return string.Concat(html.AsSpan(0, desde), contenido, html.AsSpan(fin));
    }

    /// <summary>
    /// Saca la zona de carga de archivos y su enlace del menú. El artefacto es un informe cerrado:
    /// no tiene que ofrecerle a nadie arrastrarle Excel encima, y su capa de dibujo ya no sabe
    /// calcular desde archivos (esta copia de la plantilla dibuja el modelo de C#, ver su
    /// cabecera). Falla si los marcadores no están, en vez de entregar el informe con la zona
    /// adentro.
    /// </summary>
    private static string QuitarZonaDeCarga(string html)
    {
        var ini = html.IndexOf(CargaAbre, StringComparison.Ordinal);
        if (ini < 0) throw new InvalidOperationException(
            $"La plantilla embebida no tiene {CargaAbre}: hay que revisar si el artefacto sigue saliendo sin la zona de carga.");
        var fin = html.IndexOf(CargaCierra, ini, StringComparison.Ordinal);
        if (fin < 0) throw new InvalidOperationException("La zona de carga de la plantilla quedó sin cerrar.");
        html = html.Remove(ini, fin + CargaCierra.Length - ini);

        var enlace = html.IndexOf(EnlaceCarga, StringComparison.Ordinal);
        if (enlace < 0) throw new InvalidOperationException(
            "La plantilla embebida no tiene el enlace del menú a la zona de carga: si cambió, hay que actualizar el marcador o el menú va a apuntar a una sección que no existe.");
        return html.Remove(enlace, EnlaceCarga.Length);
    }

    /// <summary>
    /// Nombre de descarga. Lleva la variante a propósito (tercera salvaguarda del spec: "los dos
    /// artefactos salen con nombre distinto"), y el período, para que dos entregas del mismo
    /// cliente no se pisen en la carpeta de descargas de nadie.
    /// </summary>
    internal static string NombreArchivo(string? cliente, string? periodo, VarianteInforme variante)
    {
        var partes = new[]
        {
            Sanear(cliente, "Cliente"),
            "Valor-Servicio-Administrado-BIT",
            Sanear(periodo, null),
            variante.Clave(),
        }.Where(p => p.Length > 0);
        return string.Join('-', partes) + ".html";
    }

    /// <summary>Deja letras (acentos incluidos), dígitos y guiones; todo lo demás se vuelve
    /// separador. Un nombre de cliente puede traer barras, comillas o dos puntos, y de acá sale un
    /// nombre de archivo y una cabecera <c>Content-Disposition</c>.</summary>
    private static string Sanear(string? valor, string? porDefecto)
    {
        var sb = new StringBuilder();
        foreach (var c in valor ?? "")
            sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : ' ');
        var limpio = string.Join('-',
            sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return limpio.Length > 0 ? limpio : porDefecto ?? "";
    }

    private static string LeerPlantilla()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(Recurso)
            ?? throw new InvalidOperationException($"No se encontró el recurso embebido '{Recurso}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Huella(string plantilla)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plantilla));
        return Convert.ToHexString(hash)[..16].ToLower(CultureInfo.InvariantCulture);
    }
}
