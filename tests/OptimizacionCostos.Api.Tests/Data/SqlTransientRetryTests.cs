using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Tests.Data;

/// <summary>
/// Reintento al abrir la conexión a Azure SQL. Sin él, un corte de red del lado de Azure se lleva la
/// petición que le tocó: el 2026-08-06 a las 15:37 UTC el login devolvió 500 ("Error interno") porque
/// el handshake TDS se cortó con "connection reset by peer", y el mismo error ya estaba en el log del
/// 29 y del 30 de julio con la base al 0% de DTU.
///
/// La decisión de fondo que se prueba acá es la lista negra: se reintenta todo salvo lo que se sabe
/// permanente. Con lista blanca habría que acertarle al número de error de la capa SNI, que no está
/// documentado, y errarle deja el fallo igual que antes.
/// </summary>
public sealed class SqlTransientRetryTests
{
    /// <summary>Excepción cualquiera: SqlException no tiene constructor público, así que la decisión se inyecta.</summary>
    private static Exception Falla(string motivo = "reset by peer") => new InvalidOperationException(motivo);

    private static Func<Exception, bool> SiempreReintentable => _ => true;
    private static Func<Exception, bool> NuncaReintentable => _ => false;

    /// <summary>Registra los reintentos sin dormir: las pruebas no dependen del reloj.</summary>
    private sealed class Reintentos
    {
        public List<TimeSpan> Esperas { get; } = [];

        public Func<int, Exception, TimeSpan, CancellationToken, Task> Registrar =>
            (_, _, espera, _) =>
            {
                Esperas.Add(espera);
                return Task.CompletedTask;
            };
    }

    [Fact]
    public async Task Cuando_abre_bien_no_reintenta()
    {
        var reintentos = new Reintentos();
        var llamadas = 0;

        var conexion = await SqlTransientRetry.EjecutarAsync(
            _ => { llamadas++; return Task.FromResult("conexion"); },
            SiempreReintentable, reintentos.Registrar, CancellationToken.None);

        Assert.Equal("conexion", conexion);
        Assert.Equal(1, llamadas);
        Assert.Empty(reintentos.Esperas);
    }

    [Fact]
    public async Task Un_fallo_transitorio_y_la_segunda_pasa()
    {
        // El caso real: el corte dura menos que la espera, así que el segundo intento entra.
        var reintentos = new Reintentos();
        var llamadas = 0;

        var conexion = await SqlTransientRetry.EjecutarAsync(
            _ =>
            {
                llamadas++;
                return llamadas == 1 ? Task.FromException<string>(Falla()) : Task.FromResult("conexion");
            },
            SiempreReintentable, reintentos.Registrar, CancellationToken.None);

        Assert.Equal("conexion", conexion);
        Assert.Equal(2, llamadas);
        Assert.Single(reintentos.Esperas);
    }

    [Fact]
    public async Task Se_rinde_al_tercer_intento_y_sube_la_ultima_excepcion()
    {
        // Que se rinda importa tanto como que reintente: si la base está caída de verdad, la petición
        // tiene que fallar rápido y no quedarse dando vueltas.
        var reintentos = new Reintentos();
        var llamadas = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlTransientRetry.EjecutarAsync<string>(
                _ => { llamadas++; return Task.FromException<string>(Falla($"corte {llamadas}")); },
                SiempreReintentable, reintentos.Registrar, CancellationToken.None));

        Assert.Equal(SqlTransientRetry.Intentos, llamadas);
        Assert.Equal(SqlTransientRetry.Intentos - 1, reintentos.Esperas.Count);
        Assert.Equal("corte 3", ex.Message); // la del último intento, no la del primero
    }

    [Fact]
    public async Task Un_error_no_reintentable_no_se_repite()
    {
        // Una contraseña mal puesta no mejora por insistir, y cada repetición suma un fallo de
        // autenticación en la auditoría del servidor.
        var reintentos = new Reintentos();
        var llamadas = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlTransientRetry.EjecutarAsync<string>(
                _ => { llamadas++; return Task.FromException<string>(Falla("Login failed for user")); },
                NuncaReintentable, reintentos.Registrar, CancellationToken.None));

        Assert.Equal(1, llamadas);
        Assert.Empty(reintentos.Esperas);
    }

    [Fact]
    public async Task Con_el_token_cancelado_no_reintenta()
    {
        // Si quien pidió ya se fue, repetir solo gasta una conexión del pool.
        var reintentos = new Reintentos();
        var llamadas = 0;
        using var cancelado = new CancellationTokenSource();
        await cancelado.CancelAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlTransientRetry.EjecutarAsync<string>(
                _ => { llamadas++; return Task.FromException<string>(Falla()); },
                SiempreReintentable, reintentos.Registrar, cancelado.Token));

        Assert.Equal(1, llamadas);
        Assert.Empty(reintentos.Esperas);
    }

    [Theory]
    [InlineData(18456)] // Login failed for user
    [InlineData(18452)] // dominio no confiable
    [InlineData(18470)] // cuenta deshabilitada
    [InlineData(916)]   // el principal no tiene acceso a la base
    [InlineData(40615)] // la IP no está en el firewall
    [InlineData(40532)] // no se puede abrir el servidor pedido en el login
    [InlineData(-2)]    // timeout: ConnectTimeout ya son 30 s, tres intentos serían minuto y medio
    public void Los_errores_permanentes_no_se_reintentan(int numero)
    {
        Assert.False(SqlTransientRetry.EsReintentable(numero));
    }

    [Theory]
    [InlineData(10053)] // transporte: conexión abortada
    [InlineData(10054)] // transporte: reset by peer
    [InlineData(10060)] // no se pudo alcanzar el servidor
    [InlineData(40613)] // base no disponible (reconfiguración de Azure)
    [InlineData(40197)] // el servicio encontró un error procesando la petición
    [InlineData(10928)] // límite de recursos
    [InlineData(233)]   // no hay proceso al otro lado del canal
    [InlineData(64)]    // conexión terminada durante el login
    [InlineData(4060)]  // en Azure suele ser la base ocupada, no un nombre mal escrito
    public void Los_errores_transitorios_conocidos_se_reintentan(int numero)
    {
        Assert.True(SqlTransientRetry.EsReintentable(numero));
    }

    [Theory]
    [InlineData(35)]        // "TCP Provider, error: 35", el de la caída del 2026-08-06
    [InlineData(121)]
    [InlineData(-2146893019)]
    [InlineData(int.MinValue)]
    public void Un_numero_desconocido_cuenta_como_transitorio(int numero)
    {
        // Es la razón de ser de la lista negra: los errores de la capa SNI no tienen número
        // documentado y son justo los que hay que reintentar. Si esto pasara a lista blanca, el
        // fallo que originó todo esto volvería a devolver 500.
        Assert.True(SqlTransientRetry.EsReintentable(numero));
    }

    [Fact]
    public void Las_esperas_crecen_y_no_pasan_de_un_segundo()
    {
        var primera = SqlTransientRetry.Espera(1);
        var segunda = SqlTransientRetry.Espera(2);

        Assert.True(primera < segunda, "la segunda espera tiene que ser mayor que la primera");
        // Del otro lado hay alguien mirando la pantalla de ingreso: el reintento completo no puede
        // volverse más lento que el error que evita.
        Assert.InRange(primera + segunda, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    public void Un_numero_de_intento_fuera_de_rango_no_revienta(int intento)
    {
        // Espera() se llama con el contador del bucle; que no lance protege de un cambio futuro en
        // Intentos que no venga acompañado de una espera más.
        Assert.InRange(SqlTransientRetry.Espera(intento), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }
}
