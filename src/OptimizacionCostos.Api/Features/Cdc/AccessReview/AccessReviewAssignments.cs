namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>
/// Colapsa las filas que ARM devuelve repetidas para una MISMA asignación.
///
/// <para>ARM lista las asignaciones por suscripción, y una asignación heredada (nivel management
/// group o root) vuelve una vez por CADA suscripción consultada. En un tenant con 29 suscripciones
/// administradas, un solo "Billing Reader" sobre un management group llegaba como 29 filas, cada una
/// diciendo una suscripción distinta en la columna Suscripción, cuando la asignación no pertenece a
/// ninguna de ellas. Medido en un cliente real: 6013 filas crudas, de las cuales 1068 eran duplicado
/// exacto de solo 124 asignaciones (995 de nivel management group y 69 de root).</para>
///
/// <para>Eso hacía que los números del módulo se contradijeran en pantalla: el hallazgo de principals
/// eliminados contaba 407 accesos (deduplicados) mientras la tabla pintaba 619 filas en rojo, el KPI
/// "asignaciones" sobrecontaba un 21%, el "% elevadas" salía de un universo inflado, y marcar tres
/// filas que eran la misma asignación guardaba una sola decisión.</para>
///
/// <para>La clave incluye la VÍA: el mismo rol en el mismo scope heredado de dos grupos distintos son
/// dos caminos reales y hay que verlos por separado (revocar uno no quita el otro). Lo que NO incluye
/// es la suscripción bajo la que se descubrió, que es justamente el ruido. El rol se compara por su
/// GUID, no por el id completo: ARM lo prefija con la suscripción consultada (misma razón que
/// <see cref="AccessReviewAccessKey"/>).</para>
///
/// <para>Se aplica al LEER, no al persistir: la tabla guarda lo que ARM devolvió, así que las corridas
/// ya existentes se ven bien sin volver a sincronizar y la decisión es reversible.</para>
/// </summary>
public static class AccessReviewAssignments
{
    public static IReadOnlyList<AccessAssignmentRow> Distinct(IReadOnlyList<AccessAssignmentRow> rows)
    {
        var orden = new List<string>(rows.Count);
        var porClave = new Dictionary<string, Acumulado>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rows)
        {
            var clave = string.Join('|',
                r.PrincipalObjectId,
                AccessReviewRoleClassifier.RoleKey(r.RoleDefinitionId),
                r.Scope,
                r.ViaGroupId ?? "");
            if (!porClave.TryGetValue(clave, out var acc))
            {
                acc = new Acumulado(r);
                porClave[clave] = acc;
                orden.Add(clave);   // el orden de llegada es el ORDER BY de la consulta: se respeta
            }
            acc.Subscripciones.Add(r.SubscriptionId);
            if (r.SubscriptionName is not null) acc.Nombres.Add(r.SubscriptionName);
        }

        // El alcance no se pierde al colapsar: "este acceso llega a N suscripciones" es justamente lo
        // que hace grave a una asignación heredada, y las cuentas lo usan para su columna Suscripciones.
        return [.. orden.Select(k => porClave[k]).Select(a => a.Primera with
        {
            SeenInSubscriptions = [.. a.Subscripciones],
            // Por encima de la suscripción el ambiente sale de TODO lo que el acceso alcanza; de la
            // suscripción para abajo, del nombre de esa suscripción, que es el dato correcto.
            Environment = a.Primera.ScopeLevel is "root" or "management_group"
                ? AccessReviewEnvironment.ForReachedSubscriptions(a.Nombres)
                : AccessReviewEnvironment.Classify(a.Primera.SubscriptionName),
        })];
    }

    private sealed class Acumulado(AccessAssignmentRow primera)
    {
        public AccessAssignmentRow Primera { get; } = primera;
        public HashSet<string> Subscripciones { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Nombres { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
