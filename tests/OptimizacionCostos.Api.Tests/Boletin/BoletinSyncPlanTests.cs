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
        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), Ninguna, Ninguna, Ninguna);

        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, result[RetirementRow.SourceAdvisor].OrderBy(s => s));
        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, result[RetirementRow.SourceServiceHealth].OrderBy(s => s));
    }

    [Fact]
    public void CredencialFallidaExcluyeAmbasFuentesDeSusSubs()
    {
        // La credencial 1 no se pudo obtener (token): sub-a/sub-b quedan fuera de AMBAS fuentes,
        // pero la credencial 2 (sub-c) no se ve afectada.
        var credencialFallida = new HashSet<int> { 1 };

        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), credencialFallida, Ninguna, Ninguna);

        Assert.Equal(["sub-c"], result[RetirementRow.SourceAdvisor]);
        Assert.Equal(["sub-c"], result[RetirementRow.SourceServiceHealth]);
    }

    [Fact]
    public void QueryDeUnaSolaFuenteFallidaExcluyeSoloEsaFuente()
    {
        // La credencial 1 obtuvo token OK, pero la query de Advisor falló para esa credencial:
        // sub-a/sub-b quedan fuera de 'advisor' pero siguen exitosas en 'service_health'.
        var advisorFallido = new HashSet<int> { 1 };

        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), Ninguna, advisorFallido, Ninguna);

        Assert.Equal(["sub-c"], result[RetirementRow.SourceAdvisor]);
        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, result[RetirementRow.SourceServiceHealth].OrderBy(s => s));
    }

    [Fact]
    public void QueryDeServiceHealthFallidaExcluyeSoloEsaFuente()
    {
        var healthFallido = new HashSet<int> { 2 };

        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), Ninguna, Ninguna, healthFallido);

        Assert.Equal(new[] { "sub-a", "sub-b", "sub-c" }, result[RetirementRow.SourceAdvisor].OrderBy(s => s));
        Assert.Equal(["sub-a", "sub-b"], result[RetirementRow.SourceServiceHealth].OrderBy(s => s));
    }

    [Fact]
    public void TodasLasCredencialesFallidasDejaAmbasFuentesVacias()
    {
        // Caso real observado en el E2E: todas las credenciales fallan → nada exitoso en ninguna
        // fuente → ReconcileAsync no debe ejecutarse (lista vacía = no-op) y no se "auto-resuelve" nada.
        var todasFallidas = new HashSet<int> { 1, 2 };

        var result = BoletinSyncPlan.SuccessfulSubscriptionsBySource(Grupos(), todasFallidas, Ninguna, Ninguna);

        Assert.Empty(result[RetirementRow.SourceAdvisor]);
        Assert.Empty(result[RetirementRow.SourceServiceHealth]);
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
}
