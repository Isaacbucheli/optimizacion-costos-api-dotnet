using System.Security.Cryptography;
using System.Text;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Hash de la clave natural de una fila de insumo. Se calcula SIEMPRE sobre el valor
/// completo, nunca sobre el truncado al ancho de la columna: truncar antes de hashear
/// funde dos recursos que difieran después del carácter 512.
/// </summary>
public static class NaturalKey
{
    // Separador de unidad (U+001F): no aparece en un export de Excel, así que "ab<sep>c" y "a<sep>bc" no colisionan.
    private const char Sep = '\u001F';

    public static string Hash(params string?[] parts)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append(Sep);
            sb.Append(parts[i] ?? string.Empty);
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
