using System.Text.Json.Serialization;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de seguridad: RBAC. Nombres sacados de <c>calcRbac</c> en
/// <c>docs/Plantilla-Dashboard-BIT.html</c>; <c>D.rbac</c> en el modelo embebido. Contrato de la
/// Tarea 2 del plan de la entrega 2b: la Tarea 5 (D9, D12) implementa el cálculo, a partir de
/// <see cref="RbacFila"/> (ya deduplicada por <see cref="RbacRecolector"/>).
///
/// <para><b>D9: los dos hallazgos falsos no se portan.</b> "Sin actividad de sesión" y "cuentas
/// deshabilitadas" son afirmaciones de seguridad que la plantilla puede fabricar cuando el dato
/// simplemente no se midió (100% de <c>ult</c> vacío ⇒ "hallazgo Alto: depurar accesos"; cualquier
/// texto que no diga "enabled"/"habilitado" ⇒ "deshabilitada", aunque sea un idioma o formato que
/// la regex no reconoce). <see cref="RbacFila.CuentaHabilitada"/> ya llega como <c>bool?</c> y
/// <see cref="RbacFila.UltimoLoginTexto"/> como texto crudo: el eje "no medido" existe en el
/// insumo. Este contrato lo propaga con <see cref="SinActividadSesion"/> y
/// <see cref="CuentasDeshabilitadas"/> como <c>int?</c> —<c>null</c> significa "no medido, no
/// publicar la cifra ni el hallazgo", nunca cero disfrazado— más los dos banderas explícitas
/// <see cref="UltimoLoginMedido"/>/<see cref="EstadoCuentaMedido"/> para que quien dibuje decida
/// entre mostrar el número o la línea de alcance sin tener que inferirlo de un <c>null</c>.</para>
///
/// <para><b>D12 (las tres cifras de suscripciones se concilian) es del ensamblador, no de este
/// bloque</b>: la conciliación cruza suscripciones de facturación, RBAC y Advisor/Matriz a la vez,
/// así que vive en el modelo de nivel superior (<see cref="ModeloInformeValor"/>), no acá. Este
/// bloque solo publica las suscripciones que ve desde RBAC (<see cref="Suscripciones"/>), igual
/// que hace <c>calcRbac</c> hoy.</para>
/// </summary>
public sealed record SeguridadModelo(
    [property: JsonPropertyName("n")] int Total,
    [property: JsonPropertyName("nu")] int Usuarios,
    [property: JsonPropertyName("ns")] int ServicePrincipals,
    [property: JsonPropertyName("ids")] int Identidades,
    [property: JsonPropertyName("idsU")] int IdentidadesUsuarios,
    [property: JsonPropertyName("idsS")] int IdentidadesServicePrincipals,
    // Suscripciones = [nombre, asignaciones de usuario, asignaciones de service principal].
    [property: JsonPropertyName("subs")] IReadOnlyList<IReadOnlyList<object?>> Suscripciones,
    // Roles/RolesServicePrincipal = [nombre del rol, cantidad, 1 si es privilegiado / 0 si no].
    [property: JsonPropertyName("roles")] IReadOnlyList<IReadOnlyList<object?>> Roles,
    [property: JsonPropertyName("rolesSp")] IReadOnlyList<IReadOnlyList<object?>> RolesServicePrincipal,
    [property: JsonPropertyName("owner")] int Owner,
    [property: JsonPropertyName("uaa")] int UserAccessAdministrator,
    [property: JsonPropertyName("contrib")] int Contributor,
    [property: JsonPropertyName("priv")] int Privilegiados,
    [property: JsonPropertyName("sinLogin")] int? SinActividadSesion,
    [property: JsonPropertyName("ultimoLoginMedido")] bool UltimoLoginMedido,
    [property: JsonPropertyName("sinNombre")] int SinNombreResuelto,
    [property: JsonPropertyName("disab")] int? CuentasDeshabilitadas,
    [property: JsonPropertyName("estadoCuentaMedido")] bool EstadoCuentaMedido,
    // SuscripcionTopServicePrincipal = [nombre, asignaciones de usuario, asignaciones de SP], o
    // null si no hay ninguna asignación de service principal.
    [property: JsonPropertyName("spTop")] IReadOnlyList<object?>? SuscripcionTopServicePrincipal,
    [property: JsonPropertyName("find")] IReadOnlyList<SeguridadHallazgo> Hallazgos,
    [property: JsonPropertyName("crit")] int Criticos);

/// <summary>Un hallazgo de seguridad (<c>find</c> de <c>calcRbac</c>).</summary>
public sealed record SeguridadHallazgo(
    [property: JsonPropertyName("s")] string Severidad,
    [property: JsonPropertyName("t")] string Titulo,
    [property: JsonPropertyName("a")] string Alcance,
    [property: JsonPropertyName("r")] string Remediacion,
    [property: JsonPropertyName("e")] string Estado);
