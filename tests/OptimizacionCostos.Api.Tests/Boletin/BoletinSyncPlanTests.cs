using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinSyncPlanTests
{
    private static readonly IReadOnlySet<int> Ninguna = new HashSet<int>();

    private static Dictionary<int, List<string>> Grupos() => new()
    {
        [1] = ["sub-a", "sub-b"],
        [2] = ["sub-c"],
    };

    [Fact]
    public void SinFallasTodasLasSubsQuedanExitosasEnAmbasFuentes()
    {
        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), Ninguna, Ninguna, Ninguna, Ninguna);

        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, result[RetirementRow.SourceAdvisor].OrderBy(s => s));
        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, result[RetirementRow.SourceServiceHealth].OrderBy(s => s));
    }

    [Fact]
    public void CredencialFallidaExcluyeAmbasFuentesDeSusSubs()
    {
        // La credencial 1 no se pudo obtener (token): sub-a/sub-b quedan fuera de AMBAS fuentes,
        // pero la credencial 2 (sub-c) no se ve afectada.
        var credencialFallida = new HashSet<int> { 1 };

        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), credencialFallida, Ninguna, Ninguna, Ninguna);

        Assert.Equal(["sub-c"], result[RetirementRow.SourceAdvisor]);
        Assert.Equal(["sub-c"], result[RetirementRow.SourceServiceHealth]);
    }

    [Fact]
    public void QueryDeUnaSolaFuenteFallidaExcluyeSoloEsaFuente()
    {
        // La credencial 1 obtuvo token OK, pero la query de Advisor falló para esa credencial:
        // sub-a/sub-b quedan fuera de 'advisor' pero siguen exitosas en 'service_health'.
        var advisorFallido = new HashSet<int> { 1 };

        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), Ninguna, advisorFallido, Ninguna, Ninguna);

        Assert.Equal(["sub-c"], result[RetirementRow.SourceAdvisor]);
        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, result[RetirementRow.SourceServiceHealth].OrderBy(s => s));
    }

    [Fact]
    public void QueryDeServiceHealthFallidaExcluyeSoloEsaFuente()
    {
        var healthFallido = new HashSet<int> { 2 };

        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), Ninguna, Ninguna, healthFallido, Ninguna);

        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, result[RetirementRow.SourceAdvisor].OrderBy(s => s));
        Assert.Equal(["sub-a", "sub-b"], result[RetirementRow.SourceServiceHealth].OrderBy(s => s));
    }

    [Fact]
    public void TodasLasCredencialesFallidasDejaAmbasFuentesVacias()
    {
        // Caso real observado en el E2E: todas las credenciales fallan → nada exitoso en ninguna
        // fuente → ReconcileAsync no debe ejecutarse (lista vacía = no-op) y no se "auto-resuelve" nada.
        var todasFallidas = new HashSet<int> { 1, 2 };

        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), todasFallidas, Ninguna, Ninguna, Ninguna);

        Assert.Empty(result[RetirementRow.SourceAdvisor]);
        Assert.Empty(result[RetirementRow.SourceServiceHealth]);
    }

    [Fact]
    public void EolCaidoExcluyeSoloEsaFuente()
    {
        var groups = new Dictionary<int, List<string>> { [1] = ["sub-1"], [2] = ["sub-2"] };
        var vacio = new HashSet<int>();
        var por = BoletinSyncPlan.SuccessfulSubscriptionsBySource(
            groups, vacio, vacio, vacio, eolFailedCredentials: new HashSet<int> { 1 });

        Assert.Contains("sub-1", por[RetirementRow.SourceAdvisor]);        // otras fuentes intactas
        Assert.Contains("sub-1", por[RetirementRow.SourceServiceHealth]);
        Assert.DoesNotContain("sub-1", por[RetirementRow.SourceEol]);      // eol NO reconcilia sub-1
        Assert.Contains("sub-2", por[RetirementRow.SourceEol]);
    }

    [Fact]
    public void CredencialCaidaExcluyeTambienEol()
    {
        var groups = new Dictionary<int, List<string>> { [1] = ["sub-1"] };
        var vacio = new HashSet<int>();
        var por = BoletinSyncPlan.SuccessfulSubscriptionsBySource(
            groups, failedCredentials: new HashSet<int> { 1 }, vacio, vacio, vacio);
        Assert.Empty(por[RetirementRow.SourceEol]);
    }

    [Fact]
    public void SinErroresElOutcomeEsCompletedSinError()
    {
        var (status, error) = BoletinSyncPlan.DetermineOutcome([]);

        Assert.Equal("completed", status);
        Assert.Null(error);
    }

    [Fact]
    public void ConErroresElOutcomeEsPartialConJsonDeErrores()
    {
        var errores = new List<object>
        {
            new { source = "credential", credential_id = 1, error = "AuthenticationFailedException" },
        };

        var (status, error) = BoletinSyncPlan.DetermineOutcome(errores);

        Assert.Equal("partial", status);
        Assert.NotNull(error);
        Assert.Contains("AuthenticationFailedException", error);
        Assert.Contains("\"credential_id\":1", error);
    }

    [Fact]
    public void ConTodasLasCredencialesFallidasSigueSiendoPartialNoCompleted()
    {
        // El bug original: un sync donde TODAS las credenciales fallan terminaba 'completed'.
        // Con errores acumulados (aunque sean solo de credencial) debe quedar 'partial'.
        var errores = new List<object>
        {
            new { source = "credential", credential_id = 1, error = "AuthenticationFailedException" },
            new { source = "credential", credential_id = 2, error = "AuthenticationFailedException" },
        };

        var (status, _) = BoletinSyncPlan.DetermineOutcome(errores);

        Assert.Equal("partial", status);
    }

    // -------------------- EolCatalogPlan --------------------

    [Fact]
    public void CatalogoIlegibleAbstieneEolPorCompleto()
    {
        var (run, failed) = BoletinSyncPlan.EolCatalogPlan(readOk: false, activeEntries: 0, credentialIds: [1, 2]);
        Assert.False(run);
        Assert.Equal(new HashSet<int> { 1, 2 }, failed);
    }

    [Fact]
    public void CatalogoVacioNoConsultaPeroSiReconcilia()
    {
        var (run, failed) = BoletinSyncPlan.EolCatalogPlan(readOk: true, activeEntries: 0, credentialIds: [1]);
        Assert.False(run);
        Assert.Empty(failed); // sin failed ⇒ el scope eol queda completo ⇒ reconcile limpia hallazgos
    }

    [Fact]
    public void CatalogoConEntradasConsultaYReconcilia()
    {
        var (run, failed) = BoletinSyncPlan.EolCatalogPlan(readOk: true, activeEntries: 5, credentialIds: [1]);
        Assert.True(run);
        Assert.Empty(failed);
    }

    // -------------------- HealthReconcileScopes --------------------

    [Fact]
    public void EnriquecimientoFallidoDejaEsasSubsEnScopeSoloSubLevel()
    {
        // La credencial 1 obtuvo la base de health OK, pero ServiceHealthImpactedResources
        // (enriquecimiento) falló para ella. sub-a/sub-b deben reconciliarse SOLO a nivel de
        // suscripción (azure_resource_id IS NULL): las filas resource-level de un sync anterior no
        // se tocan porque no se pudo volver a consultar si siguen vigentes.
        var enriquecimientoFallido = new HashSet<int> { 1 };

        var (full, excludeDerived, subLevelOnly) = BoletinSyncPlan.HealthReconcileScopes(
            Grupos(), Ninguna, Ninguna, enriquecimientoFallido, Ninguna);

        Assert.Equal(["sub-c"], full);
        Assert.Equal(new[] { "sub-a", "sub-b" }, subLevelOnly.OrderBy(s => s));
        Assert.Empty(excludeDerived);
    }

    [Fact]
    public void EnriquecimientoOkDejaTodasLasSubsEnScopeCompleto()
    {
        var (full, excludeDerived, subLevelOnly) = BoletinSyncPlan.HealthReconcileScopes(
            Grupos(), Ninguna, Ninguna, Ninguna, Ninguna);

        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, full.OrderBy(s => s));
        Assert.Empty(subLevelOnly);
        Assert.Empty(excludeDerived);
    }

    [Fact]
    public void CredencialOHealthBaseCaidaQuedaFueraDeLosTresScopesSinImportarElEnriquecimiento()
    {
        // Los sets vacíos por credencial/health caídos ya están cubiertos en
        // SuccessfulSubscriptionsBySource; acá solo se verifica que HealthReconcileScopes respeta
        // la misma precedencia y no "recupera" esas subs en ningún otro alcance por error.
        var credencialCaida = new HashSet<int> { 1 };
        var healthBaseFallida = new HashSet<int> { 2 };

        var (full, excludeDerived, subLevelOnly) = BoletinSyncPlan.HealthReconcileScopes(
            Grupos(), credencialCaida, healthBaseFallida, Ninguna, Ninguna);

        Assert.Empty(full);
        Assert.Empty(excludeDerived);
        Assert.Empty(subLevelOnly);
    }

    [Fact]
    public void CicloDeDosSyncsEnriquecimientoCaidoNoDejaLaSubEnElScopeCompleto()
    {
        // Documenta el ciclo del finding a nivel de la lógica pura: sync N con enriquecimiento OK →
        // sub-a entra al scope completo (sus filas resource-level se reconcilian normalmente si
        // dejan de verse). Sync N+1, misma credencial, pero el enriquecimiento falla → sub-a cae a
        // "solo sub-level": ReconcileAsync ya no puede marcar 'resuelto' las filas resource-level que
        // vinieron del sync N, porque este sync no sabe si siguen vigentes.
        var grupos = new Dictionary<int, List<string>> { [1] = ["sub-a"] };

        var syncN = BoletinSyncPlan.HealthReconcileScopes(grupos, Ninguna, Ninguna, Ninguna, Ninguna);
        Assert.Equal(["sub-a"], syncN.FullScope);
        Assert.Empty(syncN.SubLevelOnly);
        Assert.Empty(syncN.ExcludeDerived);

        var enriquecimientoCaidoEnCred1 = new HashSet<int> { 1 };
        var syncNMasUno = BoletinSyncPlan.HealthReconcileScopes(grupos, Ninguna, Ninguna, enriquecimientoCaidoEnCred1, Ninguna);
        Assert.Empty(syncNMasUno.FullScope);
        Assert.Equal(["sub-a"], syncNMasUno.SubLevelOnly);
        Assert.Empty(syncNMasUno.ExcludeDerived);
    }

    // -------------------- HealthReconcileScopes: detector de inventario (Task 5) --------------------

    [Fact]
    public void DetectorCaidoProtegeDerivadasPeroReconciliaElResto()
    {
        // La credencial 1 tuvo el detector de inventario caído (no se pudo re-consultar
        // LinuxSiteRuntimes/WindowsSites): sus filas DERIVADAS (inferidas por BIT) no se tocan
        // -por eso ExcludeDerived, no FullScope-, pero las filas confirmadas por Microsoft de esa
        // misma sub sí se reconcilian normalmente (derived = 0 nunca depende del detector).
        var groups = new Dictionary<int, List<string>> { [1] = ["sub-1"], [2] = ["sub-2"] };
        var scopes = BoletinSyncPlan.HealthReconcileScopes(
            groups, failedCredentials: Ninguna, healthFailedCredentials: Ninguna,
            healthResourcesFailedCredentials: Ninguna, detectorFailedCredentials: new HashSet<int> { 1 });

        Assert.Contains("sub-2", scopes.FullScope);          // todo OK → alcance completo
        Assert.Contains("sub-1", scopes.ExcludeDerived);     // detector caído → derivadas intocables
        Assert.DoesNotContain("sub-1", scopes.FullScope);
        Assert.Empty(scopes.SubLevelOnly);
    }

    [Fact]
    public void EnriquecimientoCaidoSigueMandandoSobreElDetector()
    {
        // Si ADEMÁS el enriquecimiento (ServiceHealthImpactedResources) falló para la misma
        // credencial, gana el alcance más restrictivo (SubLevelOnly): ni las derivadas ni las
        // resource-level confirmadas se tocan, sin importar que el detector también haya fallado.
        var groups = new Dictionary<int, List<string>> { [1] = ["sub-1"] };
        var scopes = BoletinSyncPlan.HealthReconcileScopes(
            groups, Ninguna, Ninguna,
            healthResourcesFailedCredentials: new HashSet<int> { 1 },
            detectorFailedCredentials: new HashSet<int> { 1 });

        Assert.Contains("sub-1", scopes.SubLevelOnly);       // el más restrictivo gana
        Assert.Empty(scopes.FullScope);
        Assert.Empty(scopes.ExcludeDerived);
    }
}
