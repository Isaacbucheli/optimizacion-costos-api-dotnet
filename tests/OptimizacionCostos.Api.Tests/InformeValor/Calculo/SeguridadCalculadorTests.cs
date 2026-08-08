using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Bloque de seguridad (Tarea 5 del plan de la entrega 2b): D9 y D12 sobre el insumo RBAC, ya
/// deduplicado como <see cref="RbacFila"/> por <see cref="RbacRecolector"/>. Puerto de
/// <c>calcRbac</c> en <c>docs/Plantilla-Dashboard-BIT.html</c>.
///
/// <para>D13 (Global Constraints/Restricciones): tres reglas de RBAC comparan un cociente de dos
/// enteros contra una fracción. Los tres tests con prefijo <c>D13_</c> reproducen exactamente el
/// caso donde la división entera de C# haría que la regla nunca disparara.</para>
/// </summary>
public sealed class SeguridadCalculadorTests
{
    private static int _n;

    private static RbacFila Fila(
        string? id = null, string? nombre = "Persona", string? login = "persona@cliente.com",
        string principalType = "User", string rol = "Reader",
        string? subscriptionId = "sub-1", string? subscriptionName = "Suscripción Uno",
        IReadOnlyList<string>? alcanza = null,
        bool? cuentaHabilitada = true, string? ultimoLogin = "2026-01-01T00:00:00Z") =>
        new(
            PrincipalObjectId: id ?? $"id-{++_n}",
            Nombre: nombre, Login: login, PrincipalType: principalType, Rol: rol,
            RoleKey: rol.ToLowerInvariant(), Scope: $"/subscriptions/{subscriptionId}", ScopeLevel: "subscription",
            SubscriptionId: subscriptionId, SubscriptionName: subscriptionName,
            SuscripcionesAlcanzadas: alcanza ?? (subscriptionId is not null ? [subscriptionId] : []),
            CuentaHabilitada: cuentaHabilitada, UltimoLoginTexto: ultimoLogin, ViaGrupoId: null,
            RoleClass: null, IsCustomRole: false);

    private static readonly EjesRbac Completo = new(EstadoCuentaMedido: true, UltimoLoginMedido: true);

    // ---------- D9: los dos hallazgos falsos no se portan ----------

    [Fact]
    public void D9_UltimoLoginMedido_false_SinActividadSesion_es_null_y_no_hay_hallazgo()
    {
        var filas = Enumerable.Range(0, 10).Select(i => Fila(id: $"u{i}", ultimoLogin: null)).ToList();
        var ejes = new EjesRbac(EstadoCuentaMedido: true, UltimoLoginMedido: false);

        var m = SeguridadCalculador.Calcular(filas, ejes)!;

        Assert.Null(m.SinActividadSesion);
        Assert.False(m.UltimoLoginMedido);
        Assert.DoesNotContain(m.Hallazgos, h => h.Titulo.Contains("actividad de sesión"));
    }

    [Fact]
    public void D9_UltimoLoginMedido_true_con_100pct_en_blanco_SI_dispara_el_hallazgo_real()
    {
        // Medido de verdad y da 100% en blanco: acá SÍ es un hallazgo legítimo, no fabricado.
        var filas = Enumerable.Range(0, 4).Select(i => Fila(id: $"u{i}", ultimoLogin: null)).ToList();

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Equal(4, m.SinActividadSesion);
        Assert.Contains(m.Hallazgos, h => h.Titulo.Contains("actividad de sesión") && h.Severidad == "Alta");
    }

    [Fact]
    public void D9_EstadoCuentaMedido_false_CuentasDeshabilitadas_es_null_y_no_hay_hallazgo()
    {
        var filas = new[]
        {
            Fila(id: "u1", cuentaHabilitada: false), Fila(id: "u2", cuentaHabilitada: false), Fila(id: "u3"),
        };
        var ejes = new EjesRbac(EstadoCuentaMedido: false, UltimoLoginMedido: true);

        var m = SeguridadCalculador.Calcular(filas, ejes)!;

        Assert.Null(m.CuentasDeshabilitadas);
        Assert.False(m.EstadoCuentaMedido);
        Assert.DoesNotContain(m.Hallazgos, h => h.Titulo.Contains("deshabilitadas"));
    }

    [Fact]
    public void D9_EstadoCuentaMedido_true_cuenta_por_booleano_no_por_texto()
    {
        var filas = new[]
        {
            Fila(id: "u1", cuentaHabilitada: false), Fila(id: "u2", cuentaHabilitada: false), Fila(id: "u3", cuentaHabilitada: true),
        };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Equal(2, m.CuentasDeshabilitadas);
        Assert.Contains(m.Hallazgos, h => h.Titulo.Contains("deshabilitadas") && h.Severidad == "Crítica");
    }

