using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>Persiste/borra cost_results. Port de _insert_cost_results/_delete de cost_engine.py.</summary>
public sealed class SqlCostResultStore(ISqlConnectionFactory factory) : ICostResultStore
{
    public void DeleteAllForAnalysis(int analysisId)
    {
        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.cost_results WHERE analysis_id = @a";
        cmd.Parameters.Add(new SqlParameter("@a", analysisId));
        cmd.ExecuteNonQuery();
    }

    public void DeleteForService(int analysisId, string serviceKey)
    {
        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.cost_results WHERE analysis_id = @a AND service_key = @s";
        cmd.Parameters.Add(new SqlParameter("@a", analysisId));
        cmd.Parameters.Add(new SqlParameter("@s", serviceKey));
        cmd.ExecuteNonQuery();
    }

    public void Insert(IReadOnlyList<CostResult> results)
    {
        if (results.Count == 0) return;

        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        EnsureFinOpsColumns(conn);
        foreach (var r in results)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dbo.cost_results (
                    analysis_id, resource_id, service_key,
                    payg_hourly, payg_monthly, ri_1y_monthly, ri_3y_monthly,
                    savings_1y_pct, savings_3y_pct, savings_1y_monthly, savings_3y_monthly,
                    sql_addon_monthly, ahb_discount_monthly, storage_monthly,
                    calculation_status, is_variable_pricing, is_manual_cost,
                    manual_monthly_cost, manual_cost_note,
                    ri_applies, ri_not_applicable_reason, calculation_notes,
                    payg_meter_id, ri_eligibility
                ) VALUES (@analysis_id, @resource_id, @service_key,
                    @payg_hourly, @payg_monthly, @ri_1y_monthly, @ri_3y_monthly,
                    @savings_1y_pct, @savings_3y_pct, @savings_1y_monthly, @savings_3y_monthly,
                    @sql_addon_monthly, @ahb_discount_monthly, @storage_monthly,
                    @calculation_status, @is_variable_pricing, @is_manual_cost,
                    @manual_monthly_cost, @manual_cost_note,
                    @ri_applies, @ri_not_applicable_reason, @calculation_notes,
                    @payg_meter_id, @ri_eligibility)
                """;
            var p = cmd.Parameters;
            p.Add(new SqlParameter("@analysis_id", r.AnalysisId));
            p.Add(new SqlParameter("@resource_id", r.ResourceId));
            p.Add(new SqlParameter("@service_key", r.ServiceKey));
            p.Add(Nullable("@payg_hourly", r.PaygHourly));
            p.Add(Nullable("@payg_monthly", r.PaygMonthly));
            p.Add(Nullable("@ri_1y_monthly", r.Ri1yMonthly));
            p.Add(Nullable("@ri_3y_monthly", r.Ri3yMonthly));
            p.Add(Nullable("@savings_1y_pct", r.Savings1yPct));
            p.Add(Nullable("@savings_3y_pct", r.Savings3yPct));
            p.Add(Nullable("@savings_1y_monthly", r.Savings1yMonthly));
            p.Add(Nullable("@savings_3y_monthly", r.Savings3yMonthly));
            p.Add(Nullable("@sql_addon_monthly", r.SqlAddonMonthly));
            p.Add(Nullable("@ahb_discount_monthly", r.AhbDiscountMonthly));
            p.Add(Nullable("@storage_monthly", r.StorageMonthly));
            p.Add(new SqlParameter("@calculation_status", r.CalculationStatus));
            p.Add(new SqlParameter("@is_variable_pricing", r.IsVariablePricing ? 1 : 0));
            p.Add(new SqlParameter("@is_manual_cost", r.IsManualCost ? 1 : 0));
            p.Add(Nullable("@manual_monthly_cost", r.ManualMonthlyCost));
            p.Add(Nullable("@manual_cost_note", r.ManualCostNote));
            p.Add(new SqlParameter("@ri_applies", r.RiApplies ? 1 : 0));
            p.Add(Nullable("@ri_not_applicable_reason", r.RiNotApplicableReason));
            p.Add(Nullable("@calculation_notes", r.CalculationNotes));
            p.Add(Nullable("@payg_meter_id", r.PaygMeterId));
            p.Add(Nullable("@ri_eligibility", r.RiEligibility));
            cmd.ExecuteNonQuery();
        }
    }

    private static SqlParameter Nullable(string name, object? value)
        => new(name, value ?? DBNull.Value);

    private static bool _columnsEnsured;

    /// <summary>
    /// Agrega columnas payg_meter_id/ri_eligibility a dbo.cost_results si faltan
    /// (schema-lazy, idempotente). internal: SqlCostResultsQuery lo reusa (Task 7).
    /// </summary>
    internal static void EnsureFinOpsColumns(SqlConnection conn)
    {
        if (_columnsEnsured) return;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF COL_LENGTH('dbo.cost_results', 'payg_meter_id') IS NULL
                ALTER TABLE dbo.cost_results ADD payg_meter_id NVARCHAR(100) NULL;
            IF COL_LENGTH('dbo.cost_results', 'ri_eligibility') IS NULL
                ALTER TABLE dbo.cost_results ADD ri_eligibility NVARCHAR(20) NULL;
            """;
        cmd.ExecuteNonQuery();
        _columnsEnsured = true;
    }
}
