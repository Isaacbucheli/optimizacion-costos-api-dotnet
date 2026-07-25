namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>Tipo de dato de una columna: define formato numérico y alineación.
/// `Eligibility` es el marcador visible "Sí"/"No" (mismo tratamiento que Text para el
/// SheetWriter, pero con su propio Kind para no confundirlo con texto libre). La fórmula viva de
/// Ahorro por fila lo usa como criterio: IF(AND(Elegible="Sí", RI&lt;&gt;""), PAYG−RI, 0) — ver spec §3.6.
/// `Decimal` es igual a `Number` (numérico, mismo tratamiento en SetCellValue) salvo el formato de
/// celda: 2 decimales en vez de entero — para capacidades donde el decimal es significativo (ej.
/// GiB facturables de Storage Files: el corte de 10 TiB se decide con decimales, revisión Task 9
/// 2026-07-24). Se agrega al final del enum para no correr el ordinal de los miembros existentes.</summary>
public enum ColKind { Text, Money, Percent, Number, Hours, Eligibility, Decimal }

/// <summary>
/// Rol de una columna de dinero dentro de la fila TOTAL comparable. `None` = columna que no
/// participa en el TOTAL (se deja vacía en esa fila).
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
/// Especificación declarativa de una hoja: nombre, columnas y si aplica la fila TOTAL comparable
/// (una única fila TOTAL con fórmulas SUBTOTAL(9,…) + "Total optimizado = PAYG − Ahorro").
/// </summary>
public sealed record SheetSpec(string Name, IReadOnlyList<ColumnSpec> Columns, bool ComparableTotals = true);
