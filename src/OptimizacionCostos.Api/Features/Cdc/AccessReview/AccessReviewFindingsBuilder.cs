using System.Globalization;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>
/// Convierte una corrida en una cola de trabajo: qué está mal, con cifra viva y qué hacer. Puro
/// (sin BD ni red): lo consumen el response y el Excel. Las cifras se interpolan en tiempo de build,
/// nunca van fijas en el texto — un detalle con números hardcodeados miente en la segunda corrida.
/// </summary>
public static class AccessReviewFindingsBuilder
{
    private const string SinGraph =
        "No evaluable: esta corrida no leyó el directorio de Entra ID (revisar el estado por credencial).";
    private const string SinP1 =
        "No evaluable: el último inicio de sesión requiere licencia Entra ID P1/P2 en el tenant del cliente.";
    // Comparar contra un insumo parcial no da "cero cambios": da altas y bajas que nadie hizo. Es
    // mejor decir que no se puede medir que reportar un alta de privilegio inexistente.
    private const string SinInventarioComparable =
        "No evaluable: alguna de las dos corridas comparadas no leyó el inventario completo de asignaciones.";
    private const string SinDirectorioComparable =
        "No evaluable: alguna de las dos corridas comparadas no leyó el directorio completo de Entra ID.";

    /// <param name="decisions">Decisiones del cliente por access_key. SOLO `justificado` descuenta:
    /// `revocar` es una promesa (mientras el acceso exista sigue siendo riesgo) y `mantener` es una
    /// revision (no vuelve seguro lo que no lo es). Es decision de producto, esta en el spec.</param>
    public static IReadOnlyList<AccessFinding> Build(
        AccessReviewSnapshot snapshot, IReadOnlyList<AccessAccountRow> accounts,
        AccessReviewKpis kpis, int inactivityDays, DateTimeOffset now,
        IReadOnlyDictionary<string, AccessDecision>? decisions = null,
        AccessReviewDelta? delta = null)
    {
        decisions ??= new Dictionary<string, AccessDecision>();
        delta ??= AccessReviewDelta.Empty;

        string Key(AccessAssignmentRow a) =>
            AccessReviewAccessKey.For(a.PrincipalObjectId, a.RoleDefinitionId, a.Scope);

        bool Justificado(AccessAssignmentRow a) =>
            decisions.TryGetValue(Key(a), out var d) && d.Decision == AccessDecisionValues.Justificado;

        // Una cuenta sale de un hallazgo solo cuando TODOS sus accesos estan justificados: con uno
        // pendiente sigue habiendo algo que revisar.
        var cuentasTotalmenteJustificadas = snapshot.Assignments
            .GroupBy(a => a.PrincipalObjectId)
            .Where(g => g.Select(Key).Distinct(StringComparer.OrdinalIgnoreCase)
                         .All(k => decisions.TryGetValue(k, out var d)
                                   && d.Decision == AccessDecisionValues.Justificado))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var graphOk = AccessReviewAccountBuilder.GraphComplete(snapshot);
        // El último login necesita además signInActivity: sin P1, un null es ambiguo y no permite
        // afirmar ni "nunca entró" ni "hace N días que no entra".
        var signInOk = graphOk && snapshot.Credentials.All(c => c.GraphStatus != "sin_licencia_p1");

        var assignments = snapshot.Assignments;

        // Las reglas de práctica miden lo que un administrador DECIDIÓ, así que su universo son las
        // asignaciones otorgadas (`via_group_id` nulo), no las filas derivadas de expandir grupos.
        // Un grupo con un rol sobre un recurso y 30 miembros produce 31 filas: contarlas todas
        // convierte "granularidad" en "tamaño de los grupos" (en el E2E: 68,3% reportado contra 29,2%
        // real). Los service principals y los grupos de otro tenant quedan fuera del universo de
        // "asignación por persona": no son gente que se administre con membresías.
        // Las reglas de riesgo ignoran lo justificado; las de practica NO, porque miden como se
        // administra el tenant y eso no cambia porque alguien acepte un caso puntual.
        var enRiesgo = assignments.Where(a => !Justificado(a)).ToList();
        var cuentasEnRiesgo = accounts
            .Where(a => !cuentasTotalmenteJustificadas.Contains(a.PrincipalObjectId)).ToList();

        var grants = assignments.Where(a => a.ViaGroupId is null).ToList();
        var agrupables = grants.Where(a => a.PrincipalType is "User" or "Group").ToList();

        // Desglose por tipo de los huérfanos: la acción difiere según qué se borró. Un service
        // principal eliminado es limpieza; un grupo eliminado significa que su gente pudo perder
        // acceso y hay que revisar si el reemplazo quedó bien. En el E2E: MINSUR 179 SP de 180,
        // BANCO DELTA 9 grupos de 13.
        var huerfanasPorTipo = string.Join(", ", cuentasEnRiesgo.Where(a => a.Orphan)
            .GroupBy(a => a.PrincipalType)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {PrincipalLabel(g.Key, g.Count())}"));

        List<AccessFinding> findings =
        [
            // ── Solo ARM: se evalúan siempre, incluso en corridas Lighthouse ──
            ByAssignment("owner_en_raiz", AccessFindingSeverity.Critica,
                "Privilegio de otorgamiento heredado desde la raíz",
                enRiesgo.Where(a => a.RoleClass is AccessReviewRoleClassifier.Owner
                                        or AccessReviewRoleClassifier.OtorgaAccesos
                                    && a.ScopeLevel is "root" or "management_group"),
                n => $"{n.Assignments} asignaciones que pueden otorgar accesos, a nivel root o management group, sobre {n.Accounts} cuentas. Alcanzan todas las suscripciones por herencia.",
                "Bajar el privilegio al scope mínimo necesario (suscripción o grupo de recursos). Reservar root y management group para 2-3 cuentas break-glass documentadas, y activarlas vía PIM con aprobación.",
                true, null),

            ByAssignment("grupo_foraneo_elevado", AccessFindingSeverity.Alta,
                "Grupo de otro tenant con privilegio elevado",
                enRiesgo.Where(a => a.PrincipalType == "ForeignGroup"
                                    && AccessReviewRoleClassifier.IsElevated(a.RoleClass)),
                n => $"{n.Accounts} grupos administrados desde otro tenant tienen privilegio elevado ({n.Assignments} asignaciones). Su membresía no es visible ni auditable desde el tenant del cliente.",
                "Identificar el tenant de origen y el propósito (suele ser administración delegada del MSP). Documentarlo formalmente, validar que su membresía esté controlada en el tenant origen, y bajar el rol a lo mínimo necesario.",
                true, null),

            ByAssignment("sp_con_otorgamiento", AccessFindingSeverity.Alta,
                "Service principal que puede otorgar accesos",
                enRiesgo.Where(a => a.PrincipalType == "ServicePrincipal"
                                    && a.RoleClass is AccessReviewRoleClassifier.Owner
                                        or AccessReviewRoleClassifier.OtorgaAccesos),
                n => $"{n.Accounts} service principals pueden crear asignaciones de rol, lo que equivale a escalar privilegios a voluntad.",
                "Inventariar dueño, propósito y último uso de cada uno. Migrar a Managed Identity donde aplique y rotar credenciales. Un SP con capacidad de otorgar accesos requiere justificación documentada o degradación inmediata.",
                true, null),

            ByAssignment("rol_propio_elevado", AccessFindingSeverity.Media,
                "Roles personalizados con privilegio elevado",
                enRiesgo.Where(a => a.IsCustomRole && AccessReviewRoleClassifier.IsElevated(a.RoleClass)),
                n => $"{n.Assignments} asignaciones usan roles personalizados cuyos permisos equivalen a escritura amplia o a otorgar accesos.",
                "Revisar la definición de cada rol personalizado: los permisos con comodín (*) y los de Microsoft.Authorization suelen estar de más. Documentar quién lo creó y por qué.",
                true, null),

            Share("asignacion_directa", AccessFindingSeverity.Media,
                "Accesos asignados por persona en vez de por grupo",
                agrupables.Count(a => a.PrincipalType == "User"), agrupables.Count,
                AccessFindingThresholds.DirectAssignmentShare,
                "los accesos otorgados a personas o grupos",
                pct => $"{pct} de los accesos otorgados a personas o grupos apuntan directo a una persona. Administrar acceso uno por uno no escala y deja permisos huérfanos cuando alguien sale.",
                "Migrar a grupos de Entra ID por función y ambiente, y asignar RBAC al grupo. El alta y la baja de una persona pasan a ser un cambio de membresía."),

            Share("granularidad_recurso", AccessFindingSeverity.Media,
                "Asignaciones a nivel de recurso individual",
                grants.Count(a => a.ScopeLevel == "resource"), grants.Count,
                AccessFindingThresholds.ResourceScopeShare,
                "los accesos otorgados",
                pct => $"{pct} de los accesos otorgados apuntan a un recurso puntual. A esa granularidad el acceso deja de ser gobernable: nadie puede responder quién tiene qué sin una herramienta.",
                "Consolidar en grupos de recursos o suscripciones según la función. Las excepciones a nivel de recurso deberían ser contadas y justificadas."),

            // ── Requieren directorio (Graph) ──
            ByAccount("externa_elevada", AccessFindingSeverity.Critica,
                "Cuenta externa con privilegio elevado",
                cuentasEnRiesgo.Where(a => a.IsExternal == true && Elevated(a)),
                n => $"{n.Accounts} cuentas externas (invitadas o de otro tenant) tienen privilegio elevado. Su MFA, su ciclo de vida y su revocación los controla el tenant de origen, no el cliente.",
                "Formalizar con el proveedor el listado de cuentas activas y su vigencia. Bajar el privilegio a lo mínimo necesario, exigir MFA por Acceso Condicional y programar revisiones trimestrales de invitados.",
                graphOk, SinGraph),

            ByAccount("principal_eliminado", AccessFindingSeverity.Critica,
                "Asignaciones a principals que ya no existen",
                cuentasEnRiesgo.Where(a => a.Orphan),
                n => $"{n.Accounts} principals con asignaciones RBAC ya no existen en Entra ID (en el portal aparecen como 'Identity not found'): {huerfanasPorTipo}. Son {n.Assignments} accesos residuales que nadie va a reclamar.",
                "Eliminar esas asignaciones. Si son service principals, suele ser rastro de app registrations borradas por pipeline: revisar el proceso que las crea. Si hay grupos, revisar además qué accesos perdió la gente que los integraba y si el reemplazo quedó bien.",
                graphOk, SinGraph),

            ByAccount("deshabilitada_con_rbac", AccessFindingSeverity.Alta,
                "Cuentas deshabilitadas que conservan RBAC",
                cuentasEnRiesgo.Where(a => a.AccountEnabled == false),
                n => $"{n.Accounts} cuentas deshabilitadas siguen teniendo permisos asignados. Si se reactivan, recuperan el acceso sin pasar por ninguna aprobación.",
                "Eliminar las asignaciones como parte del proceso de baja. Deshabilitar la cuenta no revoca RBAC.",
                graphOk, SinGraph),

            ByAccount("elevada_sin_mfa", AccessFindingSeverity.Alta,
                "Privilegio elevado sin MFA registrado",
                cuentasEnRiesgo.Where(a => a.MfaStatus == "disabled" && Elevated(a)),
                n => $"{n.Accounts} cuentas con privilegio elevado no tienen ningún método de MFA registrado.",
                "Exigir MFA por Acceso Condicional para cualquier rol con escritura, y bloquear autenticación legacy. Ojo: esto mide métodos registrados, no si una política los exige.",
                graphOk, SinGraph),

            Count("exceso_global_admins", AccessFindingSeverity.Alta,
                "Más Global Admins que los recomendados",
                kpis.GlobalAdmins > AccessFindingThresholds.MaxGlobalAdmins ? kpis.GlobalAdmins : 0,
                $"El tenant tiene {kpis.GlobalAdmins} Global Administrators permanentes; la recomendación de Microsoft es no pasar de {AccessFindingThresholds.MaxGlobalAdmins}.",
                "Reducir a 2-3 cuentas break-glass sin licencia y con credenciales en custodia. El resto debería usar roles específicos y activarlos vía PIM con aprobación.",
                graphOk, SinGraph),

            // ── Requieren directorio + licencia P1 (último inicio de sesión) ──
            ByAccount("nunca_inicio_sesion", AccessFindingSeverity.Media,
                "Cuentas con permisos que nunca iniciaron sesión",
                cuentasEnRiesgo.Where(a => a.PrincipalType == "User" && a.AccountEnabled != false && a.LastSignIn is null),
                n => $"{n.Accounts} cuentas de usuario tienen RBAC y no registran ningún inicio de sesión: permisos otorgados y jamás usados.",
                "Confirmar con el responsable si la cuenta sigue haciendo falta y eliminar las asignaciones si no. Es el patrón típico del alta que se hizo 'por si acaso'.",
                signInOk, graphOk ? SinP1 : SinGraph),

            ByAccount("inactiva_con_rbac", AccessFindingSeverity.Media,
                $"Cuentas sin actividad por más de {inactivityDays} días",
                cuentasEnRiesgo.Where(a => a.PrincipalType == "User" && Inactive(a.LastSignIn, inactivityDays, now)),
                n => $"{n.Accounts} cuentas con RBAC no registran actividad en más de {inactivityDays} días.",
                "Validar con el área correspondiente y revocar. Ajustá el umbral de inactividad arriba si para este cliente 90 días no es el criterio.",
                signInOk, graphOk ? SinP1 : SinGraph),

            Count("guest_inactivo_con_permisos", AccessFindingSeverity.Media,
                "Invitados inactivos que conservan permisos",
                kpis.GuestsInactivosConPermisos,
                $"{kpis.GuestsInactivosConPermisos} cuentas invitadas sin actividad en más de {inactivityDays} días siguen con permisos en suscripciones.",
                "Revocar los permisos y quitar la invitación. Establecer access reviews periódicos para invitados, que es donde más se acumula acceso olvidado.",
                signInOk, graphOk ? SinP1 : SinGraph),

            // Cierre del ciclo: lo que se prometio revocar y sigue vivo.
            ByAssignment("revocacion_incumplida", AccessFindingSeverity.Alta,
                "Accesos marcados para revocar que siguen activos",
                assignments.Where(a => decisions.TryGetValue(Key(a), out var d)
                                    && d.Decision == AccessDecisionValues.Revocar
                                    && d.RunsSince > 0),
                n => $"{n.Accounts} cuentas conservan {n.Assignments} accesos que ya se habían marcado para revocar en una revisión anterior (hasta {MaxRunsSince(assignments, decisions)} corridas atrás). La decisión está tomada y no se ejecutó.",
                "Escalar con el responsable de cada acceso: o se revoca, o se documenta por qué se mantiene y se cambia la decisión a justificado. Un pendiente que se arrastra entre revisiones es peor que no haberlo detectado, porque ya hay constancia de que se sabía.",
                true, null),

            // ── Novedades respecto de la corrida anterior (bloque 4) ──
            ByKeys("nuevo_acceso_elevado", AccessFindingSeverity.Alta,
                "Accesos con privilegio elevado otorgados desde la revisión anterior",
                delta.NuevosAccesos?.Where(i => AccessReviewRoleClassifier.IsElevated(i.RoleClass)).ToList() ?? [],
                (cuentas, accesos) => $"Se otorgaron {accesos} accesos con privilegio elevado a {cuentas} cuentas desde la corrida anterior"
                    + (delta.PreviousFinishedAt is null ? "." : $" ({delta.PreviousFinishedAt:yyyy-MM-dd}).")
                    + " Entre las dos revisiones alguien concedió ese privilegio.",
                "Confirmar quién lo otorgó y con qué autorización. Si el alta fue legítima, justificarla acá para que no vuelva a aparecer como novedad; si no, revocarla.",
                delta.HasPrevious && delta.AccesosComparables,
                delta.HasPrevious ? SinInventarioComparable
                    : "No evaluable: es la primera corrida de este cliente, no hay con qué comparar."),

            Count("nuevo_global_admin", AccessFindingSeverity.Alta,
                "Global Admins nuevos desde la revisión anterior",
                delta.NuevosGlobalAdmins?.Count ?? 0,
                delta.NuevosGlobalAdmins is null or { Count: 0 }
                    ? "No hay Global Administrators nuevos respecto de la corrida anterior."
                    : $"Aparecieron {delta.NuevosGlobalAdmins.Count} Global Administrators nuevos: {string.Join(", ", delta.NuevosGlobalAdmins.Take(10))}.",
                "Es el privilegio más alto del tenant: validar la autorización del alta y si corresponde un rol más específico activado vía PIM.",
                delta.HasPrevious && graphOk && delta.DirectorioComparable,
                delta.HasPrevious
                    ? (graphOk ? SinDirectorioComparable : SinGraph)
                    : "No evaluable: es la primera corrida de este cliente, no hay con qué comparar."),

            Segregacion(assignments, decisions),

            Scope(snapshot, graphOk, signInOk),
        ];

        // Aceptacion explicita de las reglas de umbral (las que no tienen accesos que marcar).
        findings = [.. findings.Select(f =>
            decisions.TryGetValue(AccessReviewAccessKey.ForFinding(f.Key), out var acc)
                ? f with { Accepted = true, AcceptedNote = acc.Note, AcceptedBy = acc.DecidedBy, AcceptedAt = acc.DecidedAt }
                : f)];

        return [.. findings
            .OrderBy(f => AccessFindingSeverity.Rank(f.Severity))
            .ThenByDescending(f => f.AffectedAccounts)
            .ThenByDescending(f => f.AffectedAssignments)
            .ThenBy(f => f.Key, StringComparer.Ordinal)];
    }

    /// <summary>Etiqueta legible del tipo de principal, en plural cuando corresponde.</summary>
    private static string PrincipalLabel(string type, int count) => (type, count) switch
    {
        ("User", 1) => "usuario", ("User", _) => "usuarios",
        ("Group", 1) => "grupo", ("Group", _) => "grupos",
        ("ServicePrincipal", 1) => "service principal", ("ServicePrincipal", _) => "service principals",
        (_, 1) => type, _ => type,
    };

    /// <summary>Regla sobre items del delta, que ya vienen deduplicados por clave de acceso.</summary>
    private static AccessFinding ByKeys(
        string key, string severity, string title, IReadOnlyList<AccessDeltaItem> items,
        Func<int, int, string> detail, string recommendation, bool evaluable, string? reason)
    {
        if (!evaluable) return NotEvaluable(key, severity, title, recommendation, reason);

        var principals = items.Select(i => i.PrincipalObjectId).Distinct().Order().ToList();
        return new AccessFinding(key, severity, title,
            items.Count == 0
                ? "No hay accesos elevados nuevos respecto de la corrida anterior."
                : detail(principals.Count, items.Count),
            recommendation, true, null, principals.Count, items.Count, principals);
    }

    /// <summary>
    /// Mismo principal con el mismo rol elevado en produccion y en no-produccion. Solo se miran
    /// asignaciones de suscripcion o menores: un Owner en root alcanza todos los ambientes por
    /// definicion, pero eso ya lo reporta owner_en_raiz e incluirlo aca duplicaria el hallazgo.
    /// El ambiente se INFIERE del nombre de la suscripcion, y el texto lo dice.
    /// </summary>
    private static AccessFinding Segregacion(
        IReadOnlyList<AccessAssignmentRow> assignments, IReadOnlyDictionary<string, AccessDecision> decisions)
    {
        const string key = "sin_segregacion_ambientes";
        const string title = "Mismo privilegio en producción y en no producción";
        const string recommendation =
            "Separar los grupos administrativos por ambiente y asignar RBAC al grupo, no a la persona. "
            + "El acceso a producción debería requerir activación vía PIM con aprobación, no ser permanente.";

        var relevantes = assignments
            .Where(a => a.ScopeLevel is "subscription" or "resource_group" or "resource")
            .Where(a => AccessReviewRoleClassifier.IsElevated(a.RoleClass))
            .Select(a => new
            {
                a.PrincipalObjectId,
                Role = AccessReviewRoleClassifier.RoleKey(a.RoleDefinitionId),
                Env = AccessReviewEnvironment.Classify(a.SubscriptionName),
            })
            .Where(x => AccessReviewEnvironment.IsKnown(x.Env))
            .ToList();

        // Cobertura de la inferencia: sin esto el hallazgo afirma sobre una parte del tenant y suena
        // como si fuera sobre todo. En el E2E, un cliente tenia 1156 de 1451 asignaciones en una
        // suscripcion sin clasificar: reportar "14 cuentas cruzan ambientes" y callar que el 80% del
        // tenant no se evaluo es una media verdad.
        var subsTotales = assignments.Select(a => a.SubscriptionName ?? a.SubscriptionId).Distinct().Count();
        var subsClasificadas = assignments
            .Select(a => a.SubscriptionName ?? a.SubscriptionId).Distinct()
            .Count(n => AccessReviewEnvironment.IsKnown(AccessReviewEnvironment.Classify(n)));
        var asignacionesCubiertas = assignments
            .Count(a => AccessReviewEnvironment.IsKnown(AccessReviewEnvironment.Classify(a.SubscriptionName)));
        var pctCobertura = assignments.Count == 0 ? 0
            : (int)Math.Round(asignacionesCubiertas * 100d / assignments.Count);
        var cobertura = $"Se pudo inferir el ambiente de {subsClasificadas} de {subsTotales} suscripciones "
            + $"({pctCobertura}% de las asignaciones); el resto no se evaluó.";

        var ambientes = relevantes.Select(x => x.Env).Distinct().ToList();
        if (ambientes.Count < 2)
            return NotEvaluable(key, AccessFindingSeverity.Alta, title, recommendation,
                "No evaluable: no se pudieron identificar al menos dos ambientes distintos a partir de los nombres de las suscripciones.");

        var cruzados = relevantes
            .GroupBy(x => (x.PrincipalObjectId, x.Role))
            .Where(g => g.Any(x => AccessReviewEnvironment.IsProduccion(x.Env))
                     && g.Any(x => !AccessReviewEnvironment.IsProduccion(x.Env)))
            .ToList();

        var principals = cruzados.Select(g => g.Key.PrincipalObjectId).Distinct().Order().ToList();
        var detail = (principals.Count == 0
            ? "No se detectaron cuentas con el mismo rol elevado en producción y en no producción. "
            : $"{principals.Count} cuentas tienen el mismo rol elevado en suscripciones de producción y de no producción "
              + $"({cruzados.Count} combinaciones de cuenta y rol). Quien administra desarrollo tiene el mismo poder en producción. ")
            + cobertura;

        return new AccessFinding(key, AccessFindingSeverity.Alta, title, detail, recommendation,
            true, null, principals.Count, cruzados.Count, principals, CoveragePct: pctCobertura);
    }

    /// <summary>Cuantas corridas lleva sin cumplirse la revocacion mas vieja que sigue viva.</summary>
    private static int MaxRunsSince(
        IReadOnlyList<AccessAssignmentRow> assignments, IReadOnlyDictionary<string, AccessDecision> decisions)
    {
        var max = 0;
        foreach (var a in assignments)
        {
            var key = AccessReviewAccessKey.For(a.PrincipalObjectId, a.RoleDefinitionId, a.Scope);
            if (decisions.TryGetValue(key, out var d)
                && d.Decision == AccessDecisionValues.Revocar && d.RunsSince > max) max = d.RunsSince;
        }
        return max;
    }

    private static bool Elevated(AccessAccountRow a) =>
        a.Owner + a.OtorgaAccesos + a.EscrituraTotal > 0;

    private static bool Inactive(DateTimeOffset? lastSignIn, int days, DateTimeOffset now) =>
        lastSignIn is not null && (now - lastSignIn.Value).TotalDays > days;

    private sealed record Counts(int Accounts, int Assignments);

    /// <summary>Regla sobre asignaciones: cuenta filas y agrupa los principals involucrados.</summary>
    private static AccessFinding ByAssignment(
        string key, string severity, string title, IEnumerable<AccessAssignmentRow> hits,
        Func<Counts, string> detail, string recommendation, bool evaluable, string? reason)
    {
        if (!evaluable) return NotEvaluable(key, severity, title, recommendation, reason);

        var rows = hits.ToList();
        var principals = rows.Select(r => r.PrincipalObjectId).Distinct().Order().ToList();
        var counts = new Counts(principals.Count, rows.Count);
        return new AccessFinding(key, severity, title, detail(counts), recommendation,
            true, null, counts.Accounts, counts.Assignments, principals);
    }

    /// <summary>Regla sobre cuentas ya agregadas.</summary>
    private static AccessFinding ByAccount(
        string key, string severity, string title, IEnumerable<AccessAccountRow> hits,
        Func<Counts, string> detail, string recommendation, bool evaluable, string? reason)
    {
        if (!evaluable) return NotEvaluable(key, severity, title, recommendation, reason);

        var rows = hits.ToList();
        var principals = rows.Select(r => r.PrincipalObjectId).Distinct().Order().ToList();
        var counts = new Counts(principals.Count, rows.Sum(r => r.TotalAssignments));
        return new AccessFinding(key, severity, title, detail(counts), recommendation,
            true, null, counts.Accounts, counts.Assignments, principals);
    }

    /// <summary>Regla de práctica: un porcentaje sobre el total, sin culpables individuales.</summary>
    private static AccessFinding Share(
        string key, string severity, string title, int hits, int total, double threshold,
        string universe, Func<string, string> detail, string recommendation)
    {
        var share = total == 0 ? 0d : (double)hits / total;
        var fires = share > threshold;
        var pct = (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        return new AccessFinding(key, severity, title,
            fires ? detail(pct)
                  : $"{pct} de {universe} ({hits} de {total}), por debajo del umbral de {threshold * 100:0}%.",
            recommendation, true, null, 0, fires ? hits : 0, []);
    }

    /// <summary>Regla que ya viene contada (KPI): no tiene lista de principals que ofrecer.</summary>
    private static AccessFinding Count(
        string key, string severity, string title, int affected, string detail, string recommendation,
        bool evaluable, string? reason)
    {
        if (!evaluable) return NotEvaluable(key, severity, title, recommendation, reason);
        return new AccessFinding(key, severity, title, detail, recommendation,
            true, null, affected, 0, []);
    }

    private static AccessFinding NotEvaluable(
        string key, string severity, string title, string recommendation, string? reason) =>
        new(key, severity, title, reason ?? "No evaluable en esta corrida.", recommendation,
            false, reason, 0, 0, []);

    /// <summary>Qué no se pudo leer y por qué: el mismo dato que los chips de estado por credencial,
    /// pero con acción. Siempre evaluable — es sobre la corrida, no sobre el tenant.</summary>
    private static AccessFinding Scope(AccessReviewSnapshot snapshot, bool graphOk, bool signInOk)
    {
        var problemas = snapshot.Credentials
            .Where(c => c.ArmStatus != "ok" || c.GraphStatus != "ok")
            .Select(c => $"{c.CredentialName ?? $"credencial {c.CredentialId}"}: ARM {c.ArmStatus}, Graph {c.GraphStatus}")
            .ToList();

        var detail = problemas.Count == 0
            ? "La corrida leyó ARM y el directorio de Entra ID de todas las credenciales administradas."
            : $"Faltó cobertura en {problemas.Count} credencial(es): {string.Join(" · ", problemas)}."
              + (graphOk ? "" : " Los indicadores de Entra ID (MFA, cuentas, invitados, administradores) no son concluyentes.")
              + (signInOk ? "" : " El último inicio de sesión no está disponible, así que la inactividad no se puede evaluar.");

        return new AccessFinding("alcance_incompleto", AccessFindingSeverity.Informativa,
            "Alcance de la corrida",
            detail,
            "Si falta admin consent de Graph, solicitarlo al cliente con los permisos de aplicación del módulo. "
            + "Si el tenant no tiene Entra ID P1, dejar constancia de que la inactividad no es medible. "
            + "Las asignaciones elegibles vía PIM y los Classic administrators quedan fuera del alcance de esta revisión.",
            true, null, problemas.Count, 0, []);
    }
}
