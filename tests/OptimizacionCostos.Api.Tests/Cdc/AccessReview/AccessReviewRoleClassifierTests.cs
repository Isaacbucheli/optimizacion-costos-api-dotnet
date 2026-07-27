using System.Text.Json;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

/// <summary>
/// Los payloads son los `properties` reales de los roles built-in de Azure (api-version 2022-04-01):
/// la clasificación se deriva de los permisos, no de nombres ni de GUIDs, así que los tests tienen
/// que ejercitar los permisos tal como los devuelve ARM.
/// </summary>
public class AccessReviewRoleClassifierTests
{
    private static JsonElement Props(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Owner_es_owner()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Owner","type":"BuiltInRole",
             "permissions":[{"actions":["*"],"notActions":[],"dataActions":[],"notDataActions":[]}]}
            """));

        Assert.Equal("owner", r.RoleClass);
        Assert.False(r.IsCustom);
        Assert.True(AccessReviewRoleClassifier.IsElevated(r.RoleClass));
    }

    [Fact]
    public void Contributor_es_escritura_total()
    {
        // Mismo `actions:["*"]` que Owner: lo que lo distingue es que notActions le quita
        // Microsoft.Authorization/*/Write, o sea la capacidad de otorgar accesos.
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Contributor","type":"BuiltInRole",
             "permissions":[{"actions":["*"],
               "notActions":["Microsoft.Authorization/*/Delete","Microsoft.Authorization/*/Write",
                             "Microsoft.Authorization/elevateAccess/Action",
                             "Microsoft.Blueprint/blueprintAssignments/write",
                             "Microsoft.Blueprint/blueprintAssignments/delete"],
               "dataActions":[],"notDataActions":[]}]}
            """));

        Assert.Equal("escritura_total", r.RoleClass);
        Assert.True(AccessReviewRoleClassifier.IsElevated(r.RoleClass));
    }

    [Fact]
    public void User_access_administrator_es_otorga_accesos()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"User Access Administrator","type":"BuiltInRole",
             "permissions":[{"actions":["*/read","Microsoft.Authorization/*","Microsoft.Support/*"],
               "notActions":[],"dataActions":[],"notDataActions":[]}]}
            """));

        Assert.Equal("otorga_accesos", r.RoleClass);
        Assert.True(AccessReviewRoleClassifier.IsElevated(r.RoleClass));
    }

    [Fact]
    public void Rbac_administrator_es_otorga_accesos()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Role Based Access Control Administrator","type":"BuiltInRole",
             "permissions":[{"actions":["Microsoft.Authorization/roleAssignments/write",
                                        "Microsoft.Authorization/roleAssignments/delete","*/read"],
               "notActions":[],"dataActions":[],"notDataActions":[]}]}
            """));

        Assert.Equal("otorga_accesos", r.RoleClass);
    }

    [Fact]
    public void Reader_es_lectura()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Reader","type":"BuiltInRole",
             "permissions":[{"actions":["*/read"],"notActions":[],"dataActions":[],"notDataActions":[]}]}
            """));

        Assert.Equal("lectura", r.RoleClass);
        Assert.False(AccessReviewRoleClassifier.IsElevated(r.RoleClass));
    }

    [Fact]
    public void Vm_contributor_es_escritura_servicio()
    {
        // Escribe, pero solo sobre providers concretos: no cuenta como elevado a propósito
        // (incluirlo diluye la señal: la mayoría de los *Contributor de servicio caerían ahí).
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Virtual Machine Contributor","type":"BuiltInRole",
             "permissions":[{"actions":["Microsoft.Authorization/*/read","Microsoft.Compute/availabilitySets/*",
                                        "Microsoft.Compute/virtualMachines/*","Microsoft.Network/networkInterfaces/*",
                                        "Microsoft.Storage/storageAccounts/*"],
               "notActions":[],"dataActions":[],"notDataActions":[]}]}
            """));

        Assert.Equal("escritura_servicio", r.RoleClass);
        Assert.False(AccessReviewRoleClassifier.IsElevated(r.RoleClass));
    }

    [Fact]
    public void Rol_de_plano_de_datos_es_escritura_servicio()
    {
        // Storage File Data SMB Share Elevated Contributor: `actions` vacío y toda la escritura en
        // `dataActions`. Si el clasificador solo mirara `actions` caería en "lectura" y subestimaría
        // el riesgo de un rol que sí modifica datos.
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Storage File Data SMB Share Elevated Contributor","type":"BuiltInRole",
             "permissions":[{"actions":[],"notActions":[],
               "dataActions":["Microsoft.Storage/storageAccounts/fileServices/fileShares/files/read",
                              "Microsoft.Storage/storageAccounts/fileServices/fileShares/files/write",
                              "Microsoft.Storage/storageAccounts/fileServices/fileShares/files/delete",
                              "Microsoft.Storage/storageAccounts/fileServices/fileShares/files/modifypermissions/action"],
               "notDataActions":[]}]}
            """));

        Assert.Equal("escritura_servicio", r.RoleClass);
    }

    [Fact]
    public void NotActions_tiene_precedencia_sobre_actions()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Casi UAA","type":"BuiltInRole",
             "permissions":[{"actions":["Microsoft.Authorization/*"],
               "notActions":["Microsoft.Authorization/roleAssignments/write"],
               "dataActions":[],"notDataActions":[]}]}
            """));

        Assert.NotEqual("owner", r.RoleClass);
        Assert.NotEqual("otorga_accesos", r.RoleClass);
        Assert.Equal("escritura_servicio", r.RoleClass);
    }

    [Fact]
    public void Rol_personalizado_con_asterisco_es_owner_y_custom()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Soporte Nivel 3","type":"CustomRole",
             "permissions":[{"actions":["*"],"notActions":[],"dataActions":[],"notDataActions":[]}]}
            """));

        Assert.Equal("owner", r.RoleClass);
        Assert.True(r.IsCustom);
    }

    [Fact]
    public void Rol_personalizado_de_lectura_no_es_elevado()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Auditoria","type":"CustomRole",
             "permissions":[{"actions":["*/read"],"notActions":[],"dataActions":[],"notDataActions":[]}]}
            """));

        Assert.Equal("lectura", r.RoleClass);
        Assert.True(r.IsCustom);
        Assert.False(AccessReviewRoleClassifier.IsElevated(r.RoleClass));
    }

    [Fact]
    public void Sin_permissions_devuelve_lectura()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Vacio","type":"BuiltInRole","permissions":[]}
            """));

        Assert.Equal("lectura", r.RoleClass);
    }

    [Fact]
    public void Properties_sin_permissions_ni_type_no_revienta()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""{"roleName":"Raro"}"""));

        Assert.Equal("lectura", r.RoleClass);
        Assert.False(r.IsCustom);
    }

    [Fact]
    public void Varios_bloques_de_permissions_se_unen()
    {
        var r = AccessReviewRoleClassifier.Classify(Props("""
            {"roleName":"Dos bloques","type":"BuiltInRole",
             "permissions":[
               {"actions":["*/read"],"notActions":[],"dataActions":[],"notDataActions":[]},
               {"actions":["Microsoft.Compute/virtualMachines/write"],"notActions":[],"dataActions":[],"notDataActions":[]}]}
            """));

        Assert.Equal("escritura_servicio", r.RoleClass);
    }

    [Fact]
    public void IsElevated_cubre_las_tres_clases_y_ninguna_mas()
    {
        Assert.True(AccessReviewRoleClassifier.IsElevated("owner"));
        Assert.True(AccessReviewRoleClassifier.IsElevated("otorga_accesos"));
        Assert.True(AccessReviewRoleClassifier.IsElevated("escritura_total"));
        Assert.False(AccessReviewRoleClassifier.IsElevated("escritura_servicio"));
        Assert.False(AccessReviewRoleClassifier.IsElevated("lectura"));
        // Definición de rol no resoluble (definida en otra rama de MG): sin clasificar, no elevada.
        Assert.False(AccessReviewRoleClassifier.IsElevated(null));
    }
}
