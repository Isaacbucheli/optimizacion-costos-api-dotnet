namespace OptimizacionCostos.Api.Features.Consultants;

/// <summary>
/// Repositorio del módulo Asignación de consultores (Gestión CDC): directorio de personas,
/// asignaciones cliente×servicio con relación N:N (principal/backup) y reasignación masiva.
/// Los updates reciben solo las columnas presentes (semántica exclude_unset); los conjuntos
/// principal/backup se REEMPLAZAN completos cuando vienen (null = no tocar).
/// </summary>
public interface IConsultantsStore
{
    // ---- Personas ----
    /// <summary>Todas las personas, activas e inactivas (el front decide cómo mostrarlas).</summary>
    Task<IReadOnlyList<PersonItem>> ListPeopleAsync(CancellationToken ct = default);
    Task<PersonItem?> GetPersonAsync(int personId, CancellationToken ct = default);
    Task<int> CreatePersonAsync(PersonCreate data, CancellationToken ct = default);
    Task<bool> UpdatePersonAsync(int personId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    /// <summary>Soft-delete: is_active=0; sus vínculos históricos se conservan.</summary>
    Task<bool> SoftDeletePersonAsync(int personId, CancellationToken ct = default);
    /// <summary>De los ids dados, cuáles existen y están activos (para validar referencias).</summary>
    Task<IReadOnlySet<int>> GetActivePersonIdsAsync(IReadOnlyCollection<int> personIds, CancellationToken ct = default);

    // ---- Asignaciones ----
    /// <summary>Solo activas, ORDER BY client_name, con personas embebidas.</summary>
    Task<IReadOnlyList<AssignmentItem>> ListAssignmentsAsync(CancellationToken ct = default);
    Task<AssignmentItem?> GetAssignmentAsync(int assignmentId, CancellationToken ct = default);
    Task<int> CreateAssignmentAsync(AssignmentCreate data, CancellationToken ct = default);
    /// <summary>
    /// Actualiza escalares (whitelist) y, si principalIds/backupIds vienen no-nulos,
    /// reemplaza transaccionalmente el conjunto completo de ese rol.
    /// </summary>
    Task<bool> UpdateAssignmentAsync(int assignmentId, IReadOnlyDictionary<string, object?> fields,
        IReadOnlyList<int>? principalIds, IReadOnlyList<int>? backupIds, CancellationToken ct = default);
    Task<bool> SoftDeleteAssignmentAsync(int assignmentId, CancellationToken ct = default);

    // ---- Reasignación masiva ----
    /// <summary>
    /// En UNA transacción sobre asignaciones ACTIVAS reemplaza a la persona origen por la
    /// destino según los scopes (principal/backup en cdc_assignment_consultants con merge
    /// si la destino ya está en la misma asignación+rol; coordinador/comercial por UPDATE
    /// de columna). Devuelve el número de asignaciones distintas tocadas.
    /// </summary>
    Task<int> ReassignAsync(int fromPersonId, int toPersonId, IReadOnlyList<string> scopes, CancellationToken ct = default);
}

/// <summary>Columnas mutables permitidas (whitelist anti-inyección al construir SET).</summary>
public static class ConsultantColumns
{
    public static readonly string[] Person = ["name", "email", "person_type", "is_active"];

    public static readonly string[] Assignment =
    [
        "client_name", "service", "category", "databases", "country", "status",
        "coordinator_id", "comercial_id", "access_accounts", "account_role", "lighthouse",
        "client_contact_name", "client_contact_phone", "client_contact_email",
        "contract_end", "observations",
    ];
}
