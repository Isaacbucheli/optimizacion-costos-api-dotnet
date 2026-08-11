using System.Globalization;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using static OptimizacionCostos.Api.Features.InformeValor.InsumoCellUtils;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Una fila de <c>informe_valor_rbac</c>, tal cual se persiste (texto crudo, incluidos
/// <see cref="CuentaActiva"/> y <see cref="UltimoLogin"/>: la conversión a los tipos que necesita
/// <c>RbacFila</c> pasa por <see cref="RbacFilaConverter"/>, al leer, no acá).
///
/// <para><see cref="RoleClass"/>/<see cref="IsCustomRole"/> NO tienen columna en
/// <c>informe_valor_rbac</c> (esa tabla es el contrato de destino de esta tarea y no se toca el
/// esquema): el parser los calcula igual, invirtiendo la etiqueta en español de "Clase de rol"/
/// "Rol personalizado" (ver <see cref="RbacParser"/>), pero <c>SqlInformeValorStore.RbacColumns</c>
/// no los proyecta a ninguna columna. Se pierden al persistir, a propósito y documentado: viajan
/// acá para que quien llame a <see cref="RbacParser.Parse"/> directamente (p. ej. una vista previa
/// antes de guardar) los tenga completos, pero <c>RbacFilaConverter.Convertir</c> —que solo ve lo
/// que la base guardó— siempre construye <c>RbacFila.RoleClass</c>/<c>IsCustomRole</c> en null/
/// false para un archivo que ya pasó por la base.</para>
/// </summary>
public sealed record RbacRow(
    string Hash, string? SheetName, string? Suscripcion, string? Scope, string? Nivel,
    string? Rol, string? Tipo, string? Nombre, string? Login, string? CuentaActiva, string? UltimoLogin,
    string? RoleClass, bool IsCustomRole);

/// <summary>
/// Resultado de <see cref="RbacParser.Parse"/>: el contrato de BitcostParser/CasosParser (total,
/// procesadas, descartadas, avisos) más lo propio de RBAC — qué hoja se leyó y cuáles se
/// ignoraron (decisión 1 del brief) y los dos ejes de identidad medidos por ESTE archivo
/// (decisión 3), independientes entre sí y de los que resuelve <c>EstadoRbac</c> para la base.
/// <see cref="SinIdentificar"/> son filas sin nombre ni login (decisión 2): se cuentan aparte,
/// pero NO se descartan ni se fusionan entre sí (por eso no hay <c>RowsMerged</c> acá: a
/// diferencia de BitcostParser, este parser nunca fusiona; una colisión de clave natural es
/// siempre una fila realmente idéntica a otra, igual que en CasosParser — el export de Revisión
/// de accesos ya llega deduplicado, así que este parser no reimplementa esa deduplicación).
/// </summary>
public sealed record RbacParseResult(
    IReadOnlyList<RbacRow> Rows, int RowsTotal, int RowsSkipped, int TruncatedValues,
    IReadOnlyList<string> Warnings, string HojaLeida, IReadOnlyList<string> HojasIgnoradas,
    EjesRbac Ejes, int SinIdentificar);

/// <summary>
/// Parser del Excel de RBAC de respaldo: se sube cuando la credencial del cliente no puede leer
/// los accesos (condicional, ver <c>EstadoRbac</c>). El insumo natural es el export de Revisión de
/// accesos (<see cref="AccessReviewExcelExporter"/>), que trae nueve hojas; la que sirve es
/// "Asignaciones RBAC".
///
/// <para><b>Por qué por nombre de hoja primero (decisión 1 del brief).</b> "Cambios" trae Cuenta,
/// Tipo, Rol, Nivel de scope y Suscripción: son altas y bajas contra la corrida anterior, no
/// asignaciones vigentes. "Service Principals" trae cinco de las nueve columnas de "Asignaciones
/// RBAC", y esos principals YA están ahí con tipo ServicePrincipal: parsear las dos duplica. Elegir
/// por cabecera sin mirar el nombre de hoja matchearía cualquiera de las tres. Por eso la regla es:
/// nombre exacto primero; solo si ninguna hoja se llama "Asignaciones RBAC" se cae a detección por
/// cabecera, y ahí se excluyen por nombre las otras ocho del export (ver
/// <see cref="HojasDelExportAExcluir"/>) para no volver a matchear "Cambios"/"Service Principals"
/// bajo un nombre no estándar.</para>
///
/// <para><b>La identidad es más débil que por la vía de la base (decisión 2).</b> El Excel no trae
/// <c>PrincipalObjectId</c> (el id real de ARM/Entra): solo "Nombre" (a veces con el id adentro
/// cuando Entra no resolvió el nombre para mostrar) y "Correo / Login". Ese id se deriva más
/// adelante, en <see cref="RbacFilaConverter"/> — acá alcanza con no colapsar en una sola clave
/// natural a dos filas que no tengan ni nombre ni login (<see cref="SinIdentificar"/>): la clave de
/// esas filas incluye un contador propio del parseo para que nunca choquen entre sí, ni con una
/// fila que sí tiene identidad.</para>
/// </summary>
public static class RbacParser
{
    public const int MaxRows = 50_000;
    public const string HojaAsignaciones = "Asignaciones RBAC";

    /// <summary>Las otras ocho hojas del export de Revisión de accesos
    /// (<see cref="AccessReviewExcelExporter.Generate"/>), en el mismo orden en que esa clase las
    /// genera. Se excluyen por nombre en la detección por cabecera de respaldo.</summary>
    internal static readonly string[] HojasDelExportAExcluir =
    [
        "Resumen", "Hallazgos", "Cuentas", "Global Administrators", "Guests",
        "Cambios", "Decisiones", "Service Principals",
    ];

    private const string ErrorFormatoRbac =
        "El archivo no tiene la forma del export de Revisión de accesos: no se encontró una hoja "
        + "llamada 'Asignaciones RBAC' ni ninguna otra con sus columnas (Suscripción, Scope, Nivel, "
        + "Rol, Tipo y Nombre).";

    /// <summary>Inverso EXACTO de la función <c>Clase()</c> de <see cref="AccessReviewExcelExporter"/>
    /// (la fuente del Excel): una etiqueta que no aparezca acá —vacía, "Sin clasificar", o editada a
    /// mano— es null. Nunca se adivina una clase a partir del nombre del rol (ese es justo el defecto
    /// que <c>RbacFila.RoleClass</c> existe para evitar).</summary>
    private static readonly Dictionary<string, string> MapaClaseDeRol = new(StringComparer.Ordinal)
    {
        ["Owner (otorga accesos)"] = AccessReviewRoleClassifier.Owner,
        ["Otorga accesos"] = AccessReviewRoleClassifier.OtorgaAccesos,
        ["Escritura total"] = AccessReviewRoleClassifier.EscrituraTotal,
        ["Escritura de servicio"] = AccessReviewRoleClassifier.EscrituraServicio,
        ["Lectura"] = AccessReviewRoleClassifier.Lectura,
    };

    public static RbacParseResult Parse(Stream stream)
    {
        var hojas = XlsxRowReader.ReadSheetNames(stream);
        stream.Position = 0;
        var (hojaElegida, hojasIgnoradas) = ElegirHoja(stream, hojas);
        stream.Position = 0;

        var rows = new Dictionary<string, RbacRow>(StringComparer.Ordinal);
        var warnings = new List<string>();
        int total = 0, skipped = 0, truncated = 0, duplicadas = 0, sinIdentificar = 0;
        var huboCuentaActiva = false;
        var huboUltimoLogin = false;
        string[]? hdr = null;
        int cSub = -1, cScope = -1, cNivel = -1, cRol = -1, cClase = -1, cPers = -1,
            cTipo = -1, cNombre = -1, cLogin = -1, cCuenta = -1, cUltimo = -1;

        foreach (var row in XlsxRowReader.Read(stream, MaxRows, hojaElegida))
        {
            if (hdr is null)
            {
                // Mismo criterio que BitcostParser/CasosParser: se saltea toda fila decorativa
                // (menos de 3 celdas no vacías) hasta encontrar la cabecera real.
                if (row.Count(x => !string.IsNullOrWhiteSpace(x)) < 3) continue;
                hdr = row;
                cSub = Col(hdr, "suscripcion"); cScope = Col(hdr, "scope"); cNivel = Col(hdr, "nivel");
                cRol = Col(hdr, "rol"); cClase = Col(hdr, "clase de rol"); cPers = Col(hdr, "rol personalizado");
                cTipo = Col(hdr, "tipo"); cNombre = Col(hdr, "nombre"); cLogin = Col(hdr, "correo login");
                cCuenta = Col(hdr, "cuenta activa"); cUltimo = Col(hdr, "ultimo login");
                if (cSub < 0 || cScope < 0 || cNivel < 0 || cRol < 0 || cTipo < 0 || cNombre < 0)
                    throw new InvalidOperationException(ErrorFormatoRbac);
                continue;
            }

            total++;
            var rol = Get(row, cRol);
            var scope = Get(row, cScope);
            if (rol.Length == 0 && scope.Length == 0) { skipped++; continue; }

            var suscripcion = Get(row, cSub);
            var nivel = Get(row, cNivel);
            var tipo = Get(row, cTipo);
            var nombre = Get(row, cNombre);
            var login = Get(row, cLogin);
            var cuentaActiva = Get(row, cCuenta);
            var ultimoLogin = Get(row, cUltimo);

            // Decisión 3: cada eje se deriva de SU PROPIA columna. cCuenta/cUltimo en -1 (columna
            // ausente) nunca prende la bandera; una celda vacía tampoco, aunque la columna exista.
            if (cCuenta >= 0 && cuentaActiva.Length > 0) huboCuentaActiva = true;
            if (cUltimo >= 0 && ultimoLogin.Length > 0) huboUltimoLogin = true;

            string hash;
            if (nombre.Length > 0 || login.Length > 0)
            {
                hash = NaturalKey.Hash(suscripcion, scope, rol, tipo, nombre, login);
                if (rows.ContainsKey(hash)) { duplicadas++; skipped++; continue; }
            }
            else
            {
                // Decisión 2: no colapsar. El contador propio de este parseo garantiza que la
                // clave nunca choca con otra fila sin identidad, ni con una que sí la tiene.
                sinIdentificar++;
                hash = NaturalKey.Hash(suscripcion, scope, rol, tipo, nombre, login,
                    "sin-identificar", sinIdentificar.ToString(CultureInfo.InvariantCulture));
            }

            var roleClass = cClase >= 0 ? InvertirClaseDeRol(Get(row, cClase)) : null;
            var isCustomRole = cPers >= 0 && Norm(Get(row, cPers)) == "si";

            rows[hash] = new RbacRow(
                hash,
                Trunc(hojaElegida, 200, ref truncated),
                Trunc(suscripcion, 200, ref truncated),
                Trunc(scope, 900, ref truncated),
                Trunc(nivel, 60, ref truncated),
                Trunc(rol, 200, ref truncated),
                Trunc(tipo, 60, ref truncated),
                Trunc(nombre, 300, ref truncated),
                Trunc(login, 300, ref truncated),
                Trunc(cuentaActiva, 30, ref truncated),
                Trunc(ultimoLogin, 60, ref truncated),
                roleClass, isCustomRole);
        }

        if (hdr is null) throw new InvalidOperationException(ErrorFormatoRbac);

        if (duplicadas > 0) warnings.Add($"{duplicadas} filas se descartaron por ser idénticas a otra.");
        if (sinIdentificar > 0) warnings.Add(
            $"{sinIdentificar} filas no traen nombre ni login: se identifican con una clave interna " +
            "para no fusionarlas entre sí.");
        if (truncated > 0) warnings.Add($"{truncated} valores se recortaron por exceder el largo de su columna.");
        warnings.Add(hojasIgnoradas.Count > 0
            ? $"Se leyó la hoja '{hojaElegida}'. Se ignoraron: {string.Join(", ", hojasIgnoradas)}."
            : $"Se leyó la hoja '{hojaElegida}'.");

        return new RbacParseResult(
            rows.Values.ToList(), total, skipped, truncated, warnings,
            hojaElegida, hojasIgnoradas, new EjesRbac(huboCuentaActiva, huboUltimoLogin), sinIdentificar);
    }

    /// <summary>
    /// Decisión 1: nombre exacto primero. Solo si ninguna hoja se llama "Asignaciones RBAC" cae a
    /// detección por cabecera entre lo que NO sea ninguna de las otras ocho del export.
    /// </summary>
    private static (string Hoja, IReadOnlyList<string> Ignoradas) ElegirHoja(Stream stream, IReadOnlyList<string> hojas)
    {
        var exacta = hojas.FirstOrDefault(h => string.Equals(h?.Trim(), HojaAsignaciones, StringComparison.Ordinal));
        if (exacta is not null)
            return (exacta, [.. hojas.Where(h => h != exacta)]);

        foreach (var candidata in hojas.Where(h => !HojasDelExportAExcluir.Contains(h, StringComparer.Ordinal)))
        {
            stream.Position = 0;
            var cabecera = XlsxRowReader.Read(stream, MaxRows, candidata)
                .FirstOrDefault(r => r.Count(x => !string.IsNullOrWhiteSpace(x)) >= 3);
            if (cabecera is null) continue;

            var tieneRequeridas = new[] { "suscripcion", "scope", "nivel", "rol", "tipo", "nombre" }
                .All(req => Col(cabecera, req) >= 0);
            if (tieneRequeridas)
                return (candidata, [.. hojas.Where(h => h != candidata)]);
        }

        throw new InvalidOperationException(ErrorFormatoRbac);
    }

    /// <summary>Coincidencia exacta sobre el nombre normalizado, igual que BitcostParser.Col.</summary>
    private static int Col(string[] hdr, string wanted)
    {
        for (var i = 0; i < hdr.Length; i++)
            if (Norm(hdr[i]) == wanted) return i;
        return -1;
    }

    internal static string? InvertirClaseDeRol(string etiqueta) =>
        MapaClaseDeRol.TryGetValue(etiqueta.Trim(), out var clase) ? clase : null;
}
