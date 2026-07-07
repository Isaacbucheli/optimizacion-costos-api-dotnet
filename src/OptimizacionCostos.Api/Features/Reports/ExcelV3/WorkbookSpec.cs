namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>Tipo de dato de una columna: define formato numérico y alineación.</summary>
public enum ColKind { Text, Money, Percent, Number, Hours }

/// <summary>
/// Rol de una columna de dinero dentro del bloque de totales comparables. `None` = columna que no
/// participa en el bloque de totales (se deja vacía en las 3 filas de subtotal/total).
/// </summary>
public enum MoneyRole { None, Payg, Ri1, Ri3, Savings1Usd, Savings3Usd, Savings1Pct, Savings3Pct }

/// <summary>
/// Especificación declarativa de una columna: encabezado, tipo, cómo extraer el valor de la fila
/// (diccionario canónico case-insensitive) y, opcionalmente, ancho fijo y rol para el bloque de
/// totales comparables.
/// </summary>
public sealed record ColumnSpec(
    string Header,
    ColKind Kind,
    Func<IDictionary<string, object?>, object?> Extract,
    double? Width = null,
    MoneyRole Role = MoneyRole.None);

/// <summary>
/// Especificación declarativa de una hoja: nombre, columnas y si aplica el bloque de totales
/// comparables (3 filas: subtotal elegible / subtotal no elegible / TOTAL).
/// </summary>
public sealed record SheetSpec(string Name, IReadOnlyList<ColumnSpec> Columns, bool ComparableTotals = true);
