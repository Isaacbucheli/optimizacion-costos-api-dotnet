using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de seguridad (Tarea 5 del plan de la entrega 2b): D9 sobre el insumo RBAC, ya
/// deduplicado como <see cref="RbacFila"/> por <see cref="RbacRecolector"/>. Puerto de
/// <c>calcRbac</c> en <c>docs/Plantilla-Dashboard-BIT.html</c>.
///
/// <para><b>D9.</b> Los dos hallazgos falsos ("sin actividad de sesión" y "cuentas
/// deshabilitadas") se suprimen —cifra en <c>null</c>, sin línea en <see cref="SeguridadModelo.Hallazgos"/>—
/// cuando su eje (<see cref="EjesRbac.UltimoLoginMedido"/>/<see cref="EjesRbac.EstadoCuentaMedido"/>)
/// no se midió, en vez de fabricar una afirmación de seguridad sobre el 100% del universo. El
/// hallazgo de "identidades sin nombre" se extiende con el mismo criterio (ver
/// <see cref="SeguridadModelo.SinNombreResuelto"/> y el comentario de <see cref="ConstruirHallazgos"/>):
/// D9 no lo nombra porque el análisis adversarial que lo encontró no llegó a él, no porque lo
/// hubiera descartado — depende exactamente del mismo eje (<c>GraphComplete</c>) por el mismo
/// motivo que "cuentas deshabilitadas".</para>
///
/// <para><b>D12</b> (las tres cifras de suscripciones se concilian) es del ensamblador, según el
/// propio contrato de <see cref="SeguridadModelo"/>: este bloque solo publica <em>su</em> vista de
/// suscripciones. Publicarla bien requiere expandir <see cref="RbacFila.SuscripcionesAlcanzadas"/>
/// (ver <see cref="CalcularSuscripciones"/>): <see cref="RbacFila.SubscriptionId"/>/<see cref="RbacFila.SubscriptionName"/>
/// son, por el propio comentario de esa clase, el valor arbitrario de la primera fila que ganó el
/// dedup para asignaciones heredadas de <c>root</c>/<c>management_group</c> — agrupar por esos dos
/// campos sueltos subcontaría el alcance real de esas asignaciones.</para>
///
/// <para><b>D13 (Restricciones).</b> Las tres reglas que comparan un cociente de asignaciones
/// contra una fracción usan <see cref="Division.Cociente"/>, nunca <c>int/int</c>.</para>
///
/// <para><b>La clase de rol es por <see cref="RbacFila.RoleClass"/>, alineada con Revisión de
/// accesos, con el nombre como respaldo.</b> Revisión de accesos (<see cref="AccessReviewRoleClassifier"/>)
/// clasifica cada rol por los permisos reales que otorga, nunca por su nombre: un rol personalizado
/// que dé permisos de Owner clasifica <c>owner</c> ahí sin importar cómo se llame. Clasificar acá
/// por nombre en inglés (<c>^owner$</c>, <c>^contributor$</c>) contradecía esa fuente justo en los
/// roles personalizados: uno llamado, por ejemplo, "Administrador de Producción" con permisos de
/// Owner contaba cero Owners en este informe y <c>owner</c> en Revisión de accesos, para el mismo
/// cliente. <see cref="EsOwner"/>/<see cref="EsUaa"/>/<see cref="EsContributor"/>/<see cref="EsPrivilegiado"/>
/// usan <see cref="RbacFila.RoleClass"/> cuando no es <c>null</c> (los mismos literales que
/// <see cref="AccessReviewRoleClassifier"/>: <c>owner</c>/<c>otorga_accesos</c>/<c>escritura_total</c>,
/// y <see cref="AccessReviewRoleClassifier.IsElevated"/> para "privilegiado", que deja fuera a
/// <c>escritura_servicio</c> a propósito — un <c>*Contributor</c> de un solo servicio, ver el
/// comentario de esa clase) y caen al regex sobre el nombre SOLO cuando <c>RoleClass</c> es
/// <c>null</c> (rol no resoluble, o un archivo de respaldo sin la columna "Clase de rol").</para>
/// </summary>
public static class SeguridadCalculador
{
    private static readonly Regex RolOwner = new(@"^owner$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RolUaa = new(
        "user access administrator|role based access control administrator",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RolContributor = new(@"^contributor$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RolPrivilegiado = new(
        @"^(owner|contributor|user access administrator|role based access control administrator)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Owner por <see cref="RbacFila.RoleClass"/> (mismo literal que
    /// <see cref="AccessReviewRoleClassifier.Owner"/>), con el nombre en inglés como respaldo
    /// cuando la clase no está disponible.</summary>
    private static bool EsOwner(RbacFila f) =>
        f.RoleClass is { } clase ? clase == AccessReviewRoleClassifier.Owner : RolOwner.IsMatch(f.Rol);

    /// <summary>User Access Administrator / Role Based Access Control Administrator por
    /// <see cref="RbacFila.RoleClass"/> (<see cref="AccessReviewRoleClassifier.OtorgaAccesos"/>),
    /// con el nombre como respaldo.</summary>
    private static bool EsUaa(RbacFila f) =>
        f.RoleClass is { } clase ? clase == AccessReviewRoleClassifier.OtorgaAccesos : RolUaa.IsMatch(f.Rol);

    /// <summary>Contributor por <see cref="RbacFila.RoleClass"/> (<see cref="AccessReviewRoleClassifier.EscrituraTotal"/>:
    /// escritura sobre cualquier provider, sin poder otorgar accesos — exactamente los permisos de
    /// Contributor), con el nombre como respaldo.</summary>
    private static bool EsContributor(RbacFila f) =>
        f.RoleClass is { } clase ? clase == AccessReviewRoleClassifier.EscrituraTotal : RolContributor.IsMatch(f.Rol);

    /// <summary>Privilegiado por <see cref="AccessReviewRoleClassifier.IsElevated"/> (owner,
    /// otorga_accesos o escritura_total — deja fuera a escritura_servicio a propósito, igual que
    /// Revisión de accesos), con el nombre como respaldo.</summary>
    private static bool EsPrivilegiado(RbacFila f) =>
        f.RoleClass is { } clase ? AccessReviewRoleClassifier.IsElevated(clase) : RolPrivilegiado.IsMatch(f.Rol);

    public static SeguridadModelo? Calcular(IReadOnlyList<RbacFila> filas, EjesRbac ejes)
    {
        if (filas.Count == 0) return null;

        var usr = filas.Where(f => f.PrincipalType != "ServicePrincipal").ToList();
        var sps = filas.Where(f => f.PrincipalType == "ServicePrincipal").ToList();

        var idsU = usr.Select(Identidad).Distinct().Count();
        var idsS = sps.Select(Identidad).Distinct().Count();

        var suscripciones = CalcularSuscripciones(usr, sps);
        var spTop = sps.Count == 0
            ? null
            : suscripciones.OrderByDescending(s => (int)s[2]!).First();

        var owner = filas.Count(EsOwner);
        var uaa = filas.Count(EsUaa);
        var contrib = filas.Count(EsContributor);
        var priv = filas.Count(EsPrivilegiado);

        int? sinLogin = ejes.UltimoLoginMedido
            ? usr.Count(f => string.IsNullOrWhiteSpace(f.UltimoLoginTexto))
            : null;
        int? disab = ejes.EstadoCuentaMedido
            ? filas.Count(f => f.CuentaHabilitada == false)
            : null;
        // Mismo eje que disab (D9 extendido: ver el comentario de clase): sin Graph, un cero
        // acá no significaría "todas resolvieron nombre" sino "no se pudo medir nada".
        int? sinNombre = ejes.EstadoCuentaMedido
            ? filas.Count(f => string.IsNullOrWhiteSpace(f.Nombre))
            : null;

        var hallazgos = ConstruirHallazgos(
            filas, usr, sps, owner, uaa, contrib, sinLogin, sinNombre, disab, spTop, ejes);

        return new SeguridadModelo(
            Total: filas.Count, Usuarios: usr.Count, ServicePrincipals: sps.Count,
            Identidades: idsU + idsS, IdentidadesUsuarios: idsU, IdentidadesServicePrincipals: idsS,
            Suscripciones: suscripciones,
            Roles: AgruparRoles(usr), RolesServicePrincipal: AgruparRoles(sps),
            Owner: owner, UserAccessAdministrator: uaa, Contributor: contrib, Privilegiados: priv,
            SinActividadSesion: sinLogin, UltimoLoginMedido: ejes.UltimoLoginMedido,
            SinNombreResuelto: sinNombre,
            CuentasDeshabilitadas: disab, EstadoCuentaMedido: ejes.EstadoCuentaMedido,
            SuscripcionTopServicePrincipal: spTop,
            Hallazgos: hallazgos, Criticos: hallazgos.Count(h => h.Severidad == "Crítica"));
    }

    /// <summary>Identidad distinta de una fila: login o nombre si existen; el object id de Entra
    /// como último respaldo. La plantilla usa <c>login||nombre</c> (cae a <c>''</c> si los dos
    /// faltan, así que dos identidades sin ninguno de los dos colapsan en una sola); acá siempre
    /// hay un id real disponible y no hace falta perder esa distinción.</summary>
    private static string Identidad(RbacFila f) =>
        (!string.IsNullOrWhiteSpace(f.Login) ? f.Login : null)
        ?? (!string.IsNullOrWhiteSpace(f.Nombre) ? f.Nombre : null)
        ?? f.PrincipalObjectId;

    /// <summary>Agrupa por el nombre exacto del rol (igual que antes): dos filas del mismo rol
    /// comparten nombre y, en la práctica, la misma definición y clase. "Privilegiado" por grupo
    /// es <c>Any</c> sobre el criterio por fila (<see cref="EsPrivilegiado"/>, RoleClass con
    /// respaldo por nombre): alcanza una fila privilegiada del grupo para marcarlo.</summary>
    private static IReadOnlyList<IReadOnlyList<object?>> AgruparRoles(IReadOnlyList<RbacFila> filas) =>
        filas.GroupBy(f => f.Rol)
            .Select(g => (IReadOnlyList<object?>)[g.Key, g.Count(), g.Any(EsPrivilegiado)])
            .OrderByDescending(r => (int)r[1]!)
            .ToList();

    /// <summary>D12: agrupa por el conjunto COMPLETO de suscripciones que alcanza cada fila
    /// (<see cref="RbacFila.SuscripcionesAlcanzadas"/>), no por su <see cref="RbacFila.SubscriptionId"/>
    /// primario — ese campo es arbitrario para asignaciones heredadas de root/management group. El
    /// nombre visible sale de <see cref="RbacFila.SuscripcionesAlcanzadasNombres"/> (Tarea 8: la
    /// misma fuente que ya resolvía el ambiente en <c>AccessReviewAssignments.Distinct</c>, antes
    /// descartada), zipeado posición a posición contra los ids de la fila que los trae. Ya no hace
    /// falta buscar una fila cuyo id PROPIO coincida: cualquier fila que alcance ese id ya declara
    /// su nombre. Si ninguna fila trae nombre para un id alcanzado (el insumo no lo midió), se
    /// muestra el id crudo en vez de perder la fila.</summary>
    private static IReadOnlyList<IReadOnlyList<object?>> CalcularSuscripciones(
        IReadOnlyList<RbacFila> usr, IReadOnlyList<RbacFila> sps)
    {
        var nombrePorId = usr.Concat(sps)
            .SelectMany(f => f.SuscripcionesAlcanzadas.Zip(f.SuscripcionesAlcanzadasNombres))
            .Where(par => !string.IsNullOrEmpty(par.First) && !string.IsNullOrEmpty(par.Second))
            .GroupBy(par => par.First, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Second!, StringComparer.OrdinalIgnoreCase);

        var usrPorId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var spPorId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var orden = new List<string>();

        void Contar(IReadOnlyList<RbacFila> grupo, Dictionary<string, int> destino)
        {
            foreach (var f in grupo)
            {
                var alcance = f.SuscripcionesAlcanzadas.Count > 0
                    ? f.SuscripcionesAlcanzadas
                    : (f.SubscriptionId is { Length: > 0 } idPropia ? (IReadOnlyList<string>)[idPropia] : []);
                foreach (var id in alcance)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!usrPorId.ContainsKey(id)) { usrPorId[id] = 0; spPorId[id] = 0; orden.Add(id); }
                    destino[id]++;
                }
            }
        }

        Contar(usr, usrPorId);
        Contar(sps, spPorId);

        return orden
            .Select(id => (IReadOnlyList<object?>)[
                nombrePorId.TryGetValue(id, out var n) ? n : id, usrPorId[id], spPorId[id]])
            .OrderByDescending(s => (int)s[1]! + (int)s[2]!)
            .ToList();
    }

