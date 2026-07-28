using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Data;

/// <summary>
/// Abre conexiones a la BD del tablero de pendientes/bloqueantes (Seguimiento CDC), que vive en OTRO
/// servidor Azure SQL y la administra otro sistema (la SWA del tablero). Es una interfaz aparte de
/// <see cref="ISqlConnectionFactory"/> a propósito: son dos bases distintas y ningún módulo debe
/// mezclarlas por accidente.
///
/// Regla dura: la app NUNCA hace DDL en esta base (nada de CREATE/ALTER/DROP ni EnsureSchema).
/// </summary>
public interface ISeguimientoSqlConnectionFactory
{
    /// <summary>False si faltan las variables SQL_*2: el módulo queda apagado (503), no roto.</summary>
    bool IsConfigured { get; }

    Task<SqlConnection> OpenAsync(CancellationToken ct = default);
}
