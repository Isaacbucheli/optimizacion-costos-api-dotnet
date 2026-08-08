using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

/// <summary>
/// ARM devuelve una asignación heredada una vez por CADA suscripción consultada. En un cliente real
/// eso eran 1068 filas duplicadas de solo 124 asignaciones (995 de nivel management group), y hacía
/// que los números del módulo se contradijeran: el hallazgo de principals eliminados contaba 407
/// accesos mientras la tabla pintaba 619 filas en rojo.
/// </summary>
public class AccessReviewAssignmentsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);

    private static AccessAssignmentRow Row(string sub, string scope = "/providers/Microsoft.Management/managementGroups/mg-1",
        string nivel = "management_group", string pid = "u1", string? via = null,
        string roleDef = "/subscriptions/{sub}/providers/Microsoft.Authorization/roleDefinitions/8e3af657") =>
        new(sub, $"Sub {sub}", null, scope, nivel, "Billing Reader", roleDef.Replace("{sub}", sub),
            pid, "User", $"N {pid}", $"{pid}@x.com", "Member", via, via is null ? null : $"Grupo {via}",
            true, Now, "enabled", "lectura", false);

    [Fact]
    public void Colapsa_la_misma_asignacion_heredada_reportada_por_cada_suscripcion()
    {
        // El mismo Billing Reader sobre el management group, visto al consultar 3 suscripciones. Y el
        // roleDefinitionId viene prefijado con cada una, que es por lo que hay que comparar el GUID.
        var rows = new[] { Row("s1"), Row("s2"), Row("s3") };

        var efectivas = AccessReviewAssignments.Distinct(rows);

        var unica = Assert.Single(efectivas);
        // El alcance no se pierde: son 3 suscripciones alcanzadas por UNA asignación.
        Assert.Equal(3, unica.SeenInSubscriptions!.Count);
    }

    /// <summary>
    /// Informe de valor (Tarea 8): el nombre de cada suscripción alcanzada viaja junto al id, en la
    /// misma posición. Antes de esta tarea la clase ya calculaba estos nombres (los necesitaba para
    /// resolver Environment más abajo) pero los descartaba después de usarlos.
    /// </summary>
    [Fact]
    public void Cada_suscripcion_alcanzada_conserva_su_nombre_en_la_misma_posicion()
    {
        var efectivas = AccessReviewAssignments.Distinct([Row("s1"), Row("s2"), Row("s3")]);

        var unica = Assert.Single(efectivas);
        Assert.Equal(unica.SeenInSubscriptions!.Count, unica.SeenInSubscriptionNames!.Count);
        var nombrePorId = unica.SeenInSubscriptions!.Zip(unica.SeenInSubscriptionNames!)
            .ToDictionary(par => par.First, par => par.Second);
        Assert.Equal("Sub s1", nombrePorId["s1"]);
        Assert.Equal("Sub s2", nombrePorId["s2"]);
        Assert.Equal("Sub s3", nombrePorId["s3"]);
    }

    [Fact]
    public void Dos_vias_al_mismo_acceso_siguen_siendo_dos_filas()
    {
        // Revocar la membresía de un grupo no quita el acceso que da el otro: son caminos distintos y
        // hay que verlos por separado, aunque el acceso efectivo resultante sea el mismo.
        var rows = new[] { Row("s1", via: "g1"), Row("s1", via: "g2"), Row("s1") };

        Assert.Equal(3, AccessReviewAssignments.Distinct(rows).Count);
    }

    [Fact]
    public void No_toca_lo_que_ya_era_distinto()
    {
        var rows = new[]
        {
            Row("s1", scope: "/subscriptions/s1", nivel: "subscription"),
            Row("s2", scope: "/subscriptions/s2", nivel: "subscription"),
            Row("s1", scope: "/subscriptions/s1", nivel: "subscription", pid: "u2"),
        };

        Assert.Equal(3, AccessReviewAssignments.Distinct(rows).Count);
    }

    [Fact]
    public void Distinto_rol_en_el_mismo_scope_no_se_colapsa()
    {
        var rows = new[]
        {
            Row("s1"),
            Row("s1", roleDef: "/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7"),
        };

        Assert.Equal(2, AccessReviewAssignments.Distinct(rows).Count);
    }

    [Fact]
    public void Respeta_el_orden_de_llegada()
    {
        // La consulta trae ORDER BY: colapsar no debe barajar la tabla.
        var rows = new[] { Row("s1", pid: "c"), Row("s1", pid: "a"), Row("s2", pid: "c") };

        Assert.Equal(["c", "a"], AccessReviewAssignments.Distinct(rows).Select(r => r.PrincipalObjectId));
    }

    [Fact]
    public void La_cuenta_conserva_el_alcance_en_suscripciones_tras_colapsar()
    {
        // El caso que hacía peligroso deduplicar: un Owner heredado alcanza N suscripciones, y esa es
        // la lectura correcta de su columna "Suscripciones" aunque la asignación sea una sola.
        var efectivas = AccessReviewAssignments.Distinct([Row("s1"), Row("s2"), Row("s3")]);

        var cuenta = Assert.Single(AccessReviewAccountBuilder.Build(
            new AccessReviewSnapshot(new AccessRunRef(1, 6, "ok", Now, Now, null, null),
                [new AccessCredStatus(1, "cred", "ok", "ok", null)], efectivas, [], [])));

        Assert.Equal(1, cuenta.TotalAssignments);
        Assert.Equal(3, cuenta.Subscriptions);
    }

    // ── Ambiente de un acceso por encima de la suscripción ────────────────────────
    // Inferirlo del nombre de UNA suscripción es arbitrario: es la que sobrevivió al colapsar. En un
    // cliente real, 98 de 142 filas de nivel management group afirmaban "desarrollo" o "preproducción"
    // por la suscripción bajo la que ARM las devolvió, estando el acceso por encima de todas ellas.

    [Fact]
    public void Un_acceso_heredado_que_cruza_ambientes_se_marca_transversal()
    {
        var rows = new[] { Sub("SAPPRD"), Sub("SAPDEV"), Sub("Analitica QAS") };

        Assert.Equal(AccessReviewEnvironment.Transversal,
            AccessReviewAssignments.Distinct(rows).Single().Environment);
    }

    [Fact]
    public void Un_acceso_heredado_dentro_de_un_solo_ambiente_conserva_ese_ambiente()
    {
        var rows = new[] { Sub("SAPDEV"), Sub("AnaliticaDEV") };

        Assert.Equal(AccessReviewEnvironment.Desarrollo,
            AccessReviewAssignments.Distinct(rows).Single().Environment);
    }

    [Fact]
    public void Si_ningun_nombre_alcanzado_clasifica_no_se_afirma_ambiente()
    {
        var rows = new[] { Sub("Ambiente de Redes"), Sub("Utilidades") };

        Assert.Equal(AccessReviewEnvironment.Desconocido,
            AccessReviewAssignments.Distinct(rows).Single().Environment);
    }

    [Fact]
    public void A_nivel_de_suscripcion_el_ambiente_sigue_saliendo_de_su_nombre()
    {
        // De la suscripción para abajo el nombre SÍ es el dato correcto: no hay nada que colapsar.
        var row = Row("s1", scope: "/subscriptions/s1", nivel: "subscription") with { SubscriptionName = "SAPPRD" };

        Assert.Equal(AccessReviewEnvironment.Produccion,
            AccessReviewAssignments.Distinct([row]).Single().Environment);
    }

    /// <summary>Una repetición del mismo acceso heredado, vista bajo la suscripción indicada.</summary>
    private static AccessAssignmentRow Sub(string nombre) =>
        Row(nombre) with { SubscriptionName = nombre };
}
