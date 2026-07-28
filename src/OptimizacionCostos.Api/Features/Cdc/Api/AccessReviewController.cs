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
    IAccessReviewDecisionStore decisions,
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
        var decided = await decisions.GetForClientAsync(clientId, ct);
        var accounts = AccessReviewAccountBuilder.Build(snapshot, decided);
        var kpis = AccessReviewKpiCalculator.Compute(snapshot, accounts, inactivityDays, now, decided);
        // Delta contra la corrida anterior finalizada: es lo que permite mirar solo lo que cambio.
        var previousRun = await store.GetPreviousFinishedRunAsync(clientId, run.RunId, ct);
        var previous = previousRun is null ? null : await store.GetSnapshotAsync(previousRun.RunId, ct);
        var delta = AccessReviewDeltaBuilder.Build(snapshot, previous);

        var findings = AccessReviewFindingsBuilder.Build(snapshot, accounts, kpis, inactivityDays, now, decided, delta);
        return Ok(ToResponse(snapshot, accounts, kpis, findings, decided, delta, inactivityDays));
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
        var decided = await decisions.GetForClientAsync(clientId, ct);
        var accounts = AccessReviewAccountBuilder.Build(snapshot, decided);
        var kpis = AccessReviewKpiCalculator.Compute(snapshot, accounts, inactivityDays, now, decided);
        var previousRun = await store.GetPreviousFinishedRunAsync(clientId, run.RunId, ct);
        var previous = previousRun is null ? null : await store.GetSnapshotAsync(previousRun.RunId, ct);
        var delta = AccessReviewDeltaBuilder.Build(snapshot, previous);

        var findings = AccessReviewFindingsBuilder.Build(snapshot, accounts, kpis, inactivityDays, now, decided, delta);
        var result = excel.Generate(clientName, snapshot, accounts, kpis, findings, decided.Values.ToList(), delta, inactivityDays);
        return File(result.Bytes, XlsxContentType, result.FileName);
    }

    public sealed record DecisionItemDto(
        string? principal_object_id, string? role_definition_id, string? scope, string? decision, string? note);
    public sealed record DecisionsBody(List<DecisionItemDto>? items);
    public sealed record AcceptBody(string? note);

    /// <summary>Registra decisiones sobre accesos. Por lote: el consultor marca varias filas a la vez.
    /// Valida todo antes de escribir — un item invalido no deja el lote a medias.</summary>
    [HttpPost("clients/{clientId:int}/access-review/decisions")]
    [RequireModule(Modules.AccessReview, ModuleAccess.Edit)]
    public async Task<IActionResult> SaveDecisions(int clientId, [FromBody] DecisionsBody body, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var items = body?.items ?? [];
        if (items.Count == 0) return BadRequest(new { detail = "No se recibio ninguna decision." });

        var inputs = new List<AccessDecisionInput>(items.Count);
        foreach (var i in items)
        {
            if (string.IsNullOrWhiteSpace(i.principal_object_id) || string.IsNullOrWhiteSpace(i.scope))
                return BadRequest(new { detail = "Cada decision requiere principal_object_id y scope." });
            if (!AccessDecisionValues.IsValid(i.decision))
                return BadRequest(new { detail = $"Decision invalida: '{i.decision}'. Valores: mantener, revocar, justificado." });
            // Justificar es la unica que baja el conteo de un hallazgo: exige razon escrita.
            if (i.decision == AccessDecisionValues.Justificado && string.IsNullOrWhiteSpace(i.note))
                return BadRequest(new { detail = "Justificar un acceso requiere una nota." });

            inputs.Add(new AccessDecisionInput(
                i.principal_object_id!, i.role_definition_id ?? "", i.scope!, i.decision!, i.note));
        }

        var run = await store.GetLatestRunAsync(clientId, ct);
        var saved = await decisions.UpsertAsync(clientId, inputs, User.FindFirst("sub")?.Value, run?.RunId, ct);
        return Ok(new { saved });
    }

    /// <summary>Acepta un hallazgo de umbral (los que no tienen accesos individuales que marcar).</summary>
    [HttpPost("clients/{clientId:int}/access-review/findings/{findingKey}/accept")]
    [RequireModule(Modules.AccessReview, ModuleAccess.Edit)]
    public async Task<IActionResult> AcceptFinding(int clientId, string findingKey,
        [FromBody] AcceptBody body, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        if (string.IsNullOrWhiteSpace(findingKey)) return BadRequest(new { detail = "Falta la clave del hallazgo." });
        if (string.IsNullOrWhiteSpace(body?.note))
            return BadRequest(new { detail = "Aceptar un hallazgo requiere una nota que lo justifique." });

        var run = await store.GetLatestRunAsync(clientId, ct);
        await decisions.AcceptFindingAsync(clientId, findingKey, body!.note!,
            User.FindFirst("sub")?.Value, run?.RunId, ct);
        return Ok(new { saved = 1 });
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
        AccessReviewKpis k, IReadOnlyList<AccessFinding> findings,
        IReadOnlyDictionary<string, AccessDecision> decided, AccessReviewDelta delta, int inactivityDays)
    {
        var nuevos = (delta.NuevosAccesos ?? []).Select(i => i.AccessKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var graphComplete = AccessReviewAccountBuilder.GraphComplete(s);

        AccessDecision? DecisionFor(AccessAssignmentRow a) =>
            decided.TryGetValue(
                AccessReviewAccessKey.For(a.PrincipalObjectId, a.RoleDefinitionId, a.Scope), out var d) ? d : null;
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
        delta = new
        {
            has_previous = delta.HasPrevious,
            previous_run_id = delta.PreviousRunId,
            previous_finished_at = delta.PreviousFinishedAt,
            // null en un eje = no comparable (una de las dos corridas leyó su insumo a medias), que no
            // es lo mismo que cero cambios. El front lo muestra como "n/d" en vez de afirmar altas y
            // bajas que nadie hizo.
            accesos_comparables = delta.AccesosComparables,
            directorio_comparable = delta.DirectorioComparable,
            nuevos_accesos = delta.NuevosAccesos?.Count,
            accesos_removidos = delta.AccesosRemovidos?.Count,
            nuevos_global_admins = delta.NuevosGlobalAdmins,
            global_admins_removidos = delta.GlobalAdminsRemovidos,
            nuevos_guests = delta.NuevosGuests,
            guests_removidos = delta.GuestsRemovidos,
            // Los principals de lo nuevo, para que el front pueda filtrar por "solo nuevos".
            nuevos_principals = (delta.NuevosAccesos ?? []).Select(i => i.PrincipalObjectId).Distinct(),
        },
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
            pendientes_de_revisar = k.PendientesDeRevisar,
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
            accepted = f.Accepted,
            accepted_note = f.AcceptedNote,
            accepted_by = f.AcceptedBy,
            accepted_at = f.AcceptedAt,
            coverage_pct = f.CoveragePct,
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
            decision_pendientes = a.Decisions?.Pendientes ?? 0,
            decision_mantener = a.Decisions?.Mantener ?? 0,
            decision_revocar = a.Decisions?.Revocar ?? 0,
            decision_justificado = a.Decisions?.Justificado ?? 0,
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
            decision = DecisionFor(a)?.Decision,
            decision_note = DecisionFor(a)?.Note,
            decision_decided_by = DecisionFor(a)?.DecidedBy,
            decision_decided_at = DecisionFor(a)?.DecidedAt,
            decision_runs_since = DecisionFor(a)?.RunsSince,
            environment = AccessReviewEnvironment.Classify(a.SubscriptionName),
            is_new = nuevos.Contains(AccessReviewAccessKey.For(a.PrincipalObjectId, a.RoleDefinitionId, a.Scope)),
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
