using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican el texto del SQL expuesto (mismo estilo que
/// AdvisorRecolectorTests/MatrizRecolectorTests). El comportamiento real contra Azure SQL real
/// (Importante 1 de la revisión de rama: credencial desactivada) lo cubre
/// InformeValorRecolectoresDbTests, gateado por BIT_INTEGRATION_DB=1.
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
}
