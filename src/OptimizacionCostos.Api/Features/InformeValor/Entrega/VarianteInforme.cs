namespace OptimizacionCostos.Api.Features.InformeValor.Entrega;

/// <summary>
/// Las dos variantes del informe (spec, decisión del 2026-08-06). <see cref="Interna"/> lleva todo;
/// <see cref="Cliente"/> lleva solo los bloques económicos aprobados uno por uno.
///
/// <para><b>La variante decide qué se DIBUJA y qué VIAJA, nunca qué se calcula</b> (F1): el modelo
/// se calcula completo siempre y el exportador es el que recorta. Calcular distinto por variante
/// haría que las dos versiones del mismo informe no fueran comparables entre sí, y que aprobar un
/// bloque después obligara a recalcular.</para>
/// </summary>
public enum VarianteInforme
{
    Interna,
    Cliente,
}

public static class VarianteInformeExtensions
{
    /// <summary>
    /// El literal con el que esta variante viaja por la API, se guarda en
    /// <c>informe_valor_entrega.variante</c> y aparece en el <c>PUBLICACION</c> del artefacto.
    /// <b>Una sola grafía para los tres usos</b>: dos piezas del producto nombrando el mismo
    /// concepto distinto es el defecto más repetido de este módulo.
    /// </summary>
    public static string Clave(this VarianteInforme variante) => variante switch
    {
        VarianteInforme.Interna => "interna",
        VarianteInforme.Cliente => "cliente",
        _ => throw new ArgumentOutOfRangeException(nameof(variante)),
    };

    /// <summary>Inversa de <see cref="Clave"/>. <c>null</c> si el literal no es ninguna de las dos:
    /// quien llama decide si eso es un 400 o un default, pero nunca se adivina.</summary>
    public static VarianteInforme? Parsear(string? clave) => clave?.Trim().ToLowerInvariant() switch
    {
        "interna" => VarianteInforme.Interna,
        "cliente" => VarianteInforme.Cliente,
        _ => null,
    };
}
