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
    /// <param name="snapshot">La corrida finalizada más reciente del cliente (<c>ok</c> o
    /// <c>partial</c>), o null si todavía no hay ninguna.</param>
    /// <param name="tieneSuscripcionesAdministradas">Sin corrida propia que lo indique (un cliente
    /// sin suscripciones administradas cierra en <c>error</c> sin filas de estado por credencial):
    /// lo resuelve el llamador con el mismo predicado que usa el sync, JOIN a
    /// <c>client_azure_credentials</c> incluido (<c>s.is_active = 1 AND COALESCE(s.is_managed,1) = 1
    /// AND c.is_active = 1</c>; ver <see cref="SqlInsumosBdRecolector.SqlSuscripcionesAdministradas"/>
    /// — antes de la revisión de rama este comentario todavía describía la versión sin el JOIN, que
    /// dejaba pasar credenciales desactivadas como si siguieran administradas).</param>
    public static EstadoRbacResultado Resolver(AccessReviewSnapshot? snapshot, bool tieneSuscripcionesAdministradas)
    {
        // Fijo en (false, false) en las tres ramas NoDisponible: ahí la fuente del insumo pasa a
        // ser el archivo que sube el consultor, y estos ejes describen la corrida de base, no el
        // archivo, así que no aplican. Default conservador: suprime en vez de afirmar (mejor
        // omitir un hallazgo que publicar una afirmación de seguridad falsa).
        var sinMedir = new EjesRbac(false, false);

        if (!tieneSuscripcionesAdministradas)
        {
            return new EstadoRbacResultado(DisponibilidadRbac.NoDisponible, sinMedir, null,
                "El cliente no tiene suscripciones de Azure administradas: no hay accesos que revisar " +
                "de forma automática. Para que el informe incluya el bloque de accesos, sube el Excel " +
                "de RBAC; en este estado es obligatorio.");
        }

        if (snapshot is null)
        {
            return new EstadoRbacResultado(DisponibilidadRbac.NoDisponible, sinMedir, null,
                "Todavía no hay una corrida de revisión de accesos finalizada para este cliente. " +
                "Ejecuta la revisión de accesos para completar esta sección del informe, o sube el " +
                "Excel de RBAC: en este estado es obligatorio.");
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
                "del cliente. Revisa el estado por credencial en Revisión de accesos, corrige lo que " +
                "falte y vuelve a ejecutar la corrida. Si no se puede resolver, sube el Excel de RBAC: " +
                "en este estado es obligatorio para que el informe incluya el bloque de accesos.");
        }

        var ejes = new EjesRbac(
            AccessReviewAccountBuilder.GraphComplete(snapshot),
            AccessReviewAccountBuilder.SignInComplete(snapshot));

        if (ejes.EstadoCuentaMedido && ejes.UltimoLoginMedido)
        {
            return new EstadoRbacResultado(DisponibilidadRbac.Completo, ejes, fechaCorrida,
                "Los permisos y los datos de identidad (estado de cuenta y último inicio de sesión) " +
                "se obtuvieron completos desde Azure.");
        }

        var motivo = ejes.EstadoCuentaMedido
            ? "El inventario de permisos y el estado de las cuentas se obtuvieron completos, pero no " +
              "la fecha del último inicio de sesión: el tenant no tiene licencia Microsoft Entra ID " +
              "P1. Es una limitación del tenant, no de la corrida, así que ese dato no va a estar " +
              "disponible por esta vía. Si lo necesitas en el informe, sube el Excel de RBAC como " +
              "respaldo; es opcional, y sin él el informe declara qué no se pudo medir."
            : "El inventario de permisos se obtuvo completo, pero no se pudieron leer los datos de " +
              "identidad de Microsoft Entra ID (estado de cuenta ni último inicio de sesión) para " +
              "este cliente. Si falta el consentimiento de administrador para Graph, solicítalo al " +
              "cliente con los permisos de aplicación del módulo. Mientras tanto puedes subir el " +
              "Excel de RBAC como respaldo; es opcional, y sin él el informe declara qué no se midió.";

        return new EstadoRbacResultado(DisponibilidadRbac.ParcialFaltaIdentidad, ejes, fechaCorrida, motivo);
    }
}
