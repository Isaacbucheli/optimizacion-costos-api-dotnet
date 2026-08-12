using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Ida y vuelta REAL contra Azure SQL de RoleClass/IsCustomRole en <c>informe_valor_rbac</c>
/// (Tarea 3 del cable de RBAC): parsear el Excel de respaldo, guardar con
/// <see cref="SqlInformeValorStore.ReplaceRbacAsync"/>, releer con
/// <see cref="SqlInformeValorStore.GetRbacAsync"/> y confirmar que la clase de rol sobrevive el
/// viaje completo. Antes de esta tarea <c>informe_valor_rbac</c> no tenía columnas para estos dos
/// campos y volvían null/false al releer (ver el comentario de clase de <see cref="RbacRow"/>);
/// desde que <see cref="Calculo.SeguridadCalculador"/> clasifica por <c>RoleClass</c>, esa pérdida
/// dejaría de ser inofensiva para todo cliente que suba este Excel.
///
/// <para>Mismo gate que <c>DbRoundTripTests</c>/<c>WafAdvisorNameEnTests</c>: no-op sin
/// <c>BIT_INTEGRATION_DB=1</c>, así que la suite normal (<c>dotnet test</c> sin esa variable) nunca
/// toca Azure SQL.</para>
/// </summary>
public class InformeValorRbacRoleClassRoundTripDbTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BIT_INTEGRATION_DB") == "1";

    private static ISqlConnectionFactory NewFactory() =>
        new SqlConnectionFactory(
            AppConfig.FromConfiguration(new ConfigurationBuilder().Build()),
            NullLogger<SqlConnectionFactory>.Instance);

    [Fact]
    public async Task RoleClass_e_IsCustomRole_sobreviven_parsear_guardar_y_releer()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var factory = NewFactory();
        var clients = new SqlClientStore(factory);
        var store = new SqlInformeValorStore(factory);

        var tag = $"e2e-rbac-roleclass-{Guid.NewGuid():N}";
        var clientId = await clients.CreateAsync($"E2E rbac roleclass {tag}", null, null, null, null);
        try
        {
            // Rol personalizado con permisos de Owner: el caso exacto que motivó clasificar por
            // RoleClass en vez de por nombre (Tarea 2). Si estas dos columnas no sobrevivieran la
            // vuelta por la base, SeguridadCalculador contaría este rol con el respaldo por
            // nombre -- y "Rol Interno de Produccion" no matchea ningún regex.
            string?[] cabecera =
            [
                "Suscripción", "Scope", "Nivel", "Rol", "Clase de rol", "Rol personalizado", "Tipo",
                "Nombre", "Correo / Login", "Tipo usuario", "Vía grupo", "Cuenta activa",
                "Último login", "MFA",
            ];
            string?[] filaPersonalizada =
            [
                $"Sub {tag}", "/subscriptions/s1", "subscription", "Rol Interno de Produccion",
                "Owner (otorga accesos)", "Sí", "User", "Ana Perez", $"ana-{tag}@x.com", "Member",
                "", "Sí", "2026-01-05 10:00", "",
            ];
            using var xlsx = XlsxRowReaderTests.BuildXlsx(
                [cabecera, filaPersonalizada], sheetName: RbacParser.HojaAsignaciones);

            var parsed = RbacParser.Parse(xlsx);
            var filaParseada = Assert.Single(parsed.Rows);
            Assert.Equal(AccessReviewRoleClassifier.Owner, filaParseada.RoleClass);
            Assert.True(filaParseada.IsCustomRole);

            await store.ReplaceRbacAsync(clientId, "rbac.xlsx", "e2e", parsed, CancellationToken.None);

            var releidas = await store.GetRbacAsync(clientId, CancellationToken.None);
            var filaReleida = Assert.Single(releidas);

            Assert.Equal(AccessReviewRoleClassifier.Owner, filaReleida.RoleClass);
            Assert.True(filaReleida.IsCustomRole);
        }
        finally
        {
            await store.DeleteInsumoAsync(clientId, SqlInformeValorStore.KindRbac, CancellationToken.None);
            await clients.DeleteClientCascadeAsync(clientId);
        }
    }
}
