using System.Globalization;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Api;

/// <summary>
/// Implementacion SQL de las consultas del router de costos. Port de las consultas inline
/// de cost_calculation.py (get_results, get_scenarios, set_manual_cost). SQL parametrizado.
/// </summary>
public sealed class SqlCostResultsQuery(ISqlConnectionFactory factory) : ICostResultsQuery
{
    public async Task<IReadOnlyList<CostResultRow>> GetResultsAsync(
        int analysisId, string? serviceKey, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var sql = """
            SELECT
                cr.cost_result_id, cr.resource_id, cr.service_key,
                r.resource_name, r.resource_type, r.location,
                r.subscription_name, r.resource_group, r.azure_resource_id,
                cr.payg_hourly, cr.payg_monthly,
                cr.ri_1y_monthly, cr.ri_3y_monthly,
                cr.savings_1y_pct, cr.savings_3y_pct,
                cr.savings_1y_monthly, cr.savings_3y_monthly,
                cr.sql_addon_monthly, cr.ahb_discount_monthly, cr.storage_monthly,
                cr.calculation_status, cr.is_variable_pricing, cr.is_manual_cost,
                cr.manual_monthly_cost, cr.manual_cost_note,
                cr.ri_applies, cr.ri_not_applicable_reason,
                cr.ri_coverage, cr.ri_reservation_name, cr.ri_term,
                pu.running_hours AS power_running_hours,
                pu.uptime_pct AS power_uptime_pct,
                pu.period_start AS power_period_start,
                pu.period_end AS power_period_end,
                cr.calculation_notes, cr.calculated_at
            FROM dbo.cost_results cr
            INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
            LEFT JOIN dbo.vm_power_usage pu
                ON pu.resource_id = cr.resource_id AND pu.analysis_id = cr.analysis_id
            WHERE cr.analysis_id = @analysis_id
            """;
        cmd.Parameters.Add(new SqlParameter("@analysis_id", analysisId));

        if (!string.IsNullOrEmpty(serviceKey))
        {
            sql += " AND cr.service_key = @service_key";
            cmd.Parameters.Add(new SqlParameter("@service_key", serviceKey));
        }
        sql += " ORDER BY cr.service_key, r.resource_name";
        cmd.CommandText = sql;

        var rows = new List<CostResultRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CostResultRow
            {
                CostResultId = reader.GetInt32(0),
                ResourceId = reader.GetInt32(1),
                ServiceKey = Str(reader, 2),
                ResourceName = Str(reader, 3),
                ResourceType = Str(reader, 4),
                Location = Str(reader, 5),
                SubscriptionName = Str(reader, 6),
                ResourceGroup = Str(reader, 7),
                AzureResourceId = Str(reader, 8),
                PaygHourly = Dbl(reader, 9),
                PaygMonthly = Dbl(reader, 10),
                Ri1yMonthly = Dbl(reader, 11),
                Ri3yMonthly = Dbl(reader, 12),
                Savings1yPct = Dbl(reader, 13),
                Savings3yPct = Dbl(reader, 14),
                Savings1yMonthly = Dbl(reader, 15),
                Savings3yMonthly = Dbl(reader, 16),
                SqlAddonMonthly = Dbl(reader, 17),
                AhbDiscountMonthly = Dbl(reader, 18),
                StorageMonthly = Dbl(reader, 19),
                CalculationStatus = Str(reader, 20),
                IsVariablePricing = Bool(reader, 21),
                IsManualCost = Bool(reader, 22),
                ManualMonthlyCost = Dbl(reader, 23),
                ManualCostNote = Str(reader, 24),
                RiApplies = Bool(reader, 25),
                RiNotApplicableReason = Str(reader, 26),
                RiCoverage = Str(reader, 27),
                RiReservationName = Str(reader, 28),
                RiTerm = Str(reader, 29),
                PowerRunningHours = Dbl(reader, 30),
                PowerUptimePct = Dbl(reader, 31),
                PowerPeriodStart = IsoDate(reader, 32),
                PowerPeriodEnd = IsoDate(reader, 33),
                CalculationNotes = Str(reader, 34),
                CalculatedAt = IsoDate(reader, 35),
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<ScenarioReadDto>> GetScenariosAsync(int analysisId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var scenarios = new List<ScenarioReadDto>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT scenario_id, scenario_number, scenario_name, description,
                       total_monthly, total_annual,
                       savings_monthly, savings_annual, savings_pct,
                       use_ri_1y, use_ri_3y,
                       eliminate_stopped_vm_disks, eliminate_orphan_disks, eliminate_orphan_ips,
                       calculated_at
                FROM dbo.cost_scenarios
                WHERE analysis_id = @analysis_id
                ORDER BY scenario_number
                """;
            cmd.Parameters.Add(new SqlParameter("@analysis_id", analysisId));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                scenarios.Add(new ScenarioReadDto
                {
                    ScenarioId = reader.GetInt32(0),
                    Number = reader.GetInt32(1),
                    Name = Str(reader, 2),
                    Description = Str(reader, 3),
                    TotalMonthly = Dbl(reader, 4) ?? 0,
                    TotalAnnual = Dbl(reader, 5) ?? 0,
                    SavingsMonthly = Dbl(reader, 6) ?? 0,
                    SavingsAnnual = Dbl(reader, 7) ?? 0,
                    SavingsPct = Dbl(reader, 8) ?? 0,
                    Config = new ScenarioConfigDto
                    {
                        UseRi1y = Bool(reader, 9) ?? false,
                        UseRi3y = Bool(reader, 10) ?? false,
                        EliminateStoppedVmDisks = Bool(reader, 11) ?? false,
                        EliminateOrphanDisks = Bool(reader, 12) ?? false,
                        EliminateOrphanIps = Bool(reader, 13) ?? false,
                    },
                    CalculatedAt = IsoDate(reader, 14),
                });
            }
        }

        // breakdown por escenario (un round-trip por escenario, igual que el Python).
        foreach (var s in scenarios)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT service_key, line_label, monthly_cost, note
                FROM dbo.scenario_breakdown
                WHERE scenario_id = @scenario_id
                ORDER BY service_key
                """;
            cmd.Parameters.Add(new SqlParameter("@scenario_id", s.ScenarioId));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                s.Breakdown.Add(new ScenarioBreakdownDto
                {
                    ServiceKey = Str(reader, 0),
                    LineLabel = Str(reader, 1),
                    MonthlyCost = Dbl(reader, 2) ?? 0,
                    Note = Str(reader, 3),
                });
            }
        }
        return scenarios;
    }

