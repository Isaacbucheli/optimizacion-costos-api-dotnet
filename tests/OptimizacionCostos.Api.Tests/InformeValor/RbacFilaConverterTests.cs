using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// RbacRow (lo que informe_valor_rbac guarda, texto crudo) -> RbacFila (lo que consume el bloque
/// de seguridad de la calculadora). "Conversión al leer, no al guardar" (decisión 7 del brief):
/// esta es esa conversión, probada como función pura, sin tocar la base.
/// </summary>
public sealed class RbacFilaConverterTests
{
    private static RbacRow Fila(
        string hash = "h1", string? sheetName = "Asignaciones RBAC", string? suscripcion = "Sub Uno",
        string? scope = "/subscriptions/s1", string? nivel = "subscription", string? rol = "Contributor",
        string? tipo = "User", string? nombre = "Ana Perez", string? login = "ana@x.com",
        string? cuentaActiva = "Sí", string? ultimoLogin = "2026-01-05 10:00",
        string? roleClass = null, bool isCustomRole = false) =>
        new(hash, sheetName, suscripcion, scope, nivel, rol, tipo, nombre, login, cuentaActiva, ultimoLogin,
            roleClass, isCustomRole);

    // ── Decisión 2: identidad (más débil que por la vía de la base, ver comentario de clase) ──

    [Fact]
    public void Con_login_la_identidad_es_el_login()
    {
        var fila = RbacFilaConverter.Convertir(Fila(login: "ana@x.com", nombre: "Ana Perez"));

        Assert.Equal("ana@x.com", fila.PrincipalObjectId);
    }

    [Fact]
    public void Sin_login_la_identidad_es_el_nombre()
    {
        var fila = RbacFilaConverter.Convertir(Fila(login: null, nombre: "Ana Perez"));

        Assert.Equal("Ana Perez", fila.PrincipalObjectId);
    }

    /// <summary>Sin nombre ni login, la identidad se deriva del hash de la fila (único por
    /// construcción: ver RbacParser) para no colapsar dos filas distintas en una sola.</summary>
    [Fact]
    public void Sin_nombre_ni_login_dos_filas_distintas_no_colapsan()
    {
        var f1 = RbacFilaConverter.Convertir(Fila(hash: "h1", login: null, nombre: null));
        var f2 = RbacFilaConverter.Convertir(Fila(hash: "h2", login: null, nombre: null));

        Assert.NotEqual(f1.PrincipalObjectId, f2.PrincipalObjectId);
    }

    [Fact]
    public void PrincipalType_Rol_Scope_y_ScopeLevel_nunca_son_null()
    {
        var fila = RbacFilaConverter.Convertir(Fila(tipo: null, rol: null, scope: null, nivel: null));

        Assert.Equal("", fila.PrincipalType);
        Assert.Equal("", fila.Rol);
        Assert.Equal("", fila.Scope);
        Assert.Equal("", fila.ScopeLevel);
    }

    [Fact]
    public void Nombre_login_tipo_rol_scope_y_nivel_pasan_igual()
    {
        var fila = RbacFilaConverter.Convertir(Fila(
            tipo: "Group", rol: "Reader", scope: "/subscriptions/s1/resourceGroups/rg", nivel: "resource_group",
            nombre: "Grupo X", login: "grupo@x.com"));

        Assert.Equal("Group", fila.PrincipalType);
        Assert.Equal("Reader", fila.Rol);
        Assert.Equal("/subscriptions/s1/resourceGroups/rg", fila.Scope);
        Assert.Equal("resource_group", fila.ScopeLevel);
        Assert.Equal("Grupo X", fila.Nombre);
        Assert.Equal("grupo@x.com", fila.Login);
    }

    // ── Decisión 5: RoleKey (derivación honesta, sin GUID) y ViaGrupoId (siempre null) ──

    [Fact]
    public void RoleKey_es_el_nombre_del_rol_porque_el_archivo_no_trae_el_guid()
    {
        var fila = RbacFilaConverter.Convertir(Fila(rol: "Contributor"));

        Assert.Equal("Contributor", fila.RoleKey);
    }

