using ClosedXML.Excel;
using OptimizacionCostos.Api.Features.Reports.ExcelV3;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public interface IAccessReviewExcelExporter
{
    ExcelV3Result Generate(string clientName, AccessReviewSnapshot snapshot,
        IReadOnlyList<AccessAccountRow> accounts, AccessReviewKpis kpis,
        IReadOnlyList<AccessFinding> findings, IReadOnlyList<AccessDecision> decisions,
        int inactivityDays);
}

/// <summary>XLSX de revisión de accesos: Resumen + Hallazgos + 5 datasets. Puro (sin BD).</summary>
public sealed class AccessReviewExcelExporter : IAccessReviewExcelExporter
{
    public ExcelV3Result Generate(string clientName, AccessReviewSnapshot snapshot,
        IReadOnlyList<AccessAccountRow> accounts, AccessReviewKpis kpis,
        IReadOnlyList<AccessFinding> findings, IReadOnlyList<AccessDecision> decisions,
        int inactivityDays)
    {
        using var wb = new XLWorkbook();

        // ── Resumen ───────────────────────────────────────────────
        var ws = wb.AddWorksheet("Resumen");
        ws.Cell(1, 1).Value = $"Revisión de accesos — {clientName}";
        ws.Cell(1, 1).Style.Font.SetBold().Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"Corrida #{snapshot.Run.RunId} · {snapshot.Run.FinishedAt:yyyy-MM-dd HH:mm} UTC · umbral inactividad {inactivityDays} días";
        var kpiRows = new (string Label, object Value)[]
        {
            ("Total de asignaciones RBAC", kpis.TotalAsignaciones),
            ("Cuentas únicas con RBAC", kpis.CuentasUnicas),
            ("Asignaciones con privilegio elevado", kpis.AsignacionesElevadas),
            ("% de asignaciones elevadas", kpis.PctElevadas),
            ("Asignaciones Owner", kpis.Owners),
            ("Cuentas externas con RBAC", kpis.CuentasExternasConRbac),
            ("Cuentas externas con Owner", kpis.OwnersExternos),
            ("Definiciones de rol personalizadas en uso", kpis.RolesPersonalizados),
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
        const int kpiFirstRow = 4;
        for (var i = 0; i < kpiRows.Length; i++)
        {
            ws.Cell(kpiFirstRow + i, 1).Value = kpiRows[i].Label;
            ws.Cell(kpiFirstRow + i, 2).Value = XLCellValue.FromObject(kpiRows[i].Value);
        }
        // Ancla derivada del número de KPIs: fijarla a mano hacía que al agregar indicadores el
        // bloque de credenciales se pisara con las últimas filas.
        var credRow = kpiFirstRow + kpiRows.Length + 1;
        ws.Cell(credRow, 1).Value = "Estado por credencial:";
        ws.Cell(credRow, 1).Style.Font.SetBold();
        for (var i = 0; i < snapshot.Credentials.Count; i++)
        {
            var c = snapshot.Credentials[i];
            ws.Cell(credRow + 1 + i, 1).Value = c.CredentialName ?? $"credencial {c.CredentialId}";
            ws.Cell(credRow + 1 + i, 2).Value = $"ARM: {c.ArmStatus} · Graph: {c.GraphStatus}" + (c.Detail is null ? "" : $" · {c.Detail}");
        }
        ws.Columns().AdjustToContents(1, 60);

        static void Sheet<T>(XLWorkbook wb, string name, string[] headers, IReadOnlyList<T> rows,
            Func<T, object?[]> cells)
        {
            var s = wb.AddWorksheet(name);
            for (var c = 0; c < headers.Length; c++)
                s.Cell(1, c + 1).Value = headers[c];
            ExcelStyles.HeaderRow(s, 1, headers.Length);
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
        static string Clase(string? c) => c switch
        {
            "owner" => "Owner (otorga accesos)", "otorga_accesos" => "Otorga accesos",
            "escritura_total" => "Escritura total", "escritura_servicio" => "Escritura de servicio",
            "lectura" => "Lectura", _ => "Sin clasificar",
        };
        // Cadena vacía cuando el eje no se midió: el Excel no debe afirmar "Interna" sin dato.
        static string Externa(bool? e) => e switch { true => "Externa", false => "Interna", null => "" };
        static string SiNo(bool? v) => v switch { true => "Sí", false => "No", null => "" };

        static string Sev(string s) => s switch
        {
            AccessFindingSeverity.Critica => "Crítica", AccessFindingSeverity.Alta => "Alta",
            AccessFindingSeverity.Media => "Media", _ => "Informativa",
        };

        // Los hallazgos van primero entre los datasets: es la cola de trabajo, no un anexo.
        Sheet(wb, "Hallazgos",
            ["Severidad", "Hallazgo", "Detalle", "Recomendación", "Cuentas", "Asignaciones", "Evaluado"],
            findings,
            f => [Sev(f.Severity), f.Title, f.Detail, f.Recommendation,
                  f.Evaluable ? f.AffectedAccounts : "", f.Evaluable ? f.AffectedAssignments : "",
                  f.Evaluable ? "Sí" : "No"]);

        Sheet(wb, "Cuentas",
            ["Cuenta", "Tipo", "Interna / Externa", "Total asignaciones", "Owner", "Otorga accesos",
             "Escritura total", "Escritura de servicio", "Lectura", "Sin clasificar", "Suscripciones",
             "Scope más amplio", "Vía", "Cuenta activa", "Último login", "MFA", "Eliminada de Entra ID"],
            accounts,
            a => [a.DisplayName ?? a.Login ?? a.PrincipalObjectId, a.PrincipalType, Externa(a.IsExternal),
                  a.TotalAssignments, a.Owner, a.OtorgaAccesos, a.EscrituraTotal, a.EscrituraServicio,
                  a.Lectura, a.SinClasificar, a.Subscriptions, a.BroadestScopeLevel, a.Via,
                  SiNo(a.AccountEnabled), Fecha(a.LastSignIn), Mfa(a.MfaStatus), a.Orphan ? "Sí" : ""]);

        Sheet(wb, "Asignaciones RBAC",
            ["Suscripción", "Scope", "Nivel", "Rol", "Clase de rol", "Rol personalizado", "Tipo",
             "Nombre", "Correo / Login", "Tipo usuario", "Vía grupo", "Cuenta activa", "Último login", "MFA"],
            snapshot.Assignments,
            a => [a.SubscriptionName ?? a.SubscriptionId, a.Scope, a.ScopeLevel, a.RoleName,
                  Clase(a.RoleClass), a.IsCustomRole ? "Sí" : "", a.PrincipalType,
                  a.DisplayName ?? a.PrincipalObjectId, a.Login ?? "", a.UserType ?? "",
                  a.ViaGroupName ?? "", SiNo(a.AccountEnabled),
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

        static string Dec(string d) => d switch
        {
            "mantener" => "Mantener", "revocar" => "Revocar",
            "justificado" => "Justificado", _ => d,
        };

        Sheet(wb, "Decisiones",
            ["Decision", "Cuenta / hallazgo", "Rol", "Scope", "Nota", "Decidido por", "Fecha",
             "Corridas desde entonces"],
            decisions,
            d => [Dec(d.Decision), d.FindingKey ?? d.PrincipalObjectId, d.RoleKey, d.Scope,
                  d.Note ?? "", d.DecidedBy ?? "", Fecha(d.DecidedAt), d.RunsSince]);

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
