using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Construye el resolver de dedup de una corrida de ingesta de Advisor (port de
/// _build_advisor_dedup_resolver de app/routes/waf.py): por cada fila sin match exacto, intenta
/// mapearla a una canónica existente del cliente — alias aprendido → prefiltro determinista →
/// confirmación Azure OpenAI (solo con alta confianza). El estado por corrida (candidatos
/// cacheados, tope IA, fusiones) se devuelve por <paramref name="state"/>.
/// </summary>
public interface IWafDedupResolverFactory
{
    Func<SqlConnection, SqlTransaction, AdvisorRow, Task<int?>> Build(
        int clientId, string? createdBy, int aiLimit, out WafDedupResolverState state);
}
