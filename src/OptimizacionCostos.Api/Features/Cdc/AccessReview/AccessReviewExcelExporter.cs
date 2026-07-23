using ClosedXML.Excel;
using OptimizacionCostos.Api.Features.Reports.ExcelV3;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public interface IAccessReviewExcelExporter
{
    ExcelV3Result Generate(string clientName, AccessReviewSnapshot snapshot, AccessReviewKpis kpis, int inactivityDays);
}

/// <summary>XLSX de revisión de accesos: Resumen + 4 datasets. Puro (sin BD).</summary>
public sealed class AccessReviewExcelExporter : IAccessReviewExcelExporter
{
    public ExcelV3Result Generate(string clientName, AccessReviewSnapshot snapshot, AccessReviewKpis kpis, int inactivityDays)
    {
        using var wb = new XLWorkbook();

        // ── Resumen ───────────────────────────────────────────────
        var ws = wb.AddWorksheet("Resumen");
        ws.Cell(1, 1).Value = $"Revisión de accesos — {clientName}";
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"Corrida #{snapshot.Run.RunId} · {snapshot.Run.FinishedAt:yyyy-MM-dd HH:mm} UTC · umbral inactividad {inactivityDays} días";
        var kpiRows = new (string Label, int Value)[]
        {
            ("Total de asignaciones RBAC", kpis.TotalAsignaciones),
            ("Global Administrators", kpis.GlobalAdmins),
            ("Global Administrators sin MFA", kpis.GlobalAdminsSinMfa),
            ("Internos sin MFA con RBAC", kpis.InternosSinMfaConRbac),
            ("Cuentas deshabilitadas con RBAC", kpis.CuentasDeshabilitadasConRbac),
            ($"Cuentas sin login > {inactivityDays} días con RBAC", kpis.CuentasInactivasConRbac),
            ("Guests en el tenant", kpis.GuestsTotal),
            ("Guests inactivos", kpis.GuestsInactivos),
            ("Guests inactivos con permisos", kpis.GuestsInactivosConPermisos),
            ("Service principals únicos", kpis.ServicePrincipalsUnicos),
        };
        for (var i = 0; i < kpiRows.Length; i++)
        {
            ws.Cell(4 + i, 1).Value = kpiRows[i].Label;
            ws.Cell(4 + i, 2).Value = kpiRows[i].Value;
        }
        ws.Cell(15, 1).Value = "Estado por credencial:";
        ws.Cell(15, 1).Style.Font.SetBold();
        for (var i = 0; i < snapshot.Credentials.Count; i++)
        {
            var c = snapshot.Credentials[i];
            ws.Cell(16 + i, 1).Value = c.CredentialName ?? $"credencial {c.CredentialId}";
            ws.Cell(16 + i, 2).Value = $"ARM: {c.ArmStatus} · Graph: {c.GraphStatus}" + (c.Detail is null ? "" : $" · {c.Detail}");
        }
        ws.Columns().AdjustToContents(1, 60);

        static void Sheet<T>(XLWorkbook wb, string name, string[] headers, IReadOnlyList<T> rows,
            Func<T, object?[]> cells)
        {
            var s = wb.AddWorksheet(name);
            for (var c = 0; c < headers.Length; c++)
            {
                s.Cell(1, c + 1).Value = headers[c];
                s.Cell(1, c + 1).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#1A1A2E"))
                    .Font.SetFontColor(XLColor.White);
            }
            for (var r = 0; r < rows.Count; r++)
            {
                var vals = cells(rows[r]);
                for (var c = 0; c < vals.Length; c++)
                    s.Cell(r + 2, c + 1).Value = XLCellValue.FromObject(vals[c]);
            }
            s.SheetView.FreezeRows(1);
            s.RangeUsed()?.SetAutoFilter();
            s.Columns().AdjustToContents(1, 55);
        }

        static string Mfa(string? m) => m switch
        {
            "enabled" => "Habilitado", "disabled" => "No habilitado",
            "unavailable" => "No disponible", _ => "",
        };
        static string Fecha(DateTimeOffset? d) => d?.ToString("yyyy-MM-dd HH:mm") ?? "";

        Sheet(wb, "Asignaciones RBAC",
            ["Suscripción", "Scope", "Nivel", "Rol", "Tipo", "Nombre", "Correo / Login", "Tipo usuario",
             "Vía grupo", "Cuenta activa", "Último login", "MFA"],
            snapshot.Assignments,
            a => [a.SubscriptionName ?? a.SubscriptionId, a.Scope, a.ScopeLevel, a.RoleName, a.PrincipalType,
                  a.DisplayName ?? a.PrincipalObjectId, a.Login ?? "", a.UserType ?? "",
                  a.ViaGroupName ?? "", a.AccountEnabled switch { true => "Sí", false => "No", null => "" },
                  Fecha(a.LastSignIn), Mfa(a.MfaStatus)]);

        Sheet(wb, "Global Administrators",
            ["Nombre", "Correo / UPN", "Tipo", "Cuenta activa", "Último login", "MFA"],
            snapshot.GlobalAdmins,
            g => [g.DisplayName ?? g.ObjectId, g.Upn ?? "", g.UserType ?? "",
                  g.AccountEnabled switch { true => "Sí", false => "No", null => "" },
                  Fecha(g.LastSignIn), Mfa(g.MfaStatus)]);

        Sheet(wb, "Guests",
            ["Nombre", "Email", "Dominio externo", "Cuenta activa", "Estado invitación", "Creado",
             "Último login", "Roles en suscripciones", "MFA"],
            snapshot.Guests,
            g => [g.DisplayName ?? g.ObjectId, g.Email ?? "", g.ExternalDomain ?? "",
                  g.AccountEnabled ? "Sí" : "No", g.ExternalState ?? "", Fecha(g.CreatedAtAzure),
                  Fecha(g.LastSignIn), g.RolesInSubs ?? "Sin permisos directos", Mfa(g.MfaStatus)]);

        Sheet(wb, "Service Principals",
            ["Suscripción", "Scope", "Nivel", "Rol", "Nombre", "AppId"],
            snapshot.Assignments.Where(a => a.PrincipalType == "ServicePrincipal").ToList(),
            a => [a.SubscriptionName ?? a.SubscriptionId, a.Scope, a.ScopeLevel, a.RoleName,
                  a.DisplayName ?? a.PrincipalObjectId, a.Login ?? ""]);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        var fileName = $"Revision_Accesos_{Sanitize(clientName)}_{snapshot.Run.FinishedAt ?? DateTimeOffset.UtcNow:yyyyMMdd}.xlsx";
        return new ExcelV3Result(stream.ToArray(), fileName);
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')).TrimEnd('_');
}
