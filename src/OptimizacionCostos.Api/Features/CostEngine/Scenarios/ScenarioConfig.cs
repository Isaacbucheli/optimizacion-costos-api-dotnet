namespace OptimizacionCostos.Api.Features.CostEngine.Scenarios;

/// <summary>Configuración de un escenario de optimización. Port de SCENARIOS_CONFIG (scenarios.py).</summary>
public sealed record ScenarioConfig(
    int Number,
    string Name,
    string Description,
    bool UseRi1y,
    bool UseRi3y,
    bool EliminateStoppedVmDisks,
    bool EliminateOrphanDisks,
    bool EliminateOrphanIps)
{
    /// <summary>Los 4 escenarios estándar, en orden, idénticos a SCENARIOS_CONFIG del Python.</summary>
    public static readonly IReadOnlyList<ScenarioConfig> All =
    [
        new(1, "IR 1 Año + Depurar huérfanos",
            "Aplica RI 1 año donde corresponda. Elimina discos huérfanos e IPs sin usar.",
            UseRi1y: true, UseRi3y: false,
            EliminateStoppedVmDisks: true, EliminateOrphanDisks: true, EliminateOrphanIps: true),
        new(2, "IR 1 Año (sin depuración)",
            "Solo aplica RI 1 año. No elimina nada.",
            UseRi1y: true, UseRi3y: false,
            EliminateStoppedVmDisks: false, EliminateOrphanDisks: false, EliminateOrphanIps: false),
        new(3, "IR 3 Años + Depurar huérfanos",
            "Aplica RI 3 años donde corresponda. Elimina discos huérfanos e IPs sin usar.",
            UseRi1y: false, UseRi3y: true,
            EliminateStoppedVmDisks: true, EliminateOrphanDisks: true, EliminateOrphanIps: true),
        new(4, "IR 3 Años (sin depuración)",
            "Solo aplica RI 3 años. No elimina nada.",
            UseRi1y: false, UseRi3y: true,
            EliminateStoppedVmDisks: false, EliminateOrphanDisks: false, EliminateOrphanIps: false),
    ];
}
