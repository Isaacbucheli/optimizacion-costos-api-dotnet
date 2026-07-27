using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.Cdc.Api;

/// <summary>
/// Gestión CDC: revisión de accesos (RBAC + Entra ID) por cliente. Sync corre como job en
/// background (202 + polling), calcado de PowerHistory/AnalysisRefreshController. Solo lectura
/// salvo el POST de sync; reutiliza credenciales del cliente.
/// </summary>
[ApiController]
[Authorize]
[Route("cdc")]
[RequireModule(Modules.AccessReview)]
public sealed class AccessReviewController(
    IAccessReviewStore store,
    IAccessReviewJobQueue queue,
    IAccessReviewExcelExporter excel,
    IAnalysisAccess access,
    ISqlConnectionFactory factory) : ControllerBase
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Encola una corrida de revisión de accesos (202). Guard: no encola doble si ya corre.</summary>
    [HttpPost("clients/{clientId:int}/access-review/sync")]
    [RequireModule(Modules.AccessReview, ModuleAccess.Edit)]
    public async Task<IActionResult> Sync(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        if (await store.IsRunActiveAsync(clientId, ct))
            return StatusCode(StatusCodes.Status202Accepted, new { status = "running", message = "Ya hay una revisión en proceso" });

        var actor = User.FindFirst("sub")?.Value;
        var runId = await store.CreateRunAsync(clientId, actor, ct);
        queue.Enqueue(new AccessReviewJob(runId, clientId));
        return StatusCode(StatusCodes.Status202Accepted, new { run_id = runId, status = "queued" });
    }

    [HttpGet("clients/{clientId:int}/access-review")]
    public async Task<IActionResult> Latest(int clientId, [FromQuery(Name = "inactivity_days")] int inactivityDays = 90, CancellationToken ct = default)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (inactivityDays is < 1 or > 3650) inactivityDays = 90;

        var run = await store.GetLatestRunAsync(clientId, ct);
        if (run is null) return Ok(new { status = "none" });
        if (run.Status is "queued" or "running")
            return Ok(new { status = run.Status, run_id = run.RunId, started_at = run.StartedAt });

        var snapshot = await store.GetSnapshotAsync(run.RunId, ct);
        if (snapshot is null) return Ok(new { status = "none" });
        var now = DateTimeOffset.UtcNow;
        var accounts = AccessReviewAccountBuilder.Build(snapshot);
        var kpis = AccessReviewKpiCalculator.Compute(snapshot, accounts, inactivityDays, now);
        var findings = AccessReviewFindingsBuilder.Build(snapshot, accounts, kpis, inactivityDays, now);
        return Ok(ToResponse(snapshot, accounts, kpis, findings, inactivityDays));
    }

    [HttpGet("clients/{clientId:int}/access-review/runs")]
    public async Task<IActionResult> Runs(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var runs = await store.ListRunsAsync(clientId, 20, ct);
        return Ok(runs.Select(r => new
        {
            run_id = r.RunId,
            status = r.Status,
            started_at = r.StartedAt,
            finished_at = r.FinishedAt,
            error = r.Error,
            requested_by = r.RequestedBy,
        }));
    }

    [HttpGet("clients/{clientId:int}/access-review/export")]
    public async Task<IActionResult> Export(
        int clientId,
        [FromQuery(Name = "run_id")] int? runId,
        [FromQuery(Name = "inactivity_days")] int inactivityDays = 90,
        CancellationToken ct = default)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (inactivityDays is < 1 or > 3650) inactivityDays = 90;

        var run = runId is int rid
            ? (await store.ListRunsAsync(clientId, 100, ct)).FirstOrDefault(r => r.RunId == rid)
            : await store.GetLatestRunAsync(clientId, ct);
        if (run is null || run.Status is "queued" or "running")
            return NotFound(new { detail = "No hay una corrida finalizada para exportar." });

        var snapshot = await store.GetSnapshotAsync(run.RunId, ct);
        if (snapshot is null) return NotFound(new { detail = "Corrida no encontrada." });

        var clientName = await ClientNameAsync(clientId, ct) ?? $"cliente-{clientId}";
        var now = DateTimeOffset.UtcNow;
        var accounts = AccessReviewAccountBuilder.Build(snapshot);
        var kpis = AccessReviewKpiCalculator.Compute(snapshot, accounts, inactivityDays, now);
        var findings = AccessReviewFindingsBuilder.Build(snapshot, accounts, kpis, inactivityDays, now);
        var result = excel.Generate(clientName, snapshot, accounts, kpis, findings, inactivityDays);
        return File(result.Bytes, XlsxContentType, result.FileName);
    }

    private async Task<string?> ClientNameAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_name FROM dbo.clients WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static object ToResponse(AccessReviewSnapshot s, IReadOnlyList<AccessAccountRow> accounts,
        AccessReviewKpis k, IReadOnlyList<AccessFinding> findings, int inactivityDays)
    {
        var graphComplete = AccessReviewAccountBuilder.GraphComplete(s);
        return new
    {
        status = s.Run.Status,
        run_id = s.Run.RunId,
        started_at = s.Run.StartedAt,
        finished_at = s.Run.FinishedAt,
        inactivity_days = inactivityDays,
        // Lo decide el backend (misma regla que usa el AccountBuilder) para que el front no tenga
        // que rederivar cuándo un indicador de Entra ID está medido y cuándo no.
        graph_complete = graphComplete,
        kpis = new
        {
            total_asignaciones = k.TotalAsignaciones,
            global_admins = k.GlobalAdmins,
            global_admins_sin_mfa = k.GlobalAdminsSinMfa,
            internos_sin_mfa = k.InternosSinMfaConRbac,
            cuentas_deshabilitadas = k.CuentasDeshabilitadasConRbac,
            cuentas_inactivas = k.CuentasInactivasConRbac,
            guests_total = k.GuestsTotal,
            guests_inactivos = k.GuestsInactivos,
            guests_inactivos_con_permisos = k.GuestsInactivosConPermisos,
            service_principals = k.ServicePrincipalsUnicos,
            cuentas_unicas = k.CuentasUnicas,
            asignaciones_elevadas = k.AsignacionesElevadas,
            pct_elevadas = k.PctElevadas,
            owners = k.Owners,
            cuentas_externas = k.CuentasExternasConRbac,
            owners_externos = k.OwnersExternos,
            roles_personalizados = k.RolesPersonalizados,
        },
        findings = findings.Select(f => new
        {
            key = f.Key,
            severity = f.Severity,
            title = f.Title,
            detail = f.Detail,
            recommendation = f.Recommendation,
            evaluable = f.Evaluable,
            not_evaluable_reason = f.NotEvaluableReason,
            affected_accounts = f.AffectedAccounts,
            affected_assignments = f.AffectedAssignments,
            affected_principals = f.AffectedPrincipals,
        }),
        accounts = accounts.Select(a => new
        {
            principal_object_id = a.PrincipalObjectId,
            principal_type = a.PrincipalType,
            display_name = a.DisplayName,
            login = a.Login,
            user_type = a.UserType,
            is_external = a.IsExternal,
            total_assignments = a.TotalAssignments,
            owner = a.Owner,
            otorga_accesos = a.OtorgaAccesos,
            escritura_total = a.EscrituraTotal,
            escritura_servicio = a.EscrituraServicio,
            lectura = a.Lectura,
            sin_clasificar = a.SinClasificar,
            subscriptions = a.Subscriptions,
            broadest_scope_level = a.BroadestScopeLevel,
            via = a.Via,
            account_enabled = a.AccountEnabled,
            last_sign_in = a.LastSignIn,
            mfa_status = a.MfaStatus,
            orphan = a.Orphan,
        }),
        credentials = s.Credentials.Select(c => new
        {
            credential_id = c.CredentialId,
            credential_name = c.CredentialName,
            arm_status = c.ArmStatus,
            graph_status = c.GraphStatus,
            detail = c.Detail,
        }),
        assignments = s.Assignments.Select(a => new
        {
            subscription_id = a.SubscriptionId,
            subscription_name = a.SubscriptionName,
            scope = a.Scope,
            scope_level = a.ScopeLevel,
            role_name = a.RoleName,
            role_definition_id = a.RoleDefinitionId,
            role_class = a.RoleClass,
            is_custom_role = a.IsCustomRole,
            is_elevated = AccessReviewRoleClassifier.IsElevated(a.RoleClass),
            is_external = AccessReviewAccountBuilder.External(a.PrincipalType, a.UserType, a.Login, graphComplete),
            principal_object_id = a.PrincipalObjectId,
            principal_type = a.PrincipalType,
            display_name = a.DisplayName,
            login = a.Login,
            user_type = a.UserType,
            via_group_id = a.ViaGroupId,
            via_group_name = a.ViaGroupName,
            account_enabled = a.AccountEnabled,
            last_sign_in = a.LastSignIn,
            mfa_status = a.MfaStatus,
        }),
        guests = s.Guests.Select(g => new
        {
            object_id = g.ObjectId,
            display_name = g.DisplayName,
            email = g.Email,
            external_domain = g.ExternalDomain,
            account_enabled = g.AccountEnabled,
            external_state = g.ExternalState,
            created_at_azure = g.CreatedAtAzure,
            last_sign_in = g.LastSignIn,
            roles_in_subs = g.RolesInSubs,
            mfa_status = g.MfaStatus,
        }),
        global_admins = s.GlobalAdmins.Select(g => new
        {
            object_id = g.ObjectId,
            display_name = g.DisplayName,
            upn = g.Upn,
            user_type = g.UserType,
            account_enabled = g.AccountEnabled,
            last_sign_in = g.LastSignIn,
            mfa_status = g.MfaStatus,
        }),
        };
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
