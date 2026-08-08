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
}
