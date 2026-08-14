namespace OptimizacionCostos.Api.Features.InformeValor.Entrega;

/// <summary>
/// Los ocho bloques económicos que se aprueban uno por uno para la variante del cliente (spec,
/// §UX). <b>Nacen apagados</b>: generar sin decidir produce la versión sin cifras.
///
/// <para><b>Un bloque apagado no es un cero</b> (F1). Con el bloque apagado la sección sigue
/// apareciendo con su relato en conteos y porcentajes, y donde iría el monto dice "No publicado".
/// Un informe de cliente que muestre 0 donde debería decir "no publicado" le afirma algo falso a
/// quien paga la factura, y es el defecto más repetido de este módulo.</para>
///
/// <para><see cref="AhorroEjecutado"/> y <see cref="ReservasFacturadas"/> se suman en la entrega 6
/// junto con la clave <c>ejecutado</c> del modelo (el titular del informe, decisión 2026-08-13):
/// sin su propio interruptor, esa clave viajaría intacta en la variante del cliente y filtraría
/// todos los montos del acumulado a quien nadie los aprobó.</para>
/// </summary>
public enum BloqueEconomico
{
    /// <summary>Gasto total del período: el monto acumulado y el promedio mensual.</summary>
    GastoTotal,

    /// <summary>Serie mensual de consumo: el gráfico de facturación mes a mes con sus montos.</summary>
    SerieMensual,

    /// <summary>Composición por servicio: en qué se gasta, con monto y porcentaje por categoría.</summary>
    ComposicionServicio,

    /// <summary>Ahorro activo: la línea que dejó de facturar, con su tasa mensual y anualizada.</summary>
    AhorroActivo,

    /// <summary>Reparto por centro de costo: el gasto asignado a cada área del cliente.</summary>
    CentroCosto,

    /// <summary>Ahorro identificado por Advisor: la cifra realizable tras depurar duplicados y
    /// opciones excluyentes.</summary>
    AhorroAdvisor,

    /// <summary>Ahorro ejecutado: el acumulado de lo que efectivamente se aplicó (barrido, matriz y
    /// reservas), con su serie mensual y el desglose por oportunidad y por categoría.</summary>
    AhorroEjecutado,

    /// <summary>Reservas facturadas: la tabla que cruza cada VM cubierta por reserva contra lo que
    /// costaba por demanda, con el ahorro resultante.</summary>
    ReservasFacturadas,
}

public static class BloqueEconomicoExtensions
{
    /// <summary>
    /// Todos los bloques. Lo usa la variante interna, que publica todo, y la validación del
    /// endpoint de generación.
    /// </summary>
    public static readonly IReadOnlyList<BloqueEconomico> Todos = Enum.GetValues<BloqueEconomico>();

    /// <summary>
    /// El literal del bloque. <b>La misma grafía en los tres lugares</b>: el cuerpo del POST de
    /// generación, la columna <c>bloques_publicados</c> y el objeto <c>PUBLICACION</c> que lee la
    /// capa de dibujo. Es camelCase y no snake_case a propósito: el consumidor final es JavaScript,
    /// y traducir la grafía en el camino es exactamente cómo nacen dos nombres para una cosa.
    /// </summary>
    public static string Clave(this BloqueEconomico bloque) => bloque switch
    {
        BloqueEconomico.GastoTotal => "gastoTotal",
        BloqueEconomico.SerieMensual => "serieMensual",
        BloqueEconomico.ComposicionServicio => "composicionServicio",
        BloqueEconomico.AhorroActivo => "ahorroActivo",
        BloqueEconomico.CentroCosto => "centroCosto",
        BloqueEconomico.AhorroAdvisor => "ahorroAdvisor",
        BloqueEconomico.AhorroEjecutado => "ahorroEjecutado",
        BloqueEconomico.ReservasFacturadas => "reservasFacturadas",
        _ => throw new ArgumentOutOfRangeException(nameof(bloque)),
    };

    /// <summary>Inversa de <see cref="Clave"/>; <c>null</c> si el literal no es ninguno de los ocho.
    /// Quien llama decide qué hacer con un bloque desconocido, pero nunca se ignora en silencio: un
    /// bloque que el consultor creyó aprobar y que nadie reconoció sale apagado sin avisar.</summary>
    public static BloqueEconomico? Parsear(string? clave)
    {
        var c = clave?.Trim();
        foreach (var b in Todos)
            if (string.Equals(b.Clave(), c, StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }
}
