using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Parser del Excel de RBAC de respaldo (entrega 2 del informe de valor: el insumo condicional
/// que se sube cuando la credencial del cliente no puede leer los accesos). Mismo patrón que
/// BitcostParserTests/CasosParserTests: resultado con total, procesadas, descartadas y avisos.
/// </summary>
public sealed class RbacParserTests
{
    private static readonly string?[] CabeceraAsignaciones =
    [
        "Suscripción", "Scope", "Nivel", "Rol", "Clase de rol", "Rol personalizado", "Tipo",
        "Nombre", "Correo / Login", "Tipo usuario", "Vía grupo", "Cuenta activa", "Último login", "MFA",
    ];

    private static string?[] Fila(
        string suscripcion = "Sub Uno", string scope = "/subscriptions/s1", string nivel = "subscription",
        string rol = "Contributor", string clase = "", string personalizado = "", string tipo = "User",
        string nombre = "Ana Perez", string login = "ana@x.com", string tipoUsuario = "Member",
        string viaGrupo = "", string cuentaActiva = "Sí", string ultimoLogin = "2026-01-05 10:00", string mfa = "") =>
        [suscripcion, scope, nivel, rol, clase, personalizado, tipo, nombre, login, tipoUsuario, viaGrupo,
         cuentaActiva, ultimoLogin, mfa];

    // ── Decisión 1: qué hoja se lee ──

    [Fact]
    public void Lee_la_hoja_Asignaciones_RBAC_por_nombre_aunque_no_sea_la_primera()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsxMultiHoja(new Dictionary<string, IEnumerable<string?[]>>
        {
            ["Resumen"] = [["x"]],
            ["Asignaciones RBAC"] = [CabeceraAsignaciones, Fila(nombre: "Ana Perez")],
            ["Cambios"] = [["y"]],
        });

        var r = RbacParser.Parse(xlsx);

        Assert.Equal("Asignaciones RBAC", r.HojaLeida);
        Assert.Single(r.Rows);
    }

    /// <summary>
    /// "Cambios" trae Cuenta/Tipo/Rol/Nivel de scope/Suscripción: no son asignaciones vigentes,
    /// son altas y bajas contra la corrida anterior. Con la hoja correcta presente bajo su
    /// nombre, ni "Cambios" ni "Service Principals" se llegan a mirar.
    /// </summary>
    [Fact]
    public void No_confunde_la_hoja_Cambios_con_Asignaciones_RBAC_cuando_ambas_existen()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsxMultiHoja(new Dictionary<string, IEnumerable<string?[]>>
        {
            ["Cambios"] = [["Cambio", "Cuenta", "Tipo", "Rol", "Clase de rol", "Nivel de scope", "Suscripcion", "Ambiente"],
                           ["Nuevo", "alguien", "User", "Reader", "Lectura", "subscription", "Sub Uno", "Producción"]],
            ["Asignaciones RBAC"] = [CabeceraAsignaciones, Fila()],
        });

        var r = RbacParser.Parse(xlsx);

        Assert.Equal("Asignaciones RBAC", r.HojaLeida);
        Assert.Single(r.Rows);
    }

    /// <summary>
    /// "Service Principals" trae cinco de las nueve columnas, y esos principals ya están dentro
    /// de "Asignaciones RBAC" con tipo ServicePrincipal: parsear las dos duplica. Con la hoja
    /// correcta presente, "Service Principals" se ignora entera.
    /// </summary>
    [Fact]
    public void No_duplica_leyendo_tambien_Service_Principals()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsxMultiHoja(new Dictionary<string, IEnumerable<string?[]>>
        {
            ["Asignaciones RBAC"] = [CabeceraAsignaciones, Fila(tipo: "ServicePrincipal", nombre: "sp-1", login: "")],
            ["Service Principals"] = [["Suscripción", "Scope", "Nivel", "Rol", "Nombre", "AppId"],
                                       ["Sub Uno", "/subscriptions/s1", "subscription", "Contributor", "sp-1", "app-1"]],
        });

        var r = RbacParser.Parse(xlsx);

        Assert.Single(r.Rows);
    }

    /// <summary>Sin una hoja llamada "Asignaciones RBAC", cae a detección por cabecera EXCLUYENDO
    /// por nombre las otras ocho del export -- así no matchea "Service Principals" ni "Cambios"
    /// aunque comparta columnas.</summary>
    [Fact]
    public void Sin_hoja_con_ese_nombre_cae_a_deteccion_por_cabecera()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsxMultiHoja(new Dictionary<string, IEnumerable<string?[]>>
        {
            ["Resumen"] = [["x"]],
            ["Service Principals"] = [["Suscripción", "Scope", "Nivel", "Rol", "Nombre", "AppId"],
                                       ["Sub Uno", "/subscriptions/s1", "subscription", "Contributor", "sp-1", "app-1"]],
            ["Hoja3"] = [CabeceraAsignaciones, Fila()],
        });

        var r = RbacParser.Parse(xlsx);

        Assert.Equal("Hoja3", r.HojaLeida);
        Assert.Single(r.Rows);
    }

    [Fact]
    public void Registra_de_que_hoja_salio_cada_fila()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([CabeceraAsignaciones, Fila()], sheetName: "Asignaciones RBAC");

        var fila = Assert.Single(RbacParser.Parse(xlsx).Rows);

        Assert.Equal("Asignaciones RBAC", fila.SheetName);
    }

    [Fact]
    public void Avisa_que_hoja_se_leyo_y_cuales_se_ignoraron()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsxMultiHoja(new Dictionary<string, IEnumerable<string?[]>>
        {
            ["Resumen"] = [["x"]],
            ["Asignaciones RBAC"] = [CabeceraAsignaciones, Fila()],
            ["Cambios"] = [["y"]],
        });

        var r = RbacParser.Parse(xlsx);

        Assert.Contains("Resumen", r.HojasIgnoradas);
        Assert.Contains("Cambios", r.HojasIgnoradas);
        Assert.DoesNotContain("Asignaciones RBAC", r.HojasIgnoradas);
        Assert.Contains(r.Warnings, w => w.Contains("Asignaciones RBAC", StringComparison.Ordinal));
    }

    [Fact]
    public void Sin_ninguna_hoja_reconocible_lanza_con_mensaje_para_el_usuario()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsxMultiHoja(new Dictionary<string, IEnumerable<string?[]>>
        {
            ["Resumen"] = [["Uno", "Dos"], ["a", "b"]],
        });

        var ex = Assert.Throws<InvalidOperationException>(() => RbacParser.Parse(xlsx));
        Assert.Contains("Revisión de accesos", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Decisión 2: identidad, sin colapsar las que no tienen ni nombre ni login ──

    [Fact]
    public void Dos_filas_sin_nombre_ni_login_no_colapsan_en_una_sola()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
        [
            CabeceraAsignaciones,
            Fila(nombre: "", login: "", rol: "Reader", scope: "/subscriptions/s1/resourceGroups/rg1"),
            Fila(nombre: "", login: "", rol: "Reader", scope: "/subscriptions/s1/resourceGroups/rg1"),
        ], sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);

        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(2, r.Rows.Select(x => x.Hash).Distinct().Count());
        Assert.Equal(0, r.RowsSkipped);
    }

    [Fact]
    public void Cuenta_aparte_cuantas_filas_quedan_sin_identificar()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
        [
            CabeceraAsignaciones,
            Fila(nombre: "Ana Perez"),
            Fila(nombre: "", login: ""),
            Fila(nombre: "", login: ""),
        ], sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);

        Assert.Equal(2, r.SinIdentificar);
        Assert.Contains(r.Warnings, w => w.Contains("sin identificar", StringComparison.OrdinalIgnoreCase)
            || w.Contains("nombre ni login", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Con nombre o login, dos filas realmente idénticas SÍ son un duplicado real (el
    /// export ya viene deduplicado: esto es la red de seguridad, no la deduplicación de ARM).</summary>
    [Fact]
    public void Dos_filas_identificadas_e_identicas_se_deduplican_con_aviso()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
        [
            CabeceraAsignaciones, Fila(nombre: "Ana Perez"), Fila(nombre: "Ana Perez"),
        ], sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);

        Assert.Single(r.Rows);
        Assert.Equal(1, r.RowsSkipped);
        Assert.Contains(r.Warnings, w => w.Contains("idéntic", StringComparison.OrdinalIgnoreCase));
    }

    // ── Decisión 3: los dos ejes de medición, cada uno por su propia columna ──

    [Fact]
    public void Con_las_dos_columnas_con_datos_los_dos_ejes_quedan_medidos()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraAsignaciones, Fila(cuentaActiva: "Sí", ultimoLogin: "2026-01-05 10:00")],
            sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);

        Assert.True(r.Ejes.EstadoCuentaMedido);
        Assert.True(r.Ejes.UltimoLoginMedido);
    }

    /// <summary>El caso que fabrica el hallazgo falso: la columna EXISTE pero todas las celdas
    /// están vacías. Tiene que verse igual que "columna ausente", nunca como "0% con actividad".</summary>
    [Fact]
    public void Con_la_columna_presente_pero_todas_las_celdas_vacias_el_eje_no_esta_medido()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
        [
            CabeceraAsignaciones,
            Fila(cuentaActiva: "", ultimoLogin: ""),
            Fila(cuentaActiva: "", ultimoLogin: "", nombre: "Otro", login: "otro@x.com"),
        ], sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);

        Assert.False(r.Ejes.EstadoCuentaMedido);
        Assert.False(r.Ejes.UltimoLoginMedido);
    }

    [Fact]
    public void Sin_la_columna_cuenta_activa_ese_eje_no_esta_medido_aunque_haya_login()
    {
        var cabecera = CabeceraAsignaciones.Where(h => h != "Cuenta activa").ToArray();
        var fila = Fila().Where((_, i) => CabeceraAsignaciones[i] != "Cuenta activa").ToArray();
        using var xlsx = XlsxRowReaderTests.BuildXlsx([cabecera, fila], sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);

        Assert.False(r.Ejes.EstadoCuentaMedido);
        Assert.True(r.Ejes.UltimoLoginMedido);
    }

    /// <summary>Los dos ejes son independientes: un archivo puede tener uno medido y no el otro
    /// (mismo caso que un tenant sin licencia P1 por la vía de la base).</summary>
    [Fact]
    public void Un_eje_medido_y_el_otro_no_son_independientes()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraAsignaciones, Fila(cuentaActiva: "Sí", ultimoLogin: "")],
            sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);

        Assert.True(r.Ejes.EstadoCuentaMedido);
        Assert.False(r.Ejes.UltimoLoginMedido);
    }

    // ── Decisión 6: RoleClass viene traducido; IsCustomRole de "Rol personalizado" ──

    [Theory]
    [InlineData("Owner (otorga accesos)", AccessReviewRoleClassifier.Owner)]
    [InlineData("Otorga accesos", AccessReviewRoleClassifier.OtorgaAccesos)]
    [InlineData("Escritura total", AccessReviewRoleClassifier.EscrituraTotal)]
    [InlineData("Escritura de servicio", AccessReviewRoleClassifier.EscrituraServicio)]
    [InlineData("Lectura", AccessReviewRoleClassifier.Lectura)]
    public void Invierte_la_etiqueta_de_clase_de_rol_al_codigo_interno(string etiqueta, string codigoEsperado)
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraAsignaciones, Fila(clase: etiqueta)], sheetName: "Asignaciones RBAC");

        var fila = Assert.Single(RbacParser.Parse(xlsx).Rows);

        Assert.Equal(codigoEsperado, fila.RoleClass);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sin clasificar")]
    [InlineData("Algo que no está en el mapa")]
    public void Una_etiqueta_no_reconocida_es_null_nunca_un_valor_adivinado(string etiqueta)
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraAsignaciones, Fila(clase: etiqueta)], sheetName: "Asignaciones RBAC");

        var fila = Assert.Single(RbacParser.Parse(xlsx).Rows);

        Assert.Null(fila.RoleClass);
    }

    [Fact]
    public void Rol_personalizado_Si_marca_IsCustomRole()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraAsignaciones, Fila(personalizado: "Sí")], sheetName: "Asignaciones RBAC");

        Assert.True(Assert.Single(RbacParser.Parse(xlsx).Rows).IsCustomRole);
    }

    [Fact]
    public void Rol_personalizado_vacio_no_marca_IsCustomRole()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraAsignaciones, Fila(personalizado: "")], sheetName: "Asignaciones RBAC");

        Assert.False(Assert.Single(RbacParser.Parse(xlsx).Rows).IsCustomRole);
    }

    // ── Decisión 7: cuenta_activa/ultimo_login se guardan como texto, sin convertir ──

    [Fact]
    public void Guarda_cuenta_activa_y_ultimo_login_como_texto_sin_convertir()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraAsignaciones, Fila(cuentaActiva: "Sí", ultimoLogin: "2026-01-05 10:00")],
            sheetName: "Asignaciones RBAC");

        var fila = Assert.Single(RbacParser.Parse(xlsx).Rows);

        Assert.Equal("Sí", fila.CuentaActiva);
        Assert.Equal("2026-01-05 10:00", fila.UltimoLogin);
    }

    // ── Invariante total = procesadas + descartadas, truncamiento, formato inesperado ──

    [Fact]
    public void Descarta_filas_sin_rol_ni_scope()
    {
        string?[] vacia = ["", "", "", "", "", "", "", "", "", "", "", "", "", ""];
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraAsignaciones, Fila(), vacia], sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);

        Assert.Single(r.Rows);
        Assert.Equal(1, r.RowsSkipped);
        Assert.Equal(2, r.RowsTotal);
    }

    [Fact]
    public void El_total_siempre_cierra_como_procesadas_mas_descartadas()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
        [
            CabeceraAsignaciones,
            Fila(nombre: "Ana Perez"),
            Fila(nombre: "Ana Perez"), // duplicada de la anterior
            Fila(nombre: "", login: ""), // sin identificar, no se descarta
            ["", "", "", "", "", "", "", "", "", "", "", "", "", ""], // vacía, se descarta
        ], sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);

        Assert.Equal(r.RowsTotal, r.Rows.Count + r.RowsSkipped);
        Assert.Equal(4, r.RowsTotal);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(2, r.RowsSkipped);
    }

    [Fact]
    public void Trunca_el_scope_al_ancho_de_su_columna_y_avisa()
    {
        var scopeLargo = "/subscriptions/" + new string('a', 900);
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraAsignaciones, Fila(scope: scopeLargo)], sheetName: "Asignaciones RBAC");

        var r = RbacParser.Parse(xlsx);
        var fila = Assert.Single(r.Rows);

        Assert.Equal(900, fila.Scope!.Length);
        Assert.Contains(r.Warnings, w => w.Contains("recortaron", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sin_las_columnas_esperadas_lanza_con_mensaje_para_el_usuario()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([["Uno", "Dos"], ["a", "b"]], sheetName: "Asignaciones RBAC");

        var ex = Assert.Throws<InvalidOperationException>(() => RbacParser.Parse(xlsx));
        Assert.Contains("Revisión de accesos", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