    public async Task<bool> SetManualCostAsync(
        int costResultId, double? manualMonthlyCost, string? manualCostNote, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.cost_results
            SET manual_monthly_cost = @manual_monthly_cost,
                manual_cost_note = @manual_cost_note,
                is_manual_cost = @is_manual_cost,
                calculation_status = @calculation_status
            WHERE cost_result_id = @id
            """;
        var hasCost = manualMonthlyCost is not null;
        cmd.Parameters.Add(new SqlParameter("@manual_monthly_cost", (object?)manualMonthlyCost ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@manual_cost_note", (object?)manualCostNote ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@is_manual_cost", hasCost ? 1 : 0));
        cmd.Parameters.Add(new SqlParameter("@calculation_status", hasCost ? "calculated" : "manual_required"));
        cmd.Parameters.Add(new SqlParameter("@id", costResultId));

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // -------------------- helpers de lectura --------------------

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();

    private static double? Dbl(SqlDataReader r, int i)
        => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);

    private static bool? Bool(SqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        return v switch
        {
            bool b => b,
            _ => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0,
        };
    }

    private static string? IsoDate(SqlDataReader r, int i)
        => r.IsDBNull(i) ? null : r.GetValue(i) is DateTime dt
            ? dt.ToString("o", CultureInfo.InvariantCulture)
            : r.GetValue(i).ToString();
}
