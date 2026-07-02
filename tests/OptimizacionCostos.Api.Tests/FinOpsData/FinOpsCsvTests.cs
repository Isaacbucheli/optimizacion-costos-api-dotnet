using OptimizacionCostos.Api.Features.FinOpsData;
using Xunit;

namespace OptimizacionCostos.Api.Tests.FinOpsData;

public sealed class FinOpsCsvTests
{
    [Fact]
    public void Parse_FilasSimples_ConHeader()
    {
        var rows = FinOpsCsv.Parse("a,b,c\r\n1,2,3\r\n4,5,6", out var header);
        Assert.Equal(new[] { "a", "b", "c" }, header);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "1", "2", "3" }, rows[0]);
    }

    [Fact]
    public void Parse_ComillasConComasYComillasEscapadas()
    {
        // ResourceTypes.csv trae descripciones con comas y comillas dobles escapadas ("")
        var rows = FinOpsCsv.Parse("\"MeterId\",\"Desc\"\n\"abc\",\"Hola, \"\"mundo\"\", fin\"", out _);
        Assert.Single(rows);
        Assert.Equal("Hola, \"mundo\", fin", rows[0][1]);
    }

    [Fact]
    public void Parse_SaltoDeLineaDentroDeComillas()
    {
        var rows = FinOpsCsv.Parse("a,b\n\"linea1\nlinea2\",x", out _);
        Assert.Single(rows);
        Assert.Equal("linea1\nlinea2", rows[0][0]);
    }

    [Fact]
    public void Parse_QuitaBomYLineasVacias()
    {
        var rows = FinOpsCsv.Parse("﻿a,b\r\n1,2\r\n\r\n", out var header);
        Assert.Equal("a", header[0]);
        Assert.Single(rows);
    }
}