    [Fact]
    public void D9_CuentaHabilitada_null_individual_no_cuenta_como_habilitada_ni_deshabilitada()
    {
        // El eje está medido en general, pero ESTA identidad puntual no resolvió: no se puede
        // afirmar nada de ella, así que no entra al numerador de "deshabilitadas".
        var filas = new[] { Fila(id: "u1", cuentaHabilitada: null) };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Equal(0, m.CuentasDeshabilitadas);
    }

    /// <summary>
    /// No nombrado explícitamente por D9 (que enumera "los DOS hallazgos"), pero mismo patrón y
    /// mismo eje: <c>AccessReviewAccountBuilder.SignInComplete</c> documenta que el nombre
    /// ("el resto del directorio") depende de <c>GraphComplete</c>, el mismo booleano que
    /// <see cref="EstadoRbac.Resolver"/> asigna a <see cref="EjesRbac.EstadoCuentaMedido"/>. Sin
    /// Graph, TODAS las identidades llegarían sin nombre y el hallazgo "no resuelven nombre"
    /// dispararía sobre el 100%, igual de fabricado que los otros dos. Se documenta como
    /// divergencia adicional en el reporte, no en D9 en sí.
    /// </summary>
    [Fact]
    public void D9_extendido_EstadoCuentaMedido_false_tampoco_emite_el_hallazgo_de_sin_nombre()
    {
        var filas = Enumerable.Range(0, 5).Select(i => Fila(id: $"u{i}", nombre: null)).ToList();
        var ejes = new EjesRbac(EstadoCuentaMedido: false, UltimoLoginMedido: true);

        var m = SeguridadCalculador.Calcular(filas, ejes)!;

        // El conteo crudo se mantiene (el campo NO es nullable en el contrato): lo que se
        // suprime es el hallazgo/recomendación, no el dato.
        Assert.Equal(5, m.SinNombreResuelto);
        Assert.DoesNotContain(m.Hallazgos, h => h.Titulo.Contains("no resuelven nombre"));
    }

    [Fact]
    public void SinNombre_SI_dispara_cuando_el_eje_esta_medido()
    {
        var filas = new[] { Fila(id: "u1", nombre: null), Fila(id: "u2") };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Equal(1, m.SinNombreResuelto);
        Assert.Contains(m.Hallazgos, h => h.Titulo.Contains("no resuelven nombre"));
    }

    // ---------- D13 (Restricciones): division entera de C# vs cociente real ----------

    [Fact]
    public void D13_SinActividadSesion_dispara_con_60pct_pese_a_que_la_division_entera_de_6_10_es_0()
    {
        Assert.Equal(0, 6 / 10); // la trampa que Division.Cociente evita
        var filas = Enumerable.Range(0, 10)
            .Select(i => Fila(id: $"u{i}", ultimoLogin: i < 6 ? null : "2026-01-01T00:00:00Z"))
            .ToList();

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Equal(6, m.SinActividadSesion);
        Assert.Contains(m.Hallazgos, h => h.Titulo.Contains("actividad de sesión"));
    }

    [Fact]
    public void D13_Contributor_dispara_con_25pct_pese_a_que_la_division_entera_de_2_8_es_0()
    {
        Assert.Equal(0, 2 / 8);
        var filas = Enumerable.Range(0, 6).Select(i => Fila(id: $"r{i}", rol: "Reader"))
            .Concat(Enumerable.Range(0, 2).Select(i => Fila(id: $"c{i}", rol: "Contributor")))
            .ToList();

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Equal(2, m.Contributor);
        Assert.Contains(m.Hallazgos, h => h.Titulo.Contains("Contributor"));
    }

    [Fact]
    public void D13_ConcentracionDeSp_dispara_con_80pct_pese_a_que_la_division_entera_de_4_5_es_0()
    {
        Assert.Equal(0, 4 / 5);
        var filas = Enumerable.Range(0, 4)
            .Select(i => Fila(id: $"sp{i}", principalType: "ServicePrincipal", rol: "Reader",
                subscriptionId: "sub-x", subscriptionName: "Suscripción X"))
            .Append(Fila(id: "sp4", principalType: "ServicePrincipal", rol: "Reader",
                subscriptionId: "sub-y", subscriptionName: "Suscripción Y"))
            .ToList();

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Contains(m.Hallazgos, h => h.Titulo.Contains("un solo ambiente"));
    }

    // ---------- D12: las tres cifras de suscripciones se concilian (en el ensamblador; este
    // bloque solo tiene que publicar SU PROPIA vista, correctamente agregada) ----------