    private static List<SeguridadHallazgo> ConstruirHallazgos(
        IReadOnlyList<RbacFila> filas, IReadOnlyList<RbacFila> usr, IReadOnlyList<RbacFila> sps,
        int owner, int uaa, int contrib, int? sinLogin, int? sinNombre, int? disab,
        IReadOnlyList<object?>? spTop, EjesRbac ejes)
    {
        var f = new List<SeguridadHallazgo>();
        var ownerFilas = filas.Where(EsOwner).ToList();
        var uaaFilas = filas.Where(EsUaa).ToList();
        var contribFilas = filas.Where(EsContributor).ToList();

        if (owner > 0)
            f.Add(new("Crítica", $"{owner} asignaciones Owner activas",
                $"{owner} asignaciones · {ownerFilas.Select(Identidad).Distinct().Count()} identidades",
                "Sustituir por Contributor más User Access Administrator segregado, y activar bajo " +
                "aprobación temporal en lugar de asignación permanente.",
                "En remediación"));

        if (uaa > 0)
            f.Add(new("Crítica", $"{uaa} identidades pueden reasignar permisos",
                $"{uaa} asignaciones · {uaaFilas.Count(x => !EsSp(x))} usuarios y {uaaFilas.Count(EsSp)} SP",
                "Reducir a un titular y un suplente por suscripción, con registro auditable de cada uso del privilegio.",
                "En remediación"));

        // D9: se suprime cuando el eje de estado de cuenta no se midió (disab es null en ese caso).
        if (ejes.EstadoCuentaMedido && disab is > 0)
            f.Add(new("Crítica", $"{disab} asignaciones sobre cuentas deshabilitadas",
                $"{disab} asignaciones",
                "Eliminar las asignaciones como parte del proceso de baja. Deshabilitar la cuenta no revoca RBAC.",
                "En remediación"));

        // D9: se suprime cuando el eje de último login no se midió (sinLogin es null en ese caso).
        if (ejes.UltimoLoginMedido && usr.Count > 0 && Division.Cociente(sinLogin ?? 0, usr.Count) > 0.5)
            f.Add(new("Alta", $"{sinLogin} de {usr.Count} asignaciones sin actividad de sesión",
                $"{sinLogin} asignaciones · {Division.Porcentaje(sinLogin ?? 0, usr.Count):F1}%",
                "Depurar por lotes con validación del dueño de cada ambiente antes de revocar el acceso.",
                "Plan definido"));

        // D9 extendido (ver el comentario de clase): el nombre depende del mismo eje que el
        // estado de cuenta (GraphComplete), así que se suprime con el mismo criterio (sinNombre
        // es null en ese caso, igual que disab y sinLogin).
        if (ejes.EstadoCuentaMedido && sinNombre is > 0)
            f.Add(new("Alta", "Identidades que no resuelven nombre en Entra",
                $"{sinNombre} de {filas.Count} asignaciones",
                "Otorgar Directory.Read.All a la cuenta de auditoría para que cada revisión futura salga con nombre y correo.",
                "Plan definido"));

        if (filas.Count > 0 && Division.Cociente(contrib, filas.Count) > 0.2)
            f.Add(new("Media", $"{contrib} asignaciones con rol Contributor",
                $"{contrib} asignaciones · {contribFilas.Count(x => !EsSp(x))} usuarios y {contribFilas.Count(EsSp)} SP",
                "Contrastar contra el inventario de recursos para identificar permisos sin uso real y retirarlos.",
                "En seguimiento"));

        if (sps.Count > 0 && spTop is not null && Division.Cociente((int)spTop[2]!, sps.Count) > 0.6)
            f.Add(new("Media", $"{Division.Porcentaje((int)spTop[2]!, sps.Count):F1}% de la automatización en un solo ambiente",
                $"{spTop[2]} de {sps.Count} asignaciones de SP",
                "Rotar secretos y migrar a identidades administradas donde el servicio lo permita.",
                "En seguimiento"));

        return f;
    }

    private static bool EsSp(RbacFila f) => f.PrincipalType == "ServicePrincipal";
}
