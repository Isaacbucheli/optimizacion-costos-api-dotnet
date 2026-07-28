namespace OptimizacionCostos.Api.Features.Pendientes;

/// <summary>
/// Fila de <c>dbo.Cliente</c> (catálogo propio del tablero). No confundir con <c>dbo.clients</c> de la
/// plataforma ni con <c>dbo.cdc_assignments</c> de Asignación de consultores: son catálogos separados
/// a propósito (decisión del usuario, 2026-07-28).
/// </summary>
public sealed record PendienteCliente
{
    public int Num { get; init; }
    public string Cliente { get; init; } = "";
    public string? Servicio { get; init; }
    public string? Categoria { get; init; }
    public string? Pais { get; init; }
    public string? Coordinador { get; init; }
    public string? Consultor { get; init; }
}

/// <summary>
/// Nota del timeline (<c>dbo.Historial</c>). <c>Orden</c> es orden de inserción y NO coincide con
/// <c>Fecha</c>: el timeline se muestra por <c>Orden</c> (ver design doc).
/// </summary>
public sealed record PendienteNota
{
    public int HistId { get; init; }
    public DateOnly? Fecha { get; init; }
    public string Nota { get; init; } = "";
    public string? Autor { get; init; }
    public int Orden { get; init; }
}

/// <summary>
/// Pendiente o bloqueante (<c>dbo.Pendiente</c>) con su historial. <c>Actualizado</c> viaja al front y
/// vuelve en la edición como token de concurrencia optimista.
/// </summary>
public sealed record PendienteItem
{
    public string Id { get; init; } = "";
    public int ClienteNum { get; init; }
    public string? Titulo { get; init; }
    public string? Descripcion { get; init; }
    public string? Tipo { get; init; }
    public string? Prioridad { get; init; }
    public string? Estado { get; init; }
    public string? Responsable { get; init; }
    public DateOnly? FechaCreacion { get; init; }
    public DateTime Actualizado { get; init; }
    public IReadOnlyList<PendienteNota> Historial { get; init; } = [];
}

/// <summary>Payload completo de un área: lo que la pantalla necesita en una sola llamada.</summary>
public sealed record PendientesPayload
{
    public string Area { get; init; } = "";
    public IReadOnlyList<PendienteCliente> Clientes { get; init; } = [];
    public IReadOnlyList<PendienteItem> Pendientes { get; init; } = [];
}

/// <summary>
/// Body de alta/edición de un pendiente. Tipo/Prioridad/Estado llegan como texto y se normalizan
/// contra la lista blanca de <see cref="PendientesDomain"/>; un valor fuera de lista es 400.
/// </summary>
public sealed record PendienteWrite
{
    public int ClienteNum { get; init; }
    public string? Titulo { get; init; }
    public string? Descripcion { get; init; }
    public string? Tipo { get; init; }
    public string? Prioridad { get; init; }
    public string? Estado { get; init; }
    public string? Responsable { get; init; }

    /// <summary>Solo en edición: el <c>Actualizado</c> que traía la fila leída. Sin él no se edita.</summary>
    public DateTime? Actualizado { get; init; }
}

/// <summary>Body de una nota nueva. El autor lo pone el backend con el usuario de la sesión.</summary>
public sealed record NotaWrite
{
    public string Nota { get; init; } = "";
    public DateOnly? Fecha { get; init; }
}

/// <summary>Body de alta/edición de cliente del catálogo. El <c>Num</c> lo asigna el backend.</summary>
public sealed record ClienteWrite
{
    public string Cliente { get; init; } = "";
    public string? Servicio { get; init; }
    public string? Categoria { get; init; }
    public string? Pais { get; init; }
    public string? Coordinador { get; init; }
    public string? Consultor { get; init; }
}

/// <summary>Resultado de una escritura con concurrencia optimista.</summary>
public enum WriteOutcome { Ok, NotFound, Conflict }

/// <summary>Resultado del borrado de un cliente del catálogo.</summary>
public enum ClienteDeleteOutcome { Ok, NotFound, HasPendientes }