    [Fact]
    public void D12_Una_asignacion_heredada_de_management_group_se_cuenta_en_cada_suscripcion_que_alcanza()
    {
        var filas = new[]
        {
            // Owner heredado: alcanza dos suscripciones. Sub-b se nombra por otra fila directa.
            Fila(id: "owner-1", rol: "Owner", subscriptionId: "sub-a", subscriptionName: "Suscripción A",
                alcanza: ["sub-a", "sub-b"]),
            Fila(id: "reader-1", rol: "Reader", subscriptionId: "sub-b", subscriptionName: "Suscripción B"),
        };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        var subA = m.Suscripciones.Single(s => (string)s[0]! == "Suscripción A");
        var subB = m.Suscripciones.Single(s => (string)s[0]! == "Suscripción B");
        Assert.Equal(1, (int)subA[1]!); // solo el owner heredado
        Assert.Equal(2, (int)subB[1]!); // el owner heredado + el reader directo
    }

    [Fact]
    public void D12_Una_suscripcion_alcanzada_sin_nombre_propio_conocido_cae_al_id_como_texto()
    {
        var filas = new[]
        {
            Fila(id: "owner-1", rol: "Owner", subscriptionId: "sub-conocida", subscriptionName: "Suscripción Conocida",
                alcanza: ["sub-conocida", "sub-fantasma"]),
        };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Contains(m.Suscripciones, s => (string)s[0]! == "sub-fantasma");
    }

    [Fact]
    public void D12_SuscripcionTopServicePrincipal_es_null_sin_ninguna_asignacion_de_SP()
    {
        var filas = new[] { Fila(id: "u1", principalType: "User") };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Null(m.SuscripcionTopServicePrincipal);
    }

    [Fact]
    public void D12_SuscripcionTopServicePrincipal_identifica_la_de_mas_asignaciones_de_SP()
    {
        var filas = new[]
        {
            Fila(id: "sp1", principalType: "ServicePrincipal", subscriptionId: "sub-x", subscriptionName: "Suscripción X"),
            Fila(id: "sp2", principalType: "ServicePrincipal", subscriptionId: "sub-x", subscriptionName: "Suscripción X"),
            Fila(id: "sp3", principalType: "ServicePrincipal", subscriptionId: "sub-y", subscriptionName: "Suscripción Y"),
        };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Equal("Suscripción X", m.SuscripcionTopServicePrincipal![0]);
    }

    // ---------- Comportamiento general (paridad e invariantes, no decisión) ----------

    [Fact]
    public void Sin_filas_el_bloque_es_null()
    {
        Assert.Null(SeguridadCalculador.Calcular([], Completo));
    }

    [Fact]
    public void Total_es_siempre_usuarios_mas_service_principals()
    {
        var filas = new[]
        {
            Fila(id: "u1", principalType: "User"),
            Fila(id: "sp1", principalType: "ServicePrincipal"),
            Fila(id: "g1", principalType: "Group"), // sin equivalente en la plantilla: cuenta como usuario
        };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Equal(m.Usuarios + m.ServicePrincipals, m.Total);
        Assert.Equal(2, m.Usuarios); // User + Group
        Assert.Equal(1, m.ServicePrincipals);
    }

    [Fact]
    public void Identidades_usa_el_object_id_como_respaldo_final_cuando_no_hay_login_ni_nombre()
    {
        var filas = new[]
        {
            Fila(id: "sp-1", principalType: "ServicePrincipal", nombre: null, login: null),
            Fila(id: "sp-2", principalType: "ServicePrincipal", nombre: null, login: null),
        };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Equal(2, m.IdentidadesServicePrincipals); // no 1: son dos SP distintos sin nombre
    }

    [Fact]
    public void Hallazgo_owner_declara_asignaciones_e_identidades_distintas_no_el_mismo_numero()
    {
        var filas = new[]
        {
            Fila(id: "u1", rol: "Owner", login: "misma@cliente.com", subscriptionId: "sub-a"),
            Fila(id: "u1b", rol: "Owner", login: "misma@cliente.com", subscriptionId: "sub-b"),
        };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        var h = m.Hallazgos.Single(h => h.Titulo.Contains("Owner"));
        Assert.Equal("2 asignaciones · 1 identidades", h.Alcance);
    }

    [Fact]
    public void Roles_agrupa_por_texto_exacto_y_marca_privilegiado_por_regex()
    {
        var filas = new[]
        {
            Fila(id: "u1", rol: "Owner"), Fila(id: "u2", rol: "Owner"), Fila(id: "u3", rol: "Reader"),
        };

        var m = SeguridadCalculador.Calcular(filas, Completo)!;

        Assert.Contains(m.Roles, r => (string)r[0]! == "Owner" && (int)r[1]! == 2 && (bool)r[2]!);
        Assert.Contains(m.Roles, r => (string)r[0]! == "Reader" && (int)r[1]! == 1 && !(bool)r[2]!);
    }

    [Fact]
    public void Criticos_no_cuenta_hallazgos_suprimidos_por_D9()
    {
        var filas = new[] { Fila(id: "u1", cuentaHabilitada: false) };
        var ejes = new EjesRbac(EstadoCuentaMedido: false, UltimoLoginMedido: false);

        var m = SeguridadCalculador.Calcular(filas, ejes)!;

        Assert.Equal(0, m.Criticos);
    }
}
