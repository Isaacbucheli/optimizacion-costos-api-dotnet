using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Scenarios;

/// <summary>
/// Acceso a datos SQL para escenarios. Port de las consultas de scenarios.py.
/// Asume que las columnas ri_coverage de cost_results ya existen (las crea el app FastAPI
/// vía ensure_ri_coverage_schema; aquí no se altera el esquema de prod).
/// </summary>
public sealed class SqlScenarioDataSource(ISqlConnectionFactory factory) : IScenarioDataSource
{
    public IReadOnlyList<ScenarioResultRow> LoadResultsWithMetadata(int analysisId)
    {
        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                cr.cost_result_id, cr.resource_id, cr.service_key,
                cr.payg_monthly, cr.ri_1y_monthly, cr.ri_3y_monthly,
                cr.ri_applies, cr.ri_coverage, cr.calculation_status, cr.is_manual_cost,
                cr.manual_monthly_cost, cr.calculation_notes,
                r.resource_name, r.resource_type,
                d.disk_category, d.attached_vm_name,
                p.is_orphan AS ip_is_orphan,
                v.power_state
            FROM dbo.cost_results cr
            INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
            LEFT JOIN dbo.disk_details d ON d.resource_id = cr.resource_id
            LEFT JOIN dbo.public_ip_details p ON p.resource_id = cr.resource_id
            LEFT JOIN dbo.vm_details v ON v.resource_id = cr.resource_id
            WHERE cr.analysis_id = @a
            """;
        cmd.Parameters.Add(new SqlParameter("@a", analysisId));

        var rows = new List<ScenarioResultRow>();
        using var reader = cmd.ExecuteReader();
        double? D(int i) => reader.IsDBNull(i) ? null : Convert.ToDouble(reader.GetValue(i));
        string? S(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i));
        bool B(int i) => !reader.IsDBNull(i) && Convert.ToBoolean(reader.GetValue(i));
        bool? NB(int i) => reader.IsDBNull(i) ? null : Convert.ToBoolean(reader.GetValue(i));

        while (reader.Read())
        {
            rows.Add(new ScenarioResultRow
            {
                CostResultId = reader.GetInt32(0),
                ResourceId = reader.GetInt32(1),
                ServiceKey = S(2),
                PaygMonthly = D(3),
                Ri1yMonthly = D(4),
                Ri3yMonthly = D(5),
                RiApplies = B(6),
                RiCoverage = S(7),
                CalculationStatus = S(8),
                IsManualCost = B(9),
                ManualMonthlyCost = D(10),
                CalculationNotes = S(11),
                ResourceName = S(12),
                ResourceType = S(13),
                DiskCategory = S(14),
                AttachedVmName = S(15),
                IpIsOrphan = NB(16),
                PowerState = S(17),
            });
        }
        return rows;
    }

    public void DeleteScenarios(int analysisId)
    {
        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.cost_scenarios WHERE analysis_id = @a";
        cmd.Parameters.Add(new SqlParameter("@a", analysisId));
        cmd.ExecuteNonQuery();
    }

    public int InsertScenario(
        int analysisId, ScenarioConfig config,
        double totalMonthly, double totalAnnual,
        double savingsMonthly, double savingsAnnual, double savingsPct)
    {
        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.cost_scenarios (
                analysis_id, scenario_number, scenario_name, description,
                use_ri_1y, use_ri_3y,
                eliminate_stopped_vm_disks, eliminate_orphan_disks, eliminate_orphan_ips,
                total_monthly, total_annual,
                savings_monthly, savings_annual, savings_pct
            )
            OUTPUT INSERTED.scenario_id
            VALUES (@a, @num, @name, @desc, @r1, @r3, @es, @eo, @ei,
                    @tm, @ta, @sm, @sa, @sp)
            """;
        var p = cmd.Parameters;
        p.Add(new SqlParameter("@a", analysisId));
        p.Add(new SqlParameter("@num", config.Number));
        p.Add(new SqlParameter("@name", config.Name));
        p.Add(new SqlParameter("@desc", config.Description));
        p.Add(new SqlParameter("@r1", config.UseRi1y ? 1 : 0));
        p.Add(new SqlParameter("@r3", config.UseRi3y ? 1 : 0));
        p.Add(new SqlParameter("@es", config.EliminateStoppedVmDisks ? 1 : 0));
        p.Add(new SqlParameter("@eo", config.EliminateOrphanDisks ? 1 : 0));
        p.Add(new SqlParameter("@ei", config.EliminateOrphanIps ? 1 : 0));
        p.Add(new SqlParameter("@tm", totalMonthly));
        p.Add(new SqlParameter("@ta", totalAnnual));
        p.Add(new SqlParameter("@sm", savingsMonthly));
        p.Add(new SqlParameter("@sa", savingsAnnual));
        p.Add(new SqlParameter("@sp", savingsPct));

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void InsertBreakdown(int scenarioId, ScenarioBreakdownLine line)
    {
        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.scenario_breakdown (scenario_id, service_key, line_label, monthly_cost)
            VALUES (@sid, @svc, @label, @cost)
            """;
        var p = cmd.Parameters;
        p.Add(new SqlParameter("@sid", scenarioId));
        p.Add(new SqlParameter("@svc", line.ServiceKey));
        p.Add(new SqlParameter("@label", line.LineLabel));
        p.Add(new SqlParameter("@cost", line.MonthlyCost));
        cmd.ExecuteNonQuery();
    }
}
