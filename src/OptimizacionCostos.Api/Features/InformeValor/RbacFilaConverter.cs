using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Construye <see cref="RbacFila"/> a partir de una fila de <see cref="RbacRow"/> (decisión 7 del
/// brief: "conversión al leer, no al guardar" — <c>informe_valor_rbac</c> guarda
/// <c>cuenta_activa</c>/<c>ultimo_login</c> como texto a propósito, y este es el único punto del
/// módulo que los convierte a los tipos que <c>RbacFila</c> necesita).
///
/// <para><b>La identidad por esta vía es más débil que por <see cref="RbacRecolector"/>.</b> Ese
/// recolector siempre tiene el <c>PrincipalObjectId</c> real de ARM/Entra; acá se deriva del login
/// si existe, si no del nombre, y si ninguno de los dos existe, de la clave natural de la fila
/// (única por construcción — ver <c>RbacParser</c>, que nunca colapsa dos filas sin identidad en
/// una sola). Dos identidades DISTINTAS que compartan el mismo nombre sin login seguirían
/// colapsando en una — ese residual no se puede evitar sin un id real, y es la misma limitación
/// que ya tenía la plantilla (ver <c>docs/informe-valor-divergencias.md</c>, "La identidad de una
/// identidad") para el caso de dos identidades sin ninguno de los dos.</para>
///
/// <para><b>RoleClass/IsCustomRole pasan tal cual llega <see cref="RbacRow"/>.</b> Esta función NO
/// los pierde: si se le pasa una fila recién parseada (antes de guardar), los propaga intactos. La
/// pérdida real ocurre en la persistencia (<c>informe_valor_rbac</c> no tiene columnas para ellos,
/// ver el comentario de clase de <see cref="RbacRow"/>): <c>SqlInformeValorStore.GetRbacAsync</c>
/// reconstruye <see cref="RbacRow"/> desde la base con esos dos campos ya en null/false, así que un
/// <see cref="RbacFila"/> construido después de un ciclo completo de guardar-y-releer siempre los
/// tiene en null/false, no por esta función sino por lo que la fila reconstruida trae.</para>
///
/// <para><b>ViaGrupoId y SubscriptionId son SIEMPRE null por esta vía</b>, incluso antes de
/// guardar: el export trae el NOMBRE del grupo ("Vía grupo") y no su id, y <see cref="RbacFila"/>
/// no tiene un campo para ese nombre; y trae el nombre de la suscripción, no su id. No hay una
/// derivación honesta posible para ninguno de los dos (a diferencia de RoleKey, que sí puede
/// caer de vuelta al nombre del rol): quedan null siempre.</para>
/// </summary>
internal static class RbacFilaConverter
{
    internal static RbacFila Convertir(RbacRow row)
    {
        var principalObjectId =
            !string.IsNullOrWhiteSpace(row.Login) ? row.Login
            : !string.IsNullOrWhiteSpace(row.Nombre) ? row.Nombre
            : $"archivo:{row.Hash}";

        var suscripcion = string.IsNullOrWhiteSpace(row.Suscripcion) ? null : row.Suscripcion;

        return new RbacFila(
            PrincipalObjectId: principalObjectId,
            Nombre: row.Nombre,
            Login: row.Login,
            PrincipalType: row.Tipo ?? "",
            Rol: row.Rol ?? "",
            // RoleKey (decisión 5): sin el GUID de la definición de rol, se usa el propio nombre
            // del rol como clave de respaldo. Ninguna regla del bloque de seguridad lo consume hoy
            // (SeguridadCalculador agrupa roles por Rol, no por RoleKey: ver AgruparRoles), así que
            // esta elección no cambia ningún resultado todavía.
            RoleKey: row.Rol ?? "",
            Scope: row.Scope ?? "",
            ScopeLevel: row.Nivel ?? "",
            SubscriptionId: null,
            SubscriptionName: suscripcion,
            SuscripcionesAlcanzadas: suscripcion is null ? [] : [suscripcion],
            SuscripcionesAlcanzadasNombres: suscripcion is null ? [] : [suscripcion],
            CuentaHabilitada: ParseSiNo(row.CuentaActiva),
            UltimoLoginTexto: string.IsNullOrWhiteSpace(row.UltimoLogin) ? null : row.UltimoLogin,
            ViaGrupoId: null,
            RoleClass: row.RoleClass,
            IsCustomRole: row.IsCustomRole);
    }

    /// <summary>"Sí"/"No"/vacío -&gt; bool?. Vacío (o el campo ausente) es null, NUNCA false: false
    /// significaría "cuenta deshabilitada", una afirmación de seguridad que el archivo no hizo.
    /// Tolera "Si" sin tilde (edición manual) además de "Sí".</summary>
    private static bool? ParseSiNo(string? texto) => texto?.Trim().ToUpperInvariant() switch
    {
        "SÍ" or "SI" => true,
        "NO" => false,
        _ => null,
    };
}
