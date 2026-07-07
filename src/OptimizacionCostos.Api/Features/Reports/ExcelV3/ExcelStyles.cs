using ClosedXML.Excel;

namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>
/// Estilos compartidos por todas las hojas del exportador v3 (código, no plantilla). Centraliza
/// colores, formatos numéricos y el "look" de header/zebra/subtotal para que todas las hojas sean
/// visualmente consistentes sin repetir código en cada escritor.
/// </summary>
public static class ExcelStyles
{
    public const string HeaderHex = "#5CB531";
    public const string ZebraHex = "#F5F7F5";

    public const string Money = "$#,##0.00";
    public const string Percent = "0.0%";
    public const string Int = "#,##0";

    /// <summary>Fila 1: bold + texto blanco + fill de marca, freeze de la fila 1 y autofiltro A1..colCount.</summary>
    public static void Header(IXLWorksheet ws, int colCount)
    {
        var headerRow = ws.Row(1);
        for (var col = 1; col <= colCount; col++)
        {
            var cell = headerRow.Cell(col);
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.FromHtml("#FFFFFF");
            cell.Style.Fill.PatternType = XLFillPatternValues.Solid;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(HeaderHex);
        }
        ws.SheetView.FreezeRows(1);
        ws.Range(1, 1, 1, colCount).SetAutoFilter();
    }

    /// <summary>Fila de datos: aplica zebra (fondo suave) en filas pares cuando corresponde.</summary>
    public static void DataRow(IXLRow row, bool zebra)
    {
        if (!zebra) return;
        row.Style.Fill.PatternType = XLFillPatternValues.Solid;
        row.Style.Fill.BackgroundColor = XLColor.FromHtml(ZebraHex);
    }

    /// <summary>Fila de subtotal: itálica gris; si es la fila TOTAL (grand), bold + borde superior.</summary>
    public static void SubtotalRow(IXLRow row, bool grand)
    {
        if (grand)
        {
            row.Style.Font.Bold = true;
            row.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }
        else
        {
            row.Style.Font.Italic = true;
            row.Style.Font.FontColor = XLColor.FromHtml("#666666");
        }
    }
}
