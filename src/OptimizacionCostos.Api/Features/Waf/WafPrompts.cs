namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Prompts EXACTOS del módulo WAF, portados 1:1 del Python (app/waf/ai_curator.py y
/// app/waf/dedup.py). Se mantienen textuales (mismo wording, mismo orden de campos JSON,
/// mismos acentos omitidos) para que las respuestas IA coincidan con las del FastAPI.
/// NO modificar el texto: cualquier cambio altera el comportamiento del modelo.
/// </summary>
public static class WafPrompts
{
    /// <summary>Pilares válidos en el orden y wording exacto del Python (PILLAR_NAMES de ai_curator.py).</summary>
    public static readonly IReadOnlyDictionary<int, string> CuratorPillarNames = new Dictionary<int, string>
    {
        [1] = "Eficiencia del rendimiento",
        [2] = "Excelencia operacional",
        [3] = "Seguridad",
        [4] = "Confiabilidad",
        [5] = "Optimizacion de costos",
    };

    /// <summary>Decisiones válidas del curador (VALID_DECISIONS de ai_curator.py).</summary>
    public static readonly IReadOnlySet<string> ValidDecisions = new HashSet<string>(StringComparer.Ordinal)
    {
        "include", "exclude", "review",
    };

    /// <summary>
    /// Prompt del curador (ai_curator.py::_prompt). {0}=pilares JSON, {1}=canonical_id,
    /// {2}=advisor_name, {3}=advisor_category, {4}=pillar_number actual, {5}=review_scope_es actual,
    /// {6}=benefit_es actual, {7}=client_action_es actual, {8}=bit_action_es actual.
    /// El cuerpo del JSON requerido NO lleva placeholders (las llaves van escapadas en el Format()).
    /// </summary>
    public const string CuratorTemplate =
        """
        Eres un consultor senior Azure de Business IT. Cura una recomendacion de Azure Advisor
        para una matriz ejecutiva de optimizacion y mejoras.

        Reglas:
        - Devuelve unicamente JSON valido, sin markdown ni caracteres decorativos.
        - No inventes datos tecnicos.
        - Si implica habilitar servicios o licencias con costo adicional, usa
          possible_additional_cost=true y decision="review" o "exclude".
        - Microsoft Defender, Defender for Cloud, Defender CSPM y protecciones pagadas
          deben marcar posible costo adicional.
        - Redacta TODOS los campos de texto en espanol corporativo claro para presentar al cliente.
        - review_scope_es debe ser un titulo/ambito en espanol, traducido y mejorado a partir del
          advisor_name. NUNCA lo dejes en ingles ni copies el advisor_name tal cual; reescribelo como
          una accion clara y concisa en espanol (maximo ~120 caracteres).
        - Si algun texto de entrada viene en ingles, traducelo al espanol; no devuelvas texto en ingles.
        - client_action_es indica lo que el cliente debe aprobar, confirmar o coordinar.
        - bit_action_es indica lo que Business IT debe analizar, ejecutar o acompanar.
        - confidence debe estar entre 0 y 1.

        Pilares validos: {0}

        Recomendacion:
        - canonical_id: {1}
        - advisor_name: {2}
        - advisor_category: {3}
        - pillar_number actual: {4}
        - review_scope_es actual: {5}
        - benefit_es actual: {6}
        - client_action_es actual: {7}
        - bit_action_es actual: {8}

        JSON requerido:
        {{
          "decision": "include|exclude|review",
          "possible_additional_cost": false,
          "cost_reason": "",
          "duplicate_group_key": "",
          "pillar_number": 1,
          "review_scope_es": "",
          "benefit_es": "",
          "client_action_es": "",
          "bit_action_es": "",
          "exclusion_reason": "",
          "confidence": 0.0
        }}
        """;

    /// <summary>
    /// Prompt de confirmación de duplicado (dedup.py::ai_confirm_duplicate). {0}=advisor_name,
    /// {1}=advisor_category, {2}=JSON array de candidatos. Las llaves del ejemplo JSON van escapadas.
    /// </summary>
    public const string ConfirmDuplicateTemplate =
        """
        Eres un consultor senior Azure. Debes decidir si una recomendacion nueva de Azure Advisor
        ya existe en la matriz del cliente (cargada a mano desde Excel o en un sync anterior),
        para NO duplicarla.

        Responde solo JSON valido. Si ningun candidato representa exactamente la MISMA
        recomendacion, devuelve match=false. No inventes. Ante la duda, match=false.

        Recomendacion de Azure Advisor:
        - nombre: {0}
        - categoria: {1}

        Candidatos ya cargados:
        {2}

        JSON requerido:
        {{"match": true, "canonical_id": 123, "confidence": 0.0, "reason": ""}}
        """;
}
