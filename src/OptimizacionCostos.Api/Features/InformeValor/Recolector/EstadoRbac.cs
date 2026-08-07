using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Qué tan disponible está el insumo de RBAC (permisos + identidad) para el informe de valor.
/// <see cref="ParcialFaltaIdentidad"/>: el inventario de permisos está completo, pero falta medir
/// alguno de los dos ejes de identidad (ver <see cref="EjesRbac"/>).
/// </summary>
public enum DisponibilidadRbac
{
    Completo,
    ParcialFaltaIdentidad,
    NoDisponible,
}

/// <summary>
/// Los dos ejes de identidad del informe, medidos por separado: una credencial sin licencia
/// Microsoft Entra ID P1 habilita <see cref="EstadoCuentaMedido"/> y no
/// <see cref="UltimoLoginMedido"/>. Un solo indicador para los dos suprimiría de más (a un cliente
/// con el estado de cuenta sí disponible) o de menos (a uno sin ningún dato de identidad).
/// </summary>
public sealed record EjesRbac(bool EstadoCuentaMedido, bool UltimoLoginMedido);

/// <summary>
/// Resultado de decidir si el insumo de RBAC de un cliente sale de la base de datos o hace falta
/// pedirle el Excel al consultor. <see cref="Motivo"/> lo lee un consultor, no un desarrollador
/// (tarjeta del insumo y trazabilidad del informe): qué falta y por qué, sin nombres de columna ni
/// de estados internos.
/// </summary>
public sealed record EstadoRbacResultado(
    DisponibilidadRbac Disponibilidad, EjesRbac Ejes, DateTime? FechaCorrida, string Motivo);

/// <summary>
/// Decide la disponibilidad del insumo de RBAC para el informe de valor. Se lee credencial por
/// credencial (<see cref="AccessReviewAccountBuilder.ArmComplete"/> y
/// <see cref="AccessReviewAccountBuilder.GraphComplete"/>), nunca del estado agregado de la
/// corrida: una credencial Lighthouse fuerza el estado de Graph a <c>no_aplica</c> y un tenant sin
/// licencia P1 lo fuerza a <c>sin_licencia_p1</c>, y las dos condiciones cierran la corrida en
/// <c>partial</c> para siempre, por naturaleza del cliente y no por una falla. Leer la
/// disponibilidad del estado agregado de la corrida dejaría a esos clientes pidiendo el Excel
/// eternamente aunque su inventario de permisos esté completo al 100%.
/// </summary>
public static class EstadoRbac
{
    /// <summary>
    /// El último inicio de sesión se pudo medir: exige el directorio completo
    /// (<see cref="AccessReviewAccountBuilder.GraphComplete"/>) y que ninguna credencial esté sin
    /// licencia P1, que es la que expone ese dato puntual. Más estricto que
    /// <c>GraphComplete</c>, que sí acepta la falta de licencia porque el resto del directorio
    /// (nombres, tipos, cuentas) no depende de ella.
    /// </summary>
    public static bool LoginMedido(AccessReviewSnapshot s) =>
        AccessReviewAccountBuilder.GraphComplete(s)
        && s.Credentials.All(c => c.GraphStatus != "sin_licencia_p1");

    /// <param name="snapshot">La corrida finalizada más reciente del cliente (<c>ok</c> o
    /// <c>partial</c>), o null si todavía no hay ninguna.</param>
    /// <param name="tieneSuscripcionesAdministradas">Sin corrida propia que lo indique (un cliente
    /// sin suscripciones administradas cierra en <c>error</c> sin filas de estado por credencial):
    /// lo resuelve el llamador con el mismo filtro que usa el sync
    /// (<c>is_active = 1 AND COALESCE(is_managed,1) = 1</c>).</param>
    public static EstadoRbacResultado Resolver(AccessReviewSnapshot? snapshot, bool tieneSuscripcionesAdministradas)
    {
        var sinMedir = new EjesRbac(false, false);

        if (!tieneSuscripcionesAdministradas)
        {
            return new EstadoRbacResultado(DisponibilidadRbac.NoDisponible, sinMedir, null,
                "El cliente no tiene suscripciones de Azure administradas: no hay accesos que revisar " +
                "de forma automática. Sube el Excel de RBAC para completar esta sección del informe.");
        }

        if (snapshot is null)
        {
            return new EstadoRbacResultado(DisponibilidadRbac.NoDisponible, sinMedir, null,
                "Todavía no hay una corrida de revisión de accesos finalizada para este cliente. " +
                "Ejecuta la revisión de accesos o sube el Excel de RBAC para completar esta sección " +
                "del informe.");
        }

        // DateTimeOffset -> DateTime en UTC: la API serializa todo DateTime como UTC con "Z"
        // (UtcDateTimeJsonConverter); .UtcDateTime conserva Kind=Utc, .DateTime no.
        var fechaCorrida = snapshot.Run.FinishedAt?.UtcDateTime;

        // El bucle de ARM va suscripción por suscripción y conserva las que sí respondieron:
        // 'error' es un inventario a medias, no uno vacío. Igual es no disponible, porque toda la
        // sección de seguridad del informe son conteos absolutos y un piso se leería como el total
        // del tenant.
        if (!AccessReviewAccountBuilder.ArmComplete(snapshot))
        {
            return new EstadoRbacResultado(DisponibilidadRbac.NoDisponible, sinMedir, fechaCorrida,
                "El inventario de permisos no se pudo leer completo: alguna suscripción falló durante " +
                "la lectura, y mostrar un conteo parcial daría una idea equivocada del total de accesos " +
                "del cliente. Sube el Excel de RBAC para completar esta sección del informe.");
        }

        var ejes = new EjesRbac(
            AccessReviewAccountBuilder.GraphComplete(snapshot),
            LoginMedido(snapshot));

        if (ejes.EstadoCuentaMedido && ejes.UltimoLoginMedido)
        {
            return new EstadoRbacResultado(DisponibilidadRbac.Completo, ejes, fechaCorrida,
                "Los permisos y los datos de identidad (estado de cuenta y último inicio de sesión) " +
                "se obtuvieron completos desde Azure.");
        }

        var motivo = ejes.EstadoCuentaMedido
            ? "El inventario de permisos y el estado de las cuentas se obtuvieron completos, pero no " +
              "la fecha del último inicio de sesión: el tenant no tiene licencia Microsoft Entra ID " +
              "P1. Sube el Excel de RBAC si necesitas ese dato en el informe."
            : "El inventario de permisos se obtuvo completo, pero no se pudieron leer los datos de " +
              "identidad de Microsoft Entra ID (estado de cuenta ni último inicio de sesión) para " +
              "este cliente. Sube el Excel de RBAC si necesitas esos datos en el informe.";

        return new EstadoRbacResultado(DisponibilidadRbac.ParcialFaltaIdentidad, ejes, fechaCorrida, motivo);
    }
}