    [Fact]
    public void ViaGrupoId_siempre_es_null_por_la_via_del_archivo()
    {
        var fila = RbacFilaConverter.Convertir(Fila());

        Assert.Null(fila.ViaGrupoId);
    }

    // ── Decisión 6: RoleClass/IsCustomRole pasan igual que llegan en RbacRow (la pérdida ocurre
    // al leer desde la base, en SqlInformeValorStore.GetRbacAsync, no en esta conversión) ──

    [Fact]
    public void RoleClass_e_IsCustomRole_pasan_intactos_si_RbacRow_los_trae()
    {
        var fila = RbacFilaConverter.Convertir(Fila(roleClass: AccessReviewRoleClassifier.OtorgaAccesos, isCustomRole: true));

        Assert.Equal(AccessReviewRoleClassifier.OtorgaAccesos, fila.RoleClass);
        Assert.True(fila.IsCustomRole);
    }

    [Fact]
    public void RoleClass_es_null_si_RbacRow_no_lo_trae()
    {
        var fila = RbacFilaConverter.Convertir(Fila(roleClass: null, isCustomRole: false));

        Assert.Null(fila.RoleClass);
        Assert.False(fila.IsCustomRole);
    }

    // ── Decisión 7: "Sí"/"No"/vacío -> bool?, con vacío = null, nunca false ──

    [Theory]
    [InlineData("Sí", true)]
    [InlineData("Si", true)]
    [InlineData("SI", true)]
    [InlineData("No", false)]
    [InlineData("NO", false)]
    public void Cuenta_activa_se_convierte_a_booleano(string texto, bool esperado)
    {
        var fila = RbacFilaConverter.Convertir(Fila(cuentaActiva: texto));

        Assert.Equal(esperado, fila.CuentaHabilitada);
    }

    /// <summary>El caso de riesgo del brief: vacío es "no medido" (null), nunca "deshabilitada"
    /// (false). Confundir los dos fabrica un hallazgo de seguridad falso.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Cuenta_activa_vacia_es_null_no_false(string? texto)
    {
        var fila = RbacFilaConverter.Convertir(Fila(cuentaActiva: texto));

        Assert.Null(fila.CuentaHabilitada);
    }

    [Fact]
    public void Ultimo_login_pasa_como_texto_crudo()
    {
        var fila = RbacFilaConverter.Convertir(Fila(ultimoLogin: "2026-01-05 10:00"));

        Assert.Equal("2026-01-05 10:00", fila.UltimoLoginTexto);
    }

    [Fact]
    public void Ultimo_login_vacio_es_null()
    {
        var fila = RbacFilaConverter.Convertir(Fila(ultimoLogin: ""));

        Assert.Null(fila.UltimoLoginTexto);
    }

    // ── SubscriptionId/SuscripcionesAlcanzadas: el archivo solo trae el nombre, nunca el id ──

    [Fact]
    public void SubscriptionId_siempre_es_null_el_archivo_no_trae_el_id()
    {
        var fila = RbacFilaConverter.Convertir(Fila(suscripcion: "Sub Uno"));

        Assert.Null(fila.SubscriptionId);
        Assert.Equal("Sub Uno", fila.SubscriptionName);
    }

    [Fact]
    public void SuscripcionesAlcanzadas_es_un_solo_elemento_con_el_nombre()
    {
        var fila = RbacFilaConverter.Convertir(Fila(suscripcion: "Sub Uno"));

        Assert.Equal(["Sub Uno"], fila.SuscripcionesAlcanzadas);
        Assert.Equal(["Sub Uno"], fila.SuscripcionesAlcanzadasNombres);
    }

    [Fact]
    public void Sin_suscripcion_SuscripcionesAlcanzadas_queda_vacio()
    {
        var fila = RbacFilaConverter.Convertir(Fila(suscripcion: null));

        Assert.Empty(fila.SuscripcionesAlcanzadas);
        Assert.Empty(fila.SuscripcionesAlcanzadasNombres);
    }
}
