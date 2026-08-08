using System.Text.Encodings.Web;
using System.Text.Json;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// <b>La trampa más cara del port (D13 del plan de la entrega 2b), medida, no supuesta:</b> el
/// repo serializa toda respuesta de controller en snake_case, INCLUIDAS las claves de
/// diccionario, vía la configuración global de <c>System.Text.Json</c> registrada en
/// <c>Program.cs</c>. La capa de dibujo de la plantilla (el <c>render()</c> embebido, que se
/// reusa sin reescribir) espera los nombres tal cual salen del JavaScript original:
///
/// <list type="bullet">
/// <item>Un campo camelCase como <c>ultCompleto</c> llegaría como <c>ult_completo</c> y el
/// dibujo revienta a mitad de pintado con un <c>undefined</c>.</item>
/// <item>Una serie indexada por categoría (<see cref="ModeloInformeValor.CatSerie"/>, o
/// <c>porSub</c> dentro de <see cref="PosturaModelo"/>) quedaría con las claves TRANSFORMADAS: el
/// código busca <c>D.catSerie["Virtual Machines"]</c> pero la clave real terminaría siendo
/// <c>"virtual_machines"</c>, la búsqueda no encuentra nada, y el gráfico de ahorro dibuja todo
/// en cero bajo un título que afirma que hubo ahorro.</item>
/// </list>
///
/// La solución no es configurar la política global (es de otro dueño y puede cambiar sin que este
/// módulo se entere): el exportador de este módulo usa <b>sus propios</b>
/// <see cref="JsonSerializerOptions"/>, con <see cref="JsonSerializerOptions.PropertyNamingPolicy"/>
/// en <c>null</c> (cada campo lleva su nombre exacto vía <c>[JsonPropertyName]</c> en el modelo,
/// nunca se infiere) y <see cref="JsonSerializerOptions.DictionaryKeyPolicy"/> también en
/// <c>null</c> (las claves de diccionario viajan tal cual, sin transformación alguna).
///
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> es el mismo encoder que usa el resto
/// del módulo de informes: sin él, texto con acentos o símbolos sale escapado como <c>\uXXXX</c>
/// dentro del <c>&lt;script&gt;</c> del artefacto exportado, que sigue siendo válido pero sería
/// ilegible para quien lo inspeccione a mano. Escapar <c>&lt;/script&gt;</c> y <c>&lt;!--</c> para
/// que el JSON no rompa el documento que lo envuelve es responsabilidad de quien arma el HTML
/// final (el exportador de la entrega 3, no esta clase): <c>UnsafeRelaxedJsonEscaping</c> NO lo
/// hace por sí solo.
///
/// <para><b>Duda para la Tarea 8 (fuera de esta entrega): a qué endpoint aplica esto.</b> El
/// artefacto HTML exportado (<c>/generar</c>) tiene que usar <see cref="Instance"/> sí o sí: es el
/// que alimenta <c>render()</c> reutilizado. El endpoint <c>/preview</c>, en cambio, alimenta una
/// vista React NUEVA (Recharts, entrega 3), no <c>render()</c>: no hay ningún motivo técnico para
/// que tenga que usar estos mismos nombres, y el resto de la API (y del frontend React existente)
/// ya está estandarizado en snake_case vía <c>Program.cs</c>. Lo más consistente sería que
/// <c>/preview</c> siga esa convención (un <c>Ok(modelo)</c> normal) y que <see cref="Instance"/>
/// se use SOLO en el paso de armar el <c>&lt;script&gt;</c> del HTML. Pero esa es una decisión de
/// la Tarea 8, no de este contrato: se deja escrita acá para que no se decida por default (un
/// <c>Ok(modelo)</c> ingenuo en el controller de <c>/generar</c> usaría la política global y
/// reintroduciría exactamente el defecto que esta clase existe para evitar).</para>
/// </summary>
public static class InformeValorJsonOptions
{
    public static readonly JsonSerializerOptions Instance = new()
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
