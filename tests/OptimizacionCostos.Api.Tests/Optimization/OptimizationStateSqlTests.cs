namespace OptimizacionCostos.Api.Tests.Optimization;

using OptimizacionCostos.Api.Features.Optimization;

public class OptimizationStateSqlTests
{
    /// <summary>Un consultor que marca 'resuelto' es autoría declarada; reabrir limpia la marca.
    /// Sin esto, el registro del informe de valor no puede separar lo nuestro de lo del cliente.
    /// Las tres ramas del CASE están fijadas: (1) resuelto→'manual', (2) abierto/en_progreso→NULL, (3) else→preservar.</summary>
    [Fact]
    public void El_update_manual_estampa_la_autoria_y_reabrir_la_limpia()
    {
        // Rama 1: cuando se marca resuelto, la autoría es 'manual' (consultor)
        Assert.Contains("WHEN @state = 'resuelto' THEN 'manual'", OptimizationService.UpdateStateSql);

        // Rama 2: cuando se reabre (abierto o en_progreso), la autoría se limpia (NULL)
        Assert.Contains("WHEN @state IN ('abierto', 'en_progreso') THEN NULL", OptimizationService.UpdateStateSql);

        // Rama 3: en cualquier otro estado, se preserva la autoría existente
        Assert.Contains("ELSE resolved_by_kind END", OptimizationService.UpdateStateSql);
    }

    /// <summary>El auto-resuelto del reconcile queda marcado como tal, nunca como declarado.</summary>
    [Fact]
    public void El_auto_resuelto_estampa_auto()
    {
        Assert.Contains("resolved_by_kind = 'auto'", OptimizationService.AutoResolveSql);
    }

    /// <summary>La columna llega por soft-migration: BD existentes no se recrean.</summary>
    [Fact]
    public void El_schema_agrega_la_columna_con_guarda()
    {
        Assert.Contains("COL_LENGTH('dbo.optimization_finding_state', 'resolved_by_kind')",
            OptimizationService.SchemaSql);
    }
}
