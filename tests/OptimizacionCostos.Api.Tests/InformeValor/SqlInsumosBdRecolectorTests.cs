using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican el texto del SQL expuesto y las funciones puras de mapeo, mismo
/// estilo que AdvisorRecolectorTests/MatrizRecolectorTests. El comportamiento real de estos
/// predicados contra Azure SQL real no tiene todavía un test de integración propio para esta clase;
/// el más cercano es RetirosRecolectorDbTests (gateado por BIT_INTEGRATION_DB=1), que ejercita el
/// mismo JOIN a client_azure_credentials sobre la copia de RetirosRecolector.
/// </summary>
public sealed class SqlInsumosBdRecolectorTests
{
    /// <summary>
    /// IMPORTANTE 1: el predicado de "suscripciones administradas" tenía que incluir el JOIN a
    /// client_azure_credentials con is_active=1 — el mismo que ya llevan RetirosRecolector,
    /// BoletinService.ManagedSubscriptionsAsync, AccessReviewSyncService.CredentialUnitsAsync y
    /// SqlAdvisorScoreStore. Antes de la corrección esta consulta SOLO miraba
    /// client_azure_subscriptions, así que una credencial desactivada no se notaba.
    /// </summary>
    [Fact]
    public void El_predicado_de_administradas_incluye_el_join_a_credenciales_activas()
    {
        var sql = SqlInsumosBdRecolector.SqlSuscripcionesAdministradas.Replace(" ", "").Replace("\n", "");
        Assert.Contains("innerjoindbo.client_azure_credentialsc", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("c.is_active=1", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("s.is_active=1", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("coalesce(s.is_managed,1)=1", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Devuelve los ids (subscription_id), no un COUNT: Advisor y Matriz (Importante 2)
    /// necesitan la lista completa, no solo saber si hay alguna.</summary>
    [Fact]
    public void La_consulta_de_administradas_selecciona_el_id_de_suscripcion_no_un_conteo()
    {
        var sql = SqlInsumosBdRecolector.SqlSuscripcionesAdministradas;
        Assert.Contains("SELECT s.subscription_id", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// IMPORTANTE 2 de la re-revisión: la consulta de seguridad gestionada tenía que traer también
    /// la nota (antes solo traía security_managed_externally), para que InsumosBd pueda explicar por
    /// qué el pilar de Seguridad está vacío.
    /// </summary>
    [Fact]
    public void La_consulta_de_seguridad_gestionada_tambien_trae_la_nota()
    {
        var sql = SqlInsumosBdRecolector.SqlSeguridadGestionadaExternamente;
        Assert.Contains("security_managed_externally", sql, StringComparison.Ordinal);
        Assert.Contains("security_managed_note", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// IMPORTANTE 2: el criterio exacto de ResolverNota, calcado de la tarjeta de Seguridad de
    /// WafController.Sections. Sin gestión externa no hay nada que explicar (null, aunque el
    /// cliente tenga una nota guardada de una gestión externa anterior); con gestión externa la
    /// nota propia del cliente gana, y el texto por defecto solo entra cuando no escribió ninguna.
    /// </summary>
    [Theory]
    [InlineData(false, null, null)]
    [InlineData(false, "nota de una gestion externa anterior", null)]
    [InlineData(true, null, WafConstants.SecurityManagedDefaultNote)]
    [InlineData(true, "   ", WafConstants.SecurityManagedDefaultNote)]
    [InlineData(true, "Gestionado por el CSIRT del cliente.", "Gestionado por el CSIRT del cliente.")]
    public void ResolverNota_distingue_no_gestionada_de_gestionada_sin_nota_propia(
        bool managed, string? notaCruda, string? esperado)
    {
        Assert.Equal(esperado, SqlInsumosBdRecolector.ResolverNota(managed, notaCruda));
    }

    // ---------- El cable de la condicional de RBAC: ResolverRbac/EjesDesdeArchivo como funciones
    // puras, sin base de datos (mismo mecanismo que ResolverNota arriba) ----------

    private static RbacFila Fila(string id = "u1", bool? cuentaHabilitada = null, string? ultimoLogin = null) => new(
        PrincipalObjectId: id, Nombre: "Persona", Login: $"{id}@cliente.com", PrincipalType: "User",
        Rol: "Reader", RoleKey: "reader", Scope: "/subscriptions/s1", ScopeLevel: "subscription",
        SubscriptionId: "s1", SubscriptionName: "Suscripción Uno",
        SuscripcionesAlcanzadas: ["s1"], SuscripcionesAlcanzadasNombres: ["Suscripción Uno"],
        CuentaHabilitada: cuentaHabilitada, UltimoLoginTexto: ultimoLogin, ViaGrupoId: null,
        RoleClass: null, IsCustomRole: false);

    private static EstadoRbacResultado Estado(DisponibilidadRbac disponibilidad, EjesRbac? ejes = null) =>
        new(disponibilidad, ejes ?? new EjesRbac(false, false), FechaCorrida: null, Motivo: "prueba");

    /// <summary>Decisión 4 ("gana la base"): con la base Completo, ni se mira el archivo -- ni
    /// siquiera cuando el archivo trae filas (defensivo: en producción SqlInsumosBdRecolector.LeerAsync
    /// ni siquiera pide GetRbacAsync en este caso, pero la función pura tiene que sostener la regla
    /// igual si alguna vez se la llama con las dos fuentes pobladas).</summary>
    [Fact]
    public void Con_base_completa_gana_la_base_e_ignora_el_archivo()
    {
        var estadoBase = Estado(DisponibilidadRbac.Completo, new EjesRbac(true, true));
        var rbacBase = new[] { Fila("base-1") };
        var rbacArchivo = new[] { Fila("archivo-1") };

        var (rbac, ejes, origen) = SqlInsumosBdRecolector.ResolverRbac(estadoBase, rbacBase, rbacArchivo);

        Assert.Same(rbacBase, rbac);
        Assert.Equal(estadoBase.Ejes, ejes);
        Assert.Equal(InsumosBd.OrigenBase, origen);
    }

    /// <summary>El caso que este cable existía para resolver: la base está parcial y el consultor
    /// sí cargó el Excel. Antes de esta tarea, InsumosBd.Rbac seguía viniendo de la base (o vacío)
    /// porque nadie llamaba a IInformeValorStore.GetRbacAsync.</summary>
    [Fact]
    public void Con_base_parcial_y_archivo_con_filas_usa_el_archivo_completo()
    {
        var estadoBase = Estado(DisponibilidadRbac.ParcialFaltaIdentidad, new EjesRbac(true, false));
        var rbacBase = new[] { Fila("base-1") };
        var rbacArchivo = new[] { Fila("archivo-1"), Fila("archivo-2") };

        var (rbac, _, origen) = SqlInsumosBdRecolector.ResolverRbac(estadoBase, rbacBase, rbacArchivo);

        Assert.Same(rbacArchivo, rbac);
        Assert.Equal(InsumosBd.OrigenArchivo, origen);
    }

    /// <summary>
    /// El espejo de D9 que pide la tarea: la base no pudo medir el último login (tenant sin P1,
    /// por ejemplo), pero el archivo SÍ trae la columna "Último login" con datos. Si los ejes
    /// vinieran de la base (estadoBase.Ejes), UltimoLoginMedido seguiría en false y
    /// SeguridadCalculador suprimiría un hallazgo real de "sin actividad de sesión" sobre datos que
    /// el archivo sí midió.
    /// </summary>
    [Fact]
    public void Con_archivo_como_fuente_los_ejes_son_los_del_archivo_no_los_de_la_base()
    {
        var estadoBase = Estado(DisponibilidadRbac.ParcialFaltaIdentidad, new EjesRbac(true, false));
        var rbacArchivo = new[] { Fila("a1", cuentaHabilitada: true, ultimoLogin: "2026-01-05 10:00") };

        var (_, ejes, _) = SqlInsumosBdRecolector.ResolverRbac(estadoBase, rbacBase: [], rbacArchivo: rbacArchivo);

        Assert.True(ejes.UltimoLoginMedido); // el archivo sí lo mide, aunque la base no pudiera
        Assert.True(ejes.EstadoCuentaMedido);
    }

    /// <summary>Sin archivo, se conserva exactamente lo que ya hacía el código antes de esta
    /// tarea: las filas (parciales) que la base pudo dar, con sus propios ejes.</summary>
    [Fact]
    public void Con_base_parcial_y_sin_archivo_conserva_la_base_parcial()
    {
        var estadoBase = Estado(DisponibilidadRbac.ParcialFaltaIdentidad, new EjesRbac(true, false));
        var rbacBase = new[] { Fila("base-1") };

        var (rbac, ejes, origen) = SqlInsumosBdRecolector.ResolverRbac(estadoBase, rbacBase, rbacArchivo: []);

        Assert.Same(rbacBase, rbac);
        Assert.Equal(estadoBase.Ejes, ejes);
        Assert.Equal(InsumosBd.OrigenBase, origen);
    }

    /// <summary>Sin ninguna de las dos fuentes (NoDisponible, sin corrida ni archivo) no hay
    /// origen que declarar: null, no "base" con cero filas.</summary>
    [Fact]
    public void Sin_base_ni_archivo_el_origen_es_null()
    {
        var estadoBase = Estado(DisponibilidadRbac.NoDisponible);

        var (rbac, _, origen) = SqlInsumosBdRecolector.ResolverRbac(estadoBase, rbacBase: [], rbacArchivo: []);

        Assert.Empty(rbac);
        Assert.Null(origen);
    }

    [Fact]
    public void EjesDesdeArchivo_mide_cada_eje_por_su_propia_columna()
    {
        var filas = new[]
        {
            Fila("u1", cuentaHabilitada: null, ultimoLogin: "2026-01-05 10:00"),
            Fila("u2", cuentaHabilitada: false, ultimoLogin: null),
        };

        var ejes = SqlInsumosBdRecolector.EjesDesdeArchivo(filas);

        Assert.True(ejes.EstadoCuentaMedido); // u2 sí resolvió (false, no null)
        Assert.True(ejes.UltimoLoginMedido); // u1 sí trae texto
    }

    [Fact]
    public void EjesDesdeArchivo_sin_ninguna_fila_medida_da_los_dos_ejes_en_false()
    {
        var filas = new[] { Fila("u1", cuentaHabilitada: null, ultimoLogin: null) };

        var ejes = SqlInsumosBdRecolector.EjesDesdeArchivo(filas);

        Assert.False(ejes.EstadoCuentaMedido);
        Assert.False(ejes.UltimoLoginMedido);
    }
}
