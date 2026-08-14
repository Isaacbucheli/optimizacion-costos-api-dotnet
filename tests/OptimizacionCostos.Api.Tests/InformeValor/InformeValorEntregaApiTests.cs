using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Cdc;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.Optimization;
using OptimizacionCostos.Api.Features.Storage;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Los tres endpoints de entrega (Tarea 4 de la entrega 3): POST .../generar,
/// GET .../entregas y GET .../entregas/{id}/descargar. Mismo patrón que
/// InformeValorPreviewApiTests: pipeline MVC real (auth, roles, RequireModule, IAnalysisAccess),
/// falsos solo para la base, Blob Storage y la lectura de reservas contra Azure.
///
/// <para>Lo que estos tests cuidan, más allá del código de estado:</para>
/// <list type="bullet">
/// <item><b>El cable</b>: generar tiene que CAPTURAR la foto de reservas (E7) y ARCHIVARLA, subir el
/// artefacto y registrar la fila. Cada una de esas cuatro cosas se afirma por separado, porque el
/// defecto que este módulo repite es una pieza correcta que nadie invoca.</item>
/// <item><b>F1 de punta a punta</b>: con la variante del cliente y ningún bloque aprobado, el
/// artefacto que se sube no puede contener ningún monto.</item>
/// <item><b>El contenedor sale de la fila</b>, no de la configuración: si no, cambiar la variable de
/// entorno deja sin descarga a todo lo archivado.</item>
/// </list>
/// </summary>
public sealed class InformeValorEntregaApiTests : IClassFixture<InformeValorEntregaApiTests.Factory>
{
    private readonly Factory _factory;
    public InformeValorEntregaApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role, bool canEdit)
    {
        _factory.Perms.Set(role, Modules.InformeValor, canView: true, canEdit: canEdit);
        _factory.Service.Invalidate();
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object Cuerpo(string variante = "interna", string[]? bloques = null) => new
    {
        period_start = "2026-01-01",
        period_end = "2026-02-28",
        corte = "2026-03-01T00:00:00Z",
        meses_parciales_forzados = Array.Empty<string>(),
        variante,
        bloques = bloques ?? [],
    };

    // ================================================================================
    // Acceso y permisos
    // ================================================================================

    [Fact]
    public async Task Generar_sin_acceso_al_cliente_devuelve_403_y_no_toca_azure_ni_storage()
    {
        _factory.Access.Deny(clientId: 900);
        var client = ClientFor("g1@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/900/generar", Cuerpo());

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        // El guard va antes de todo: un cliente ajeno no dispara la lectura de reservas ni deja un
        // artefacto suelto en el contenedor.
        Assert.Equal(0, _factory.Reservations.LlamadasACredenciales(900));
        Assert.DoesNotContain(_factory.Blobs.Uploads, u => u.BlobName.Contains("client-900", StringComparison.Ordinal));
    }

    /// <summary>Generar es la única escritura de este flujo: con permiso de solo lectura sobre el
    /// módulo tiene que dar 403 aunque el usuario tenga acceso al cliente.</summary>
    [Fact]
    public async Task Generar_sin_permiso_de_edicion_devuelve_403()
    {
        _factory.Access.Allow(clientId: 901);
        var client = ClientFor("g2@bit.ec", Roles.Lector, canEdit: false);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/901/generar", Cuerpo());

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(0, _factory.Reservations.LlamadasACredenciales(901));
    }

    /// <summary>Listar y descargar son View: alcanza con permiso de lectura.</summary>
    [Fact]
    public async Task Listar_entregas_sin_permiso_de_edicion_funciona()
    {
        _factory.Access.Allow(clientId: 902);
        var client = ClientFor("g3@bit.ec", Roles.Lector, canEdit: false);

        var res = await client.GetAsync("/informe-valor/clients/902/entregas");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Listar_entregas_sin_acceso_al_cliente_devuelve_403()
    {
        _factory.Access.Deny(clientId: 903);
        var client = ClientFor("g4@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.GetAsync("/informe-valor/clients/903/entregas");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Descargar_sin_acceso_al_cliente_devuelve_403_y_no_lee_el_blob()
    {
        _factory.Access.Deny(clientId: 904);
        var client = ClientFor("g5@bit.ec", Roles.Consultor, canEdit: true);
        var antes = _factory.Blobs.Descargas.Count;

        var res = await client.GetAsync("/informe-valor/clients/904/entregas/1/descargar");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(antes, _factory.Blobs.Descargas.Count);
    }

    // ================================================================================
    // Validación del cuerpo
    // ================================================================================

    [Fact]
    public async Task Generar_sin_variante_devuelve_400()
    {
        _factory.Access.Allow(clientId: 910);
        var client = ClientFor("g6@bit.ec", Roles.Consultor, canEdit: true);

        var cuerpo = new
        {
            period_start = "2026-01-01", period_end = "2026-02-28",
            corte = "2026-03-01T00:00:00Z", bloques = Array.Empty<string>(),
        };
        var res = await client.PostAsJsonAsync("/informe-valor/clients/910/generar", cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        // No hay default seguro: ni "interna" (publicaría el informe completo a un cliente) ni
        // "cliente" (entregaría un informe sin cifras al consultor que las esperaba).
        Assert.Equal(0, _factory.Reservations.LlamadasACredenciales(910));
    }

    [Fact]
    public async Task Generar_con_variante_desconocida_devuelve_400()
    {
        _factory.Access.Allow(clientId: 911);
        var client = ClientFor("g7@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync(
            "/informe-valor/clients/911/generar", Cuerpo(variante: "completa"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>
    /// Un bloque que la API no reconoce es un 400 que lo nombra, nunca un bloque que se ignora: el
    /// consultor creyó aprobarlo y el informe saldría sin esa cifra, con la sección diciendo "No
    /// publicado", sin que nadie se lo diga. Es el caso que <c>BloqueEconomicoExtensions.Parsear</c>
    /// deja explícitamente en manos de quien llama.
    /// </summary>
    [Fact]
    public async Task Generar_con_un_bloque_desconocido_devuelve_400_y_lo_nombra()
    {
        _factory.Access.Allow(clientId: 912);
        var client = ClientFor("g8@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync(
            "/informe-valor/clients/912/generar",
            Cuerpo(variante: "cliente", bloques: ["gastoTotal", "gasto_total"]));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var detail = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.Contains("gasto_total", detail, StringComparison.Ordinal);
        Assert.Equal(0, _factory.Reservations.LlamadasACredenciales(912));
    }

    /// <summary>Mismo rechazo que las dos fases de la vista previa: si /generar aceptara un rango que
    /// /preview rechaza, el informe entregado mediría una ventana que la pantalla no reconoce.</summary>
    [Fact]
    public async Task Generar_con_periodo_invertido_devuelve_400()
    {
        _factory.Access.Allow(clientId: 913);
        var client = ClientFor("g9@bit.ec", Roles.Consultor, canEdit: true);

        var cuerpo = new
        {
            period_start = "2026-02-28", period_end = "2026-01-01",
            corte = "2026-03-01T00:00:00Z", variante = "interna", bloques = Array.Empty<string>(),
        };
        var res = await client.PostAsJsonAsync("/informe-valor/clients/913/generar", cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ================================================================================
    // El camino feliz, pieza por pieza
    // ================================================================================

    /// <summary>
    /// El cable completo de generar: sube el artefacto al contenedor configurado, archiva la fila con
    /// el mismo contenedor y nombre de blob que subió, y devuelve el enlace de descarga que apunta a
    /// la entrega recién creada. Un artefacto subido sin fila, o una fila con otro nombre de blob, es
    /// una descarga que falla más adelante sin explicación.
    /// </summary>
    [Fact]
    public async Task Generar_sube_el_artefacto_y_archiva_la_entrega_con_el_mismo_blob()
    {
        const int clientId = 920;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g10@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync($"/informe-valor/clients/{clientId}/generar", Cuerpo());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();

        var subida = Assert.Single(_factory.Blobs.Uploads.Where(u => u.BlobName.Contains($"client-{clientId}/", StringComparison.Ordinal)).ToList());
        var entrega = Assert.Single(_factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList());

        Assert.Equal(subida.Container, entrega.BlobContainer);
        Assert.Equal(subida.BlobName, entrega.BlobName);
        Assert.Equal(subida.Data.Length, entrega.BlobSizeBytes);
        Assert.Equal("text/html; charset=utf-8", subida.ContentType);
        Assert.EndsWith(".html", entrega.FileName, StringComparison.Ordinal);

        Assert.Equal(entrega.BlobName, json.GetProperty("blob_name").GetString());
        Assert.Equal(subida.Container, json.GetProperty("container").GetString());
        Assert.Equal(
            $"/informe-valor/clients/{clientId}/entregas/{json.GetProperty("entrega_id").GetInt32()}/descargar",
            json.GetProperty("download_url").GetString());
    }

    /// <summary>
    /// E7, y el motivo por el que este endpoint puede tardar: la foto de reservas se captura ACÁ (a
    /// diferencia de /preview, que no la toca) y se archiva con la entrega. Sin esto, reemitir un
    /// informe viejo lo recalcularía contra las reservas de hoy — justo lo que la decisión evita.
    /// </summary>
    [Fact]
    public async Task Generar_captura_la_foto_de_reservas_y_la_archiva()
    {
        const int clientId = 921;
        _factory.Access.Allow(clientId);
        _factory.SembrarUnaReservaConConsumidor(clientId, credentialId: 21, reservationId: "resv-921");
        var client = ClientFor("g11@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync($"/informe-valor/clients/{clientId}/generar", Cuerpo());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, _factory.Reservations.LlamadasACredenciales(clientId));

        var entrega = Assert.Single(_factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList());
        Assert.NotNull(entrega.FotoReservas);
        Assert.True(entrega.FotoReservas!.Medido);
        var reserva = Assert.Single(entrega.FotoReservas.Reservas);
        Assert.Equal("resv-921", reserva.ReservationId);

        // Y la respuesta declara el estado del eje: un balde en cero por falla de lectura y un cero
        // legítimo no pueden verse igual desde afuera.
        var reservas = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reservas");
        Assert.True(reservas.GetProperty("medido").GetBoolean());
        Assert.Equal(1, reservas.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// Una falla leyendo credenciales de Azure no puede tumbar la generación: el informe tiene cinco
    /// bloques y solo uno depende de reservas. El eje sale "no medido" con su motivo, la entrega se
    /// archiva igual y la respuesta lo dice — con la foto guardada como <c>Medido=false</c>, que es
    /// distinto de no haber capturado ninguna.
    /// </summary>
    [Fact]
    public async Task Si_la_lectura_de_reservas_falla_el_informe_se_genera_con_el_eje_no_medido()
    {
        const int clientId = 922;
        _factory.Access.Allow(clientId);
        _factory.Reservations.FallarLecturaDeCredenciales(clientId);
        var client = ClientFor("g12@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync($"/informe-valor/clients/{clientId}/generar", Cuerpo());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var reservas = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reservas");
        Assert.False(reservas.GetProperty("medido").GetBoolean());
        Assert.NotEmpty(reservas.GetProperty("motivo").GetString()!);

        var entrega = Assert.Single(_factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList());
        Assert.NotNull(entrega.FotoReservas);
        Assert.False(entrega.FotoReservas!.Medido);
    }

    /// <summary>
    /// La variante interna publica los ocho bloques aunque no se apruebe ninguno: pedir la interna es
    /// pedir el informe completo. Lo que se archiva es lo que el artefacto HACE, no lo que se pidió.
    /// </summary>
    [Fact]
    public async Task Generar_la_variante_interna_archiva_los_seis_bloques_aunque_no_se_apruebe_ninguno()
    {
        const int clientId = 923;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g13@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync(
            $"/informe-valor/clients/{clientId}/generar", Cuerpo(variante: "interna"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(8, json.GetProperty("bloques_publicados").GetArrayLength());
        Assert.Equal(8, json.GetProperty("bloques_totales").GetInt32());

        var entrega = Assert.Single(_factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList());
        Assert.Equal(8, entrega.BloquesPublicados.Count);
    }

    /// <summary>
    /// F1 de punta a punta, sobre el artefacto que de verdad se sube y no sobre el modelo: con la
    /// variante del cliente y ningún bloque aprobado, el total facturado del fixture (1500) no puede
    /// aparecer en el HTML. Recortar solo el dibujo no alcanzaría: quien abre el archivo puede leer
    /// la variable <c>EMBEDDED</c> desde el navegador.
    /// </summary>
    [Fact]
    public async Task Generar_para_el_cliente_sin_bloques_aprobados_no_sube_ningun_monto()
    {
        const int clientId = 924;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g14@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync(
            $"/informe-valor/clients/{clientId}/generar", Cuerpo(variante: "cliente"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("bloques_publicados").GetArrayLength());

        var subida = Assert.Single(_factory.Blobs.Uploads.Where(u => u.BlobName.Contains($"client-{clientId}/", StringComparison.Ordinal)).ToList());
        var datos = BloqueDeDatos(Encoding.UTF8.GetString(subida.Data));
        // 1000 + 500 de FakeInformeValorStoreConDatosParaEntrega: el total, el promedio y las dos
        // filas mensuales salen de ahí. Ninguno puede viajar.
        Assert.DoesNotContain("1500", datos, StringComparison.Ordinal);
        Assert.DoesNotContain("1000", datos, StringComparison.Ordinal);
        // Y la variante viaja declarada, para que la capa de dibujo sepa que tiene que escribir "No
        // publicado" en vez de un cero.
        Assert.Contains("\"variante\":\"cliente\"", datos, StringComparison.Ordinal);
    }

    /// <summary>
    /// Solo el bloque <c>&lt;script id="data"&gt;</c> del artefacto: el único lugar donde viven los
    /// datos del cliente. Buscar un monto sobre el HTML completo da falsos positivos —la plantilla
    /// trae imágenes en base64 y cualquier dígito aparece ahí por casualidad— y un falso positivo en
    /// este test es peor que no tenerlo: se "arregla" cambiando el número del fixture y el test deja
    /// de probar lo que decía probar.
    /// </summary>
    private static string BloqueDeDatos(string html)
    {
        const string abre = "<script id=\"data\">";
        var desde = html.IndexOf(abre, StringComparison.Ordinal);
        Assert.True(desde >= 0, "El artefacto no tiene el bloque de datos: la plantilla cambió.");
        desde += abre.Length;
        var hasta = html.IndexOf("</script>", desde, StringComparison.Ordinal);
        Assert.True(hasta > desde, "El bloque de datos del artefacto quedó sin cerrar.");
        return html[desde..hasta];
    }

    /// <summary>
    /// El bloque aprobado sí viaja. Es el contraste del test de arriba: sin esto, un recorte
    /// demasiado ancho —que borrara también lo aprobado— pasaría desapercibido.
    /// </summary>
    [Fact]
    public async Task Generar_para_el_cliente_con_el_gasto_total_aprobado_si_publica_ese_monto()
    {
        const int clientId = 925;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g15@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync(
            $"/informe-valor/clients/{clientId}/generar",
            Cuerpo(variante: "cliente", bloques: ["gastoTotal"]));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var subida = Assert.Single(_factory.Blobs.Uploads.Where(u => u.BlobName.Contains($"client-{clientId}/", StringComparison.Ordinal)).ToList());
        Assert.Contains("1500", BloqueDeDatos(Encoding.UTF8.GetString(subida.Data)), StringComparison.Ordinal);

        var entrega = Assert.Single(_factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList());
        Assert.Equal([BloqueEconomico.GastoTotal], entrega.BloquesPublicados);
    }

    /// <summary>
    /// La trampa que encontró la revisión de la Tarea 9: <c>Generar_para_el_cliente_sin_bloques_aprobados_no_sube_ningun_monto</c>
    /// no siembra ninguna credencial de reservas, así que <c>FotoReservas.Medido</c> queda en
    /// <c>false</c> y <see cref="InformeValorEnsamblador.Ensamblar"/> nunca entra a calcular
    /// <c>ejecutado</c> — la clave queda <c>null</c> y ese test pasa sin haber custodiado nada del
    /// subárbol nuevo (Tarea 9), que es el de más riesgo por ser el más reciente y el que más
    /// piezas cruza (barrido, matriz y reservas). Este test siembra una reserva medida (mismo
    /// helper que <see cref="Generar_captura_la_foto_de_reservas_y_la_archiva"/>) para que
    /// <c>ejecutado</c> viaje poblado de verdad, y solo entonces confirma que su propio recorte
    /// (<c>InformeValorHtmlExporter</c>, cuando <c>AhorroEjecutado</c> no está aprobado) hace su
    /// trabajo igual que el resto de los bloques.
    /// </summary>
    [Fact]
    public async Task Generar_para_el_cliente_sin_bloques_con_ejecutado_poblado_no_sube_sus_montos()
    {
        const int clientId = 941;
        _factory.Access.Allow(clientId);
        _factory.SembrarUnaReservaConConsumidor(clientId, credentialId: 41, reservationId: "resv-941");
        var client = ClientFor("g28@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync(
            $"/informe-valor/clients/{clientId}/generar", Cuerpo(variante: "cliente"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("bloques_publicados").GetArrayLength());

        var subida = Assert.Single(_factory.Blobs.Uploads.Where(u => u.BlobName.Contains($"client-{clientId}/", StringComparison.Ordinal)).ToList());
        var datos = BloqueDeDatos(Encoding.UTF8.GetString(subida.Data));

        // El guard positivo primero: si "ejecutado" volviera a quedar null (la trampa de arriba),
        // esto revienta ACÁ, antes de llegar a los DoesNotContain de abajo — que de otro modo
        // pasarían igual de vacíos por la razón equivocada.
        // El bloque de datos es JS, no JSON puro ("var EMBEDDED={...};var PUBLICACION={...};"): se
        // recorta el objeto de EMBEDDED antes de parsear.
        const string prefijoEmbedded = "var EMBEDDED=";
        const string separadorPublicacion = ";var PUBLICACION=";
        Assert.StartsWith(prefijoEmbedded, datos, StringComparison.Ordinal);
        var finEmbedded = datos.IndexOf(separadorPublicacion, StringComparison.Ordinal);
        Assert.True(finEmbedded > 0, "El bloque de datos no tiene el separador esperado entre EMBEDDED y PUBLICACION.");
        var embeddedJson = datos[prefijoEmbedded.Length..finEmbedded];

        var ejecutado = JsonDocument.Parse(embeddedJson).RootElement.GetProperty("ejecutado");
        Assert.True(ejecutado.GetProperty("medido").GetBoolean());
        Assert.True(ejecutado.TryGetProperty("pctGasto", out var pctGasto));
        Assert.NotEqual(JsonValueKind.Null, pctGasto.ValueKind);
        Assert.True(ejecutado.TryGetProperty("ejes", out var ejes));
        Assert.Equal(JsonValueKind.Object, ejes.ValueKind);

        // Y con "ejecutado" de verdad poblado, la variante del cliente sin bloques aprobados sigue
        // sin subir los montos de la facturación (1000 + 500 = 1500 del mismo fixture que la reserva
        // ahora comparte recurso): el recorte de "ejecutado" tiene que nulear sus propios campos de
        // monto igual que lo hacen los demás bloques, no solo el bloque de facturación de siempre.
        Assert.DoesNotContain("1500", datos, StringComparison.Ordinal);
        Assert.DoesNotContain("1000", datos, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reemitir el mismo período es legítimo (F4) y las dos emisiones tienen que quedar en dos filas
    /// con DOS artefactos distintos. Con un nombre de blob derivado solo del período, la segunda
    /// emisión sobrescribiría la primera y la fila vieja apuntaría a un archivo con contenido nuevo.
    /// </summary>
    [Fact]
    public async Task Reemitir_el_mismo_periodo_deja_dos_entregas_con_dos_artefactos_distintos()
    {
        const int clientId = 926;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g16@bit.ec", Roles.Consultor, canEdit: true);

        var uno = await client.PostAsJsonAsync($"/informe-valor/clients/{clientId}/generar", Cuerpo());
        var dos = await client.PostAsJsonAsync($"/informe-valor/clients/{clientId}/generar", Cuerpo());

        Assert.Equal(HttpStatusCode.OK, uno.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dos.StatusCode);

        var entregas = _factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList();
        Assert.Equal(2, entregas.Count);
        Assert.NotEqual(entregas[0].BlobName, entregas[1].BlobName);
    }

    /// <summary>
    /// Si Storage falla, no se archiva NADA: una fila que apunta a un artefacto que no existe da una
    /// descarga que falla mucho después, sin pista de por qué. Al revés (blob primero, fila después)
    /// lo peor que queda es un archivo que nadie referencia, y eso va al log.
    /// </summary>
    [Fact]
    public async Task Si_la_subida_al_blob_falla_no_se_archiva_ninguna_entrega()
    {
        const int clientId = 927;
        _factory.Access.Allow(clientId);
        _factory.Blobs.FallarSubidas = true;
        var client = ClientFor("g17@bit.ec", Roles.Consultor, canEdit: true);
        try
        {
            var res = await client.PostAsJsonAsync($"/informe-valor/clients/{clientId}/generar", Cuerpo());

            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
            Assert.Empty(_factory.Store.Entregas.Where(e => e.ClientId == clientId));
        }
        finally
        {
            _factory.Blobs.FallarSubidas = false;
        }
    }

    /// <summary>Los ids de las tres corridas de ingesta se archivan: los insumos son vivos y cada
    /// carga borra la anterior, así que es lo único que después permite detectar que la entrega ya no
    /// se puede reemitir contra los mismos datos.</summary>
    [Fact]
    public async Task Generar_archiva_las_corridas_de_ingesta_que_alimentaron_el_informe()
    {
        const int clientId = 928;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g18@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync($"/informe-valor/clients/{clientId}/generar", Cuerpo());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entrega = Assert.Single(_factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList());
        Assert.Equal(FakeInformeValorStoreConDatosParaEntrega.IngestaFacturacion, entrega.FacturacionIngestaId);
        Assert.Equal(FakeInformeValorStoreConDatosParaEntrega.IngestaCasos, entrega.CasosIngestaId);
        // El insumo de RBAC no está cargado en el fixture: null significa "no estaba", no cero.
        Assert.Null(entrega.RbacIngestaId);
    }

    /// <summary>
    /// El nombre del blob lleva el período y la variante para poder navegar el contenedor, más un
    /// sufijo único por emisión. No lleva el nombre de descarga: ése depende del nombre del cliente,
    /// puede ser largo, y <c>blob_name</c> se guarda en una columna que el store trunca — un nombre
    /// truncado al archivarlo dejaría de coincidir con el que se subió.
    /// </summary>
    [Fact]
    public async Task El_nombre_del_blob_lleva_periodo_y_variante_y_no_el_nombre_de_descarga()
    {
        const int clientId = 929;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g26@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync(
            $"/informe-valor/clients/{clientId}/generar", Cuerpo(variante: "cliente"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entrega = Assert.Single(_factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList());

        Assert.StartsWith($"informe-valor/client-{clientId}/202601-202602-cliente-", entrega.BlobName, StringComparison.Ordinal);
        Assert.EndsWith(".html", entrega.BlobName, StringComparison.Ordinal);
        Assert.DoesNotContain(entrega.FileName, entrega.BlobName, StringComparison.Ordinal);
        Assert.True(entrega.BlobName.Length <= 400, "El nombre del blob no puede pasar del ancho de la columna: el store lo truncaría y la descarga buscaría otro blob.");
    }

    /// <summary>
    /// <c>summary_json</c> tiene que explicar la fila sin bajar el artefacto: qué bloques del modelo
    /// se pudieron armar y en qué estado quedó el eje de reservas. Y sin montos, a propósito: la
    /// bitácora guarda las dos variantes en la misma tabla, así que un resumen con cifras sería un
    /// camino por el que un monto suprimido para el cliente podría reaparecer.
    /// </summary>
    [Fact]
    public async Task El_resumen_archivado_explica_la_fila_y_no_lleva_montos()
    {
        const int clientId = 940;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g27@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync(
            $"/informe-valor/clients/{clientId}/generar", Cuerpo(variante: "cliente"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entrega = Assert.Single(_factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList());
        Assert.NotNull(entrega.SummaryJson);

        // Lo que se archiva es el corte YA resuelto a fecha de Guayaquil, no el instante crudo: es la
        // fecha contra la que se clasificaron los retiros, y reemitir con el instante original tiene
        // que dar la misma fecha.
        Assert.Equal(new DateOnly(2026, 2, 28), entrega.Corte);

        var resumen = JsonDocument.Parse(entrega.SummaryJson!).RootElement;
        Assert.Equal("2026-01 a 2026-02", resumen.GetProperty("periodo").GetString());
        // El corte llega como instante (2026-03-01T00:00:00Z) y se resuelve a fecha de Guayaquil
        // (UTC-5) una sola vez, en el controller: la medianoche UTC del 1 de marzo es el 28 de
        // febrero en Quito. Es el MISMO valor que resuelve /preview, y por eso la vista previa y el
        // informe entregado miden la misma ventana.
        Assert.Equal("2026-02-28", resumen.GetProperty("corte").GetString());
        Assert.Equal(1, resumen.GetProperty("suscripciones").GetInt32());
        var bloques = resumen.GetProperty("bloques_del_modelo");
        Assert.True(bloques.GetProperty("consumo").GetBoolean());
        // Sin casos cargados el bloque de operación no existe: false, no un objeto vacío que simule
        // que se midió y salió en cero.
        Assert.False(bloques.GetProperty("operacion").GetBoolean());
        // Hay bloque de consumo, así que la lista existe (vacía porque ningún mes resultó parcial).
        // Con el bloque ausente sería null, que es otra cosa: "no se pudo determinar".
        Assert.Equal(JsonValueKind.Array, resumen.GetProperty("meses_parciales").ValueKind);
        Assert.False(resumen.GetProperty("reservas").GetProperty("medido").GetBoolean());
        Assert.NotEmpty(resumen.GetProperty("reservas").GetProperty("motivo").GetString()!);

        Assert.DoesNotContain("1500", entrega.SummaryJson!, StringComparison.Ordinal);
    }

    // ================================================================================
    // Listar y descargar
    // ================================================================================

    [Fact]
    public async Task Las_entregas_se_listan_con_su_variante_bloques_y_enlace_de_descarga()
    {
        const int clientId = 930;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g19@bit.ec", Roles.Consultor, canEdit: true);

        await client.PostAsJsonAsync(
            $"/informe-valor/clients/{clientId}/generar", Cuerpo(variante: "cliente", bloques: ["centroCosto"]));

        var res = await client.GetAsync($"/informe-valor/clients/{clientId}/entregas");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var fila = Assert.Single(
            (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entregas").EnumerateArray().ToList());
        Assert.Equal("cliente", fila.GetProperty("variante").GetString());
        // La MISMA grafía que aceptó el POST y que lee la capa de dibujo: una sola por concepto.
        Assert.Equal("centroCosto", fila.GetProperty("bloques_publicados")[0].GetString());
        Assert.Equal(8, fila.GetProperty("bloques_totales").GetInt32());
        Assert.Equal("2026-01-01", fila.GetProperty("period_start").GetString());
        Assert.Contains("/descargar", fila.GetProperty("download_url").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// El contenedor con el que se descarga sale de la FILA, no de la configuración de hoy: es lo que
    /// ya hacen las dos descargas del módulo de informes. Si se dedujera del entorno, cambiar
    /// STORAGE_CONTAINER_OUTPUTS dejaría sin artefacto a todo lo archivado.
    /// </summary>
    [Fact]
    public async Task Descargar_usa_el_contenedor_archivado_en_la_fila()
    {
        const int clientId = 931;
        _factory.Access.Allow(clientId);
        _factory.Store.SembrarEntrega(clientId, entregaId: 3101,
            container: "contenedor-de-otra-epoca", blobName: "informe-valor/viejo.html",
            fileName: "Informe-Viejo.html");
        var client = ClientFor("g20@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.GetAsync($"/informe-valor/clients/{clientId}/entregas/3101/descargar");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pedido = Assert.Single(_factory.Blobs.Descargas.Where(d => d.BlobName == "informe-valor/viejo.html").ToList());
        Assert.Equal("contenedor-de-otra-epoca", pedido.Container);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        // Adjunto, nunca en línea: es HTML con datos del cliente y no tiene por qué ejecutarse en el
        // origen de la API.
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("Informe-Viejo.html", res.Content.Headers.ContentDisposition?.ToString() ?? "", StringComparison.Ordinal);
    }

    /// <summary>Sin contenedor archivado (fila de una versión anterior del módulo) se cae al
    /// configurado, que es la única suposición razonable.</summary>
    [Fact]
    public async Task Descargar_una_entrega_sin_contenedor_archivado_usa_el_configurado()
    {
        const int clientId = 932;
        _factory.Access.Allow(clientId);
        _factory.Store.SembrarEntrega(clientId, entregaId: 3201,
            container: null, blobName: "informe-valor/sin-contenedor.html", fileName: "X.html");
        var client = ClientFor("g21@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.GetAsync($"/informe-valor/clients/{clientId}/entregas/3201/descargar");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pedido = Assert.Single(_factory.Blobs.Descargas.Where(d => d.BlobName == "informe-valor/sin-contenedor.html").ToList());
        Assert.Equal("outputs", pedido.Container);
    }

    [Fact]
    public async Task Descargar_una_entrega_que_no_existe_devuelve_404()
    {
        _factory.Access.Allow(clientId: 933);
        var client = ClientFor("g22@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.GetAsync("/informe-valor/clients/933/entregas/999999/descargar");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var detail = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.Contains("no existe", detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// T10: la corrida de evolución que alimentó el informe se archiva igual que sus tres hermanas
    /// (facturación, casos, RBAC), y el fake store la devuelve intacta al releerla por
    /// <c>GetEntregaAsync</c> -- no solo desde la lista de escritura (<c>_factory.Store.Entregas</c>,
    /// que ya prueba el test hermano de arriba para las otras tres corridas). Si el índice posicional
    /// de <c>EvolucionIngestaId</c> quedara mal contado en el fake (o en el store real, que usa el
    /// mismo criterio posicional sobre <c>ColumnasEntregaCompleta</c>), esta vuelta lo detecta.
    /// </summary>
    [Fact]
    public async Task Generar_archiva_la_corrida_de_evolucion_y_la_entrega_archivada_la_devuelve()
    {
        const int clientId = 942;
        _factory.Access.Allow(clientId);
        var client = ClientFor("g29@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync($"/informe-valor/clients/{clientId}/generar", Cuerpo());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entregaId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entrega_id").GetInt32();

        var entrega = Assert.Single(_factory.Store.Entregas.Where(e => e.ClientId == clientId).ToList());
        Assert.Equal(FakeInformeValorStoreConDatosParaEntrega.IngestaEvolucion, entrega.EvolucionIngestaId);

        var archivada = await _factory.Store.GetEntregaAsync(clientId, entregaId, CancellationToken.None);
        Assert.NotNull(archivada);
        Assert.Equal(FakeInformeValorStoreConDatosParaEntrega.IngestaEvolucion, archivada!.EvolucionIngestaId);
    }

    /// <summary>
    /// "La entrega no existe" y "la entrega existe pero su artefacto ya no está" son dos hechos
    /// distintos y llevan a acciones distintas (revisar el id contra volver a generar). Los dos son
    /// 404, pero el texto tiene que separarlos.
    /// </summary>
    [Fact]
    public async Task Si_el_artefacto_ya_no_esta_en_storage_el_404_lo_dice_distinto()
    {
        const int clientId = 934;
        _factory.Access.Allow(clientId);
        _factory.Store.SembrarEntrega(clientId, entregaId: 3401,
            container: "outputs", blobName: "informe-valor/borrado.html", fileName: "Y.html");
        _factory.Blobs.BlobsFaltantes.Add("informe-valor/borrado.html");
        var client = ClientFor("g23@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.GetAsync($"/informe-valor/clients/{clientId}/entregas/3401/descargar");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var detail = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.Contains("almacenamiento", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("volver a generar", detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Un fallo de almacenamiento que NO es "no existe" (credencial vencida, throttling) no puede
    /// contarse como archivo borrado: es un 500. Colapsarlos convertiría un problema de permisos en
    /// "el informe se perdió" y mandaría a regenerar sin motivo.
    /// </summary>
    [Fact]
    public async Task Si_el_almacenamiento_no_responde_la_descarga_devuelve_500_y_no_404()
    {
        const int clientId = 935;
        _factory.Access.Allow(clientId);
        _factory.Store.SembrarEntrega(clientId, entregaId: 3501,
            container: "outputs", blobName: "informe-valor/storage-caido.html", fileName: "Z.html");
        _factory.Blobs.BlobsQueRevientan.Add("informe-valor/storage-caido.html");
        var client = ClientFor("g24@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.GetAsync($"/informe-valor/clients/{clientId}/entregas/3501/descargar");

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
    }

    /// <summary>El store filtra por client_id dentro del WHERE, así que una entrega de otro cliente
    /// es un 404 y nunca el artefacto ajeno.</summary>
    [Fact]
    public async Task Descargar_la_entrega_de_otro_cliente_devuelve_404()
    {
        _factory.Access.Allow(clientId: 936);
        _factory.Access.Allow(clientId: 937);
        _factory.Store.SembrarEntrega(clientId: 937, entregaId: 3601,
            container: "outputs", blobName: "informe-valor/de-937.html", fileName: "W.html");
        var client = ClientFor("g25@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.GetAsync("/informe-valor/clients/936/entregas/3601/descargar");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain(_factory.Blobs.Descargas, d => d.BlobName == "informe-valor/de-937.html");
    }

    // ---- Fixture: API real en memoria, falsos para base, Storage, acceso, permisos y reservas ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAccesoPorCliente Access { get; } = new();
        public FakeInformeValorStoreConDatosParaEntrega Store { get; } = new();
        public FakeInsumosBdRecolectorParaEntrega Recolector { get; } = new();
        public FakeClientStoreParaEntrega ClientStore { get; } = new();
        public FakeBlobsControlable Blobs { get; } = new();
        public InformeValorPreviewApiTests.FakeReservationServiceControlable Reservations { get; } = new();
        public InformeValorPreviewApiTests.FakeAzureReservationsClientControlable ReservationsClient { get; } = new();
        public FakeModulePermissionStore Perms { get; } = new FakeModulePermissionStore().SeedDefaults();
        public IModulePermissionService Service => Services.GetRequiredService<IModulePermissionService>();
        public InformeValorPreviewApiTests.FakeOptimizationServiceControlable Optimization { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        /// <summary>Una credencial activa, una reserva viva y un consumidor confirmado sobre vm-1, que
        /// es el recurso que factura en <see cref="FakeInformeValorStoreConDatosParaEntrega"/>. Datos
        /// sintéticos: ningún nombre de cliente ni de recurso real.</summary>
        public void SembrarUnaReservaConConsumidor(int clientId, int credentialId, string reservationId)
        {
            Reservations.ConCredencialYReservas(clientId, new CredentialRef(credentialId, "cred-prueba"),
            [
                new ReservationDto(
                    ReservationId: reservationId, CredentialId: credentialId, Name: "Reserva de prueba",
                    Product: "Standard_D2s_v5", Region: "eastus", Quantity: 2, Term: "P1Y", TermLabel: "1 ano",
                    State: "Succeeded", AppliedScopeType: null, AppliedScopes: [], ExpiresOn: "2027-06-01",
                    DaysRemaining: 300, Expired: false, Expiring: false, UtilizationLast: "80%",
                    Utilization7d: "75%"),
            ]);
            ReservationsClient.ConConsumidores(reservationId,
            [
                new ReservationConsumer(
                    InstanceId: "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-1",
                    ResourceName: "vm-1", ResourceGroup: "rg-1", SubscriptionId: "sub-1", SubscriptionName: null,
                    SkuName: "Standard_D2s_v5", UsedHours: 700, LastSeen: "2026-02-20", DaysSeen: 28),
            ]);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAnalysisAccess>();
                services.AddSingleton<IAnalysisAccess>(Access);
                services.RemoveAll<IInformeValorStore>();
                services.AddSingleton<IInformeValorStore>(Store);
                services.RemoveAll<IInsumosBdRecolector>();
                services.AddSingleton<IInsumosBdRecolector>(Recolector);
                services.RemoveAll<IClientStore>();
                services.AddSingleton<IClientStore>(ClientStore);
                services.RemoveAll<IBlobStorageService>();
                services.AddSingleton<IBlobStorageService>(Blobs);
                services.RemoveAll<IReservationService>();
                services.AddSingleton<IReservationService>(Reservations);
                services.RemoveAll<IAzureReservationsClient>();
                services.AddSingleton<IAzureReservationsClient>(ReservationsClient);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(Perms);
                services.RemoveAll<IModulePermissionService>();
                services.AddSingleton<IModulePermissionService, ModulePermissionService>();
                // Entrega 6 (T10): mismo motivo que en InformeValorPreviewApiTests.Factory -- sin
                // este falso, /generar con un rol con acceso al módulo Optimization intentaría
                // EnsureSchemaAsync contra una conexión SQL de verdad.
                services.RemoveAll<IOptimizationService>();
                services.AddSingleton<IOptimizationService>(Optimization);
            });
        }
    }

    public sealed class FakeAccesoPorCliente : IAnalysisAccess
    {
        private readonly HashSet<int> _allowed = [];

        public void Allow(int clientId) => _allowed.Add(clientId);
        public void Deny(int clientId) => _allowed.Remove(clientId);

        public Task<IReadOnlySet<int>?> AccessibleClientIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<int>?>(_allowed);

        public Task<AccessCheck> AssertAnalysisAccessAsync(ClaimsPrincipal user, int analysisId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.NotFound("no aplica"));

        public Task<AccessCheck> AssertCostResultAccessAsync(ClaimsPrincipal user, int costResultId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.NotFound("no aplica"));

        public Task<AccessCheck> AssertClientAccessAsync(ClaimsPrincipal user, int clientId, CancellationToken ct = default)
            => Task.FromResult(_allowed.Contains(clientId) ? AccessCheck.Allow() : AccessCheck.Forbidden());

        public Task<AccessCheck> AssertFileAccessAsync(ClaimsPrincipal user, int fileId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.NotFound("no aplica"));
    }

    /// <summary>
    /// Blob Storage en memoria, controlable: registra cada subida con su contenido (los tests de F1
    /// miran el HTML que se subió de verdad, no el modelo) y cada descarga con su contenedor (así se
    /// prueba que la descarga usa el contenedor archivado y no el configurado).
    ///
    /// <para><see cref="BlobsFaltantes"/> simula el blob borrado con la misma excepción que lanza el
    /// SDK (<see cref="RequestFailedException"/> con 404) y <see cref="BlobsQueRevientan"/> el otro
    /// caso, el que no se puede confundir con ése: el almacenamiento no responde.</para>
    /// </summary>
    public sealed class FakeBlobsControlable : IBlobStorageService
    {
        public List<(string Container, string BlobName, byte[] Data, string? ContentType)> Uploads { get; } = [];
        public List<(string Container, string BlobName)> Descargas { get; } = [];
        public HashSet<string> BlobsFaltantes { get; } = [];
        public HashSet<string> BlobsQueRevientan { get; } = [];
        public bool FallarSubidas { get; set; }

        public Task UploadAsync(
            string containerName, string blobName, byte[] data, string? contentType = null, CancellationToken ct = default)
        {
            if (FallarSubidas) throw new RequestFailedException(500, "Falla simulada de Storage al subir.");
            Uploads.Add((containerName, blobName, data, contentType));
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadAsync(string containerName, string blobName, CancellationToken ct = default)
        {
            Descargas.Add((containerName, blobName));
            if (BlobsQueRevientan.Contains(blobName))
                throw new RequestFailedException(503, "Storage no responde (falla simulada).");
            if (BlobsFaltantes.Contains(blobName))
                throw new RequestFailedException(404, "BlobNotFound (falla simulada).");

            var subida = Uploads.FirstOrDefault(u => u.BlobName == blobName);
            return Task.FromResult(subida.Data ?? Encoding.UTF8.GetBytes("<!doctype html><title>x</title>"));
        }

        public Task DeleteAsync(string containerName, string blobName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Store en memoria: las mismas dos filas de facturación del fixture de la vista previa (enero y
    /// febrero 2026, 1000 + 500) más una bitácora de entregas real, para poder afirmar QUÉ se archivó
    /// y no solo que el endpoint devolvió 200.
    /// </summary>
    public sealed class FakeInformeValorStoreConDatosParaEntrega : IInformeValorStore
    {
        public const int IngestaFacturacion = 4001;
        public const int IngestaCasos = 4002;
        public const int IngestaEvolucion = 4003;

        public List<EntregaNueva> Entregas { get; } = [];
        private readonly Dictionary<(int ClientId, int EntregaId), EntregaArchivada> _archivadas = [];
        private int _siguienteId = 5000;

        /// <summary>Una entrega ya archivada, para los tests de descarga (que no pasan por generar).
        /// <paramref name="container"/> en <c>null</c> es la fila escrita antes de que existiera la
        /// columna.</summary>
        public void SembrarEntrega(int clientId, int entregaId, string? container, string blobName, string fileName)
        {
            var resumen = new EntregaResumen(
                EntregaId: entregaId,
                PeriodStart: new DateOnly(2026, 1, 1), PeriodEnd: new DateOnly(2026, 2, 28),
                Corte: new DateOnly(2026, 3, 1), Variante: "interna",
                BloquesPublicados: [], RbacOrigen: null, FileName: fileName, BlobSizeBytes: 10,
                GeneratedBy: "tester@bit.ec", GeneratedAt: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
            _archivadas[(clientId, entregaId)] = new EntregaArchivada(
                resumen, container, blobName, MesesParcialesForzados: [], RbacCorridaFecha: null,
                SeguridadGestionadaExternamente: false, FacturacionIngestaId: null, CasosIngestaId: null,
                RbacIngestaId: null, EvolucionIngestaId: null, FotoReservas: null, PlantillaVersion: null,
                SummaryJson: null);
        }

        public Task<int> ReplaceFacturacionAsync(
            int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> ReplaceCasosAsync(
            int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> ReplaceRbacAsync(
            int clientId, string fileName, string? user, RbacParseResult parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> ReplaceEvolucionAsync(
            int clientId, string fileName, string? user, ParseResult<EvolucionRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct) => throw new NotSupportedException();

        /// <summary>Facturación, casos y evolución cargados (con su corrida), RBAC no: el tri-estado
        /// de "insumo ausente" tiene que llegar como null a la entrega, no como cero.</summary>
        public Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<InsumoEstado>>(
            [
                new InsumoEstado(
                    SqlInformeValorStore.KindFacturacion, true, "facturacion.xlsx",
                    new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), 2, 0, "ok", [], IngestaFacturacion),
                new InsumoEstado(
                    SqlInformeValorStore.KindCasos, true, "casos.xlsx",
                    new DateTime(2026, 3, 1, 8, 5, 0, DateTimeKind.Utc), 0, 0, "ok", [], IngestaCasos),
                new InsumoEstado(
                    SqlInformeValorStore.KindEvolucion, true, "evolucion.xlsx",
                    new DateTime(2026, 3, 1, 8, 10, 0, DateTimeKind.Utc), 0, 0, "ok", [], IngestaEvolucion),
            ]);

        public Task<IReadOnlyList<FacturacionRow>> GetFacturacionAsync(int clientId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FacturacionRow>>(
            [
                new FacturacionRow(
                    Hash: "h1", Tenant: null, SubscriptionName: "Suscripción Uno", SubscriptionId: "sub-1",
                    ResourceGroup: "rg-1", ResourceName: "vm-1", CostCenter: "Operaciones",
                    Category: "Redes y Conectividad", Subcategory: null, Service: null, Quantity: null,
                    Unit: null, Rate: null, Pvp: 1000m, Year: 2026, Month: 1),
                new FacturacionRow(
                    Hash: "h2", Tenant: null, SubscriptionName: "Suscripción Uno", SubscriptionId: "sub-1",
                    ResourceGroup: "rg-1", ResourceName: "vm-1", CostCenter: "Operaciones",
                    Category: "Redes y Conectividad", Subcategory: null, Service: null, Quantity: null,
                    Unit: null, Rate: null, Pvp: 500m, Year: 2026, Month: 2),
            ]);

        public Task<IReadOnlyList<CasoRow>> GetCasosAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CasoRow>>([]);

        public Task<IReadOnlyList<RbacFila>> GetRbacAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RbacFila>>([]);

        public Task<IReadOnlyList<EvolucionRow>> GetEvolucionAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EvolucionRow>>([]);

        public Task<int> RegistrarEntregaAsync(EntregaNueva entrega, CancellationToken ct)
        {
            Entregas.Add(entrega);
            var entregaId = ++_siguienteId;
            var resumen = new EntregaResumen(
                EntregaId: entregaId, PeriodStart: entrega.PeriodStart, PeriodEnd: entrega.PeriodEnd,
                Corte: entrega.Corte, Variante: entrega.Variante.Clave(),
                BloquesPublicados: entrega.BloquesPublicados.Select(b => b.Clave()).ToList(),
                RbacOrigen: entrega.RbacOrigen, FileName: entrega.FileName,
                BlobSizeBytes: entrega.BlobSizeBytes, GeneratedBy: entrega.GeneratedBy,
                GeneratedAt: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
            _archivadas[(entrega.ClientId, entregaId)] = new EntregaArchivada(
                resumen, entrega.BlobContainer, entrega.BlobName, entrega.MesesParcialesForzados,
                entrega.RbacCorridaFecha, entrega.SeguridadGestionadaExternamente,
                entrega.FacturacionIngestaId, entrega.CasosIngestaId, entrega.RbacIngestaId,
                entrega.EvolucionIngestaId, entrega.FotoReservas, entrega.PlantillaVersion, entrega.SummaryJson);
            return Task.FromResult(entregaId);
        }

        public Task<IReadOnlyList<EntregaResumen>> GetEntregasAsync(int clientId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EntregaResumen>>(
                _archivadas.Where(kv => kv.Key.ClientId == clientId)
                    .OrderByDescending(kv => kv.Key.EntregaId)
                    .Select(kv => kv.Value.Resumen).ToList());

        /// <summary>Filtra por cliente y por id juntos, igual que el WHERE del store real: un
        /// entrega_id adivinado no puede devolver el artefacto de otro cliente.</summary>
        public Task<EntregaArchivada?> GetEntregaAsync(int clientId, int entregaId, CancellationToken ct) =>
            Task.FromResult(_archivadas.GetValueOrDefault((clientId, entregaId)));
    }

    /// <summary>Insumos de base vacíos, con la trazabilidad que la entrega tiene que archivar
    /// (origen del RBAC, fecha de la corrida y la bandera de seguridad gestionada por fuera).</summary>
    public sealed class FakeInsumosBdRecolectorParaEntrega : IInsumosBdRecolector
    {
        public static readonly DateTime FechaCorrida = new(2026, 2, 25, 3, 0, 0, DateTimeKind.Utc);

        public Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult(new InsumosBd(
                Advisor: [], Matriz: [], Rbac: [], Retiros: [],
                EstadoRbac: new EstadoRbacResultado(
                    DisponibilidadRbac.ParcialFaltaIdentidad, new EjesRbac(false, false), FechaCorrida,
                    "Sin datos de prueba."),
                SeguridadGestionadaExternamente: true, SeguridadGestionadaNota: "Gestión externa de prueba.",
                LeidoEn: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                RbacOrigen: InsumosBd.OrigenBase));

        public Task<EstadoRbacResultado> LeerEstadoRbacAsync(int clientId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(EstadoRbacResultado Estado, string? Origen)> LeerEstadoRbacConOrigenAsync(
            int clientId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<HallazgoResueltoFila>> LeerHallazgosResueltosAsync(
            int clientId, CancellationToken ct = default) => throw new NotSupportedException();

        // Entrega 6 (T10): ningún test de esta clase configura el barrido en particular (la doble
        // puerta ya está cubierta en InformeValorPreviewApiTests); mismo default seguro del resto de
        // los fakes de este módulo.
        public Task<RegistroBarrido> LeerBarridoResueltoAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult(RegistroBarrido.SinBarrido());
    }

    public sealed class FakeClientStoreParaEntrega : IClientStore
    {
        public Task<string?> GetNameAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult<string?>("Cliente de Prueba");

        public Task<IReadOnlyList<ClientListItem>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CreateAsync(string clientName, string? taxId, string? contactName, string? contactEmail, string? notes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> NameExistsAsync(string name, int excludeClientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RenameAsync(int clientId, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string Name, string? LogoBlobName)?> GetNameAndLogoAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>> PurgeDataAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyDictionary<string, int> Counts, string? LogoBlobName)> DeleteClientCascadeAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateLogoMetaAsync(int clientId, string blobName, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string? BlobName, string? ContentType)?> GetLogoAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearLogoAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(bool Managed, string? Note)> GetSecurityManagementAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSecurityManagementAsync(int clientId, bool managed, string? note, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
