using System.Text.RegularExpressions;
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
/// hallazgo de "identidades sin nombre" se extiende con el mismo criterio (ver el comentario de
/// <see cref="ConstruirHallazgos"/>): no está nombrado en D9, pero depende exactamente del mismo
/// eje por el mismo motivo, documentado como divergencia adicional en el reporte de la tarea.</para>
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

        var owner = filas.Count(f => RolOwner.IsMatch(f.Rol));
        var uaa = filas.Count(f => RolUaa.IsMatch(f.Rol));
        var contrib = filas.Count(f => RolContributor.IsMatch(f.Rol));
        var priv = filas.Count(f => RolPrivilegiado.IsMatch(f.Rol));

        int? sinLogin = ejes.UltimoLoginMedido
            ? usr.Count(f => string.IsNullOrWhiteSpace(f.UltimoLoginTexto))
            : null;
        int? disab = ejes.EstadoCuentaMedido
            ? filas.Count(f => f.CuentaHabilitada == false)
            : null;
        var sinNombre = filas.Count(f => string.IsNullOrWhiteSpace(f.Nombre));

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

    private static IReadOnlyList<IReadOnlyList<object?>> AgruparRoles(IReadOnlyList<RbacFila> filas) =>
        filas.GroupBy(f => f.Rol)
            .Select(g => (IReadOnlyList<object?>)[g.Key, g.Count(), RolPrivilegiado.IsMatch(g.Key)])
            .OrderByDescending(r => (int)r[1]!)
            .ToList();

    /// <summary>D12: agrupa por el conjunto COMPLETO de suscripciones que alcanza cada fila
    /// (<see cref="RbacFila.SuscripcionesAlcanzadas"/>), no por su <see cref="RbacFila.SubscriptionId"/>
    /// primario — ese campo es arbitrario para asignaciones heredadas de root/management group.
    /// El nombre visible se resuelve contra cualquier fila que SÍ tenga ese id como su propia
    /// suscripción primaria; si ninguna la tiene (una suscripción alcanzada solo por herencia,
    /// nunca vista de forma directa), se muestra el id crudo en vez de perder la fila.</summary>
    private static IReadOnlyList<IReadOnlyList<object?>> CalcularSuscripciones(
        IReadOnlyList<RbacFila> usr, IReadOnlyList<RbacFila> sps)
    {
        var nombrePorId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in usr.Concat(sps))
            if (f.SubscriptionId is { Length: > 0 } id && f.SubscriptionName is { Length: > 0 } nombre)
                nombrePorId.TryAdd(id, nombre);

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
        int owner, int uaa, int contrib, int? sinLogin, int sinNombre, int? disab,
        IReadOnlyList<object?>? spTop, EjesRbac ejes)
    {
        var f = new List<SeguridadHallazgo>();
        var ownerFilas = filas.Where(x => RolOwner.IsMatch(x.Rol)).ToList();
        var uaaFilas = filas.Where(x => RolUaa.IsMatch(x.Rol)).ToList();
        var contribFilas = filas.Where(x => RolContributor.IsMatch(x.Rol)).ToList();

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
        // estado de cuenta (GraphComplete), así que se suprime con el mismo criterio.
        if (ejes.EstadoCuentaMedido && sinNombre > 0)
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
