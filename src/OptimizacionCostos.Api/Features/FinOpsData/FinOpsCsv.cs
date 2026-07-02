namespace OptimizacionCostos.Api.Features.FinOpsData;

/// <summary>
/// Parser CSV mínimo RFC-4180 para los datasets open data del FinOps Toolkit
/// (comillas, comas/saltos de línea embebidos, comillas escapadas "" y BOM).
/// Fuente de los datos: Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit
/// </summary>
internal static class FinOpsCsv
{
    public static List<string[]> Parse(string content, out string[] header)
    {
        var all = new List<string[]>();
        var cell = new System.Text.StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        var i = 0;
        if (content.Length > 0 && content[0] == '﻿') i = 1;

        void EndCell() { row.Add(cell.ToString()); cell.Clear(); }
        void EndRow()
        {
            EndCell();
            if (row.Count > 1 || row[0].Length > 0) all.Add(row.ToArray());
            row.Clear();
        }

        for (; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') EndCell();
            else if (c == '\n') EndRow();
            else if (c != '\r') cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) EndRow();

        header = all.Count > 0 ? all[0] : [];
        return all.Count > 0 ? all.GetRange(1, all.Count - 1) : all;
    }
}
