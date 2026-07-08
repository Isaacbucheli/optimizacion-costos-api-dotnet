namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>Entrada mínima por fila para los totales comparables.</summary>
public sealed record TotalsInput(double Payg, double? Ri1, double? Ri3, bool ReservedConfirmed);

/// <summary>
/// Totales comparables por hoja (fix del "ahorro inflado" reportado en producción):
/// la columna RI del TOTAL vale RI(elegibles) + PAYG(no elegibles) — lo que se pagaría
/// de verdad reservando todo lo reservable — y el ahorro sale de comparar contra PAYG total.
/// </summary>
public sealed record ComparableTotals(
    int RowCount, int EligibleCount, int NotEligibleCount,
    double PaygTotal, double PaygEligible, double PaygNotEligible,
    double Ri1Eligible, double Ri3Eligible,
    double Total1, double Total3,
    double Savings1, double Savings3,
    double Savings1Pct, double Savings3Pct);

public static class ComparableTotalsCalculator
{
    /// <summary>Regla canónica (idéntica al toggle del front): reservado confirmado NUNCA es elegible.</summary>
    public static bool IsEligible(double? ri1, double? ri3, bool reservedConfirmed)
        => !reservedConfirmed && (ri1 is not null || ri3 is not null);

    public static ComparableTotals Compute(IReadOnlyList<TotalsInput> rows)
    {
        double paygT = 0, paygE = 0, paygNe = 0, ri1E = 0, ri3E = 0;
        int elig = 0;
        foreach (var r in rows)
        {
            paygT += r.Payg;
            if (IsEligible(r.Ri1, r.Ri3, r.ReservedConfirmed))
            {
                elig++;
                paygE += r.Payg;
                ri1E += r.Ri1 ?? r.Payg;   // sin precio 1A → sigue pagando PAYG en esa columna
                ri3E += r.Ri3 ?? r.Payg;
            }
            else paygNe += r.Payg;
        }
        var total1 = ri1E + paygNe;
        var total3 = ri3E + paygNe;
        var s1 = paygT - total1;
        var s3 = paygT - total3;
        return new ComparableTotals(
            rows.Count, elig, rows.Count - elig,
            paygT, paygE, paygNe, ri1E, ri3E, total1, total3, s1, s3,
            paygT > 0 ? s1 / paygT : 0, paygT > 0 ? s3 / paygT : 0);
    }
}
