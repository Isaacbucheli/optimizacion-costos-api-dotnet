using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

/// <summary>
/// Toda la persistencia de decisiones cuelga de esta clave: si cambia entre corridas, las decisiones
/// se "pierden" sin ningún error visible. De ahí el nivel de detalle de estos tests.
/// </summary>
public class AccessReviewAccessKeyTests
{
    private const string Owner = "/subscriptions/aaa/providers/Microsoft.Authorization/roleDefinitions/8e3af657";
    private const string Scope = "/subscriptions/aaa/resourceGroups/rg-app";

    [Fact]
    public void Es_determinista()
    {
        Assert.Equal(
            AccessReviewAccessKey.For("u1", Owner, Scope),
            AccessReviewAccessKey.For("u1", Owner, Scope));
    }

    [Fact]
    public void Devuelve_64_hex()
    {
        var key = AccessReviewAccessKey.For("u1", Owner, Scope);

        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void El_mismo_rol_prefijado_por_distintas_suscripciones_da_la_misma_clave()
    {
        // ARM prefija el roleDefinitionId con la suscripción consultada: una asignación heredada
        // vuelve con N ids distintos para el MISMO rol. Si la clave dependiera del id completo, la
        // decisión se perdería en cada corrida sin que nada avise.
        var a = AccessReviewAccessKey.For("u1", "/subscriptions/aaa/providers/Microsoft.Authorization/roleDefinitions/8e3af657", "/");
        var b = AccessReviewAccessKey.For("u1", "/subscriptions/bbb/providers/Microsoft.Authorization/roleDefinitions/8e3af657", "/");
        var c = AccessReviewAccessKey.For("u1", "/providers/Microsoft.Management/managementGroups/mg1/providers/Microsoft.Authorization/roleDefinitions/8e3af657", "/");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Acepta_el_guid_pelado_igual_que_el_id_completo()
    {
        Assert.Equal(
            AccessReviewAccessKey.For("u1", Owner, Scope),
            AccessReviewAccessKey.For("u1", "8e3af657", Scope));
    }

    [Fact]
    public void Cambia_si_cambia_el_principal_el_rol_o_el_scope()
    {
        var baseKey = AccessReviewAccessKey.For("u1", Owner, Scope);

        Assert.NotEqual(baseKey, AccessReviewAccessKey.For("u2", Owner, Scope));
        Assert.NotEqual(baseKey, AccessReviewAccessKey.For("u1", "/subscriptions/aaa/providers/Microsoft.Authorization/roleDefinitions/acdd72a7", Scope));
        Assert.NotEqual(baseKey, AccessReviewAccessKey.For("u1", Owner, "/subscriptions/aaa"));
    }

    [Fact]
    public void Es_insensible_al_casing()
    {
        // ARM no garantiza el casing de los scopes ni de los GUID: "resourceGroups" y "resourcegroups"
        // aparecen indistintamente y no deben producir dos decisiones separadas.
        Assert.Equal(
            AccessReviewAccessKey.For("U1", Owner, "/subscriptions/AAA/resourceGroups/RG-APP"),
            AccessReviewAccessKey.For("u1", Owner.ToUpperInvariant(), "/subscriptions/aaa/resourcegroups/rg-app"));
    }

    [Fact]
    public void No_confunde_campos_por_concatenacion()
    {
        // Sin separador, ("ab","c") y ("a","bc") colisionarían.
        Assert.NotEqual(
            AccessReviewAccessKey.For("ab", "c", Scope),
            AccessReviewAccessKey.For("a", "bc", Scope));
    }

    [Fact]
    public void Clave_de_hallazgo_es_estable_y_distinta_de_la_de_un_acceso()
    {
        var f = AccessReviewAccessKey.ForFinding("exceso_global_admins");

        Assert.Equal(f, AccessReviewAccessKey.ForFinding("exceso_global_admins"));
        Assert.NotEqual(f, AccessReviewAccessKey.ForFinding("granularidad_recurso"));
        Assert.NotEqual(f, AccessReviewAccessKey.For("", "", ""));
    }
}
