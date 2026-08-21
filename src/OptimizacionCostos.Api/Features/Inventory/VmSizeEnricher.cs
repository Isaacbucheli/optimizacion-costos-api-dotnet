using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace OptimizacionCostos.Api.Features.Inventory;

/// <summary>Capacidades de un tamaño de VM que salen de la API de SKUs de ARM.</summary>
/// <param name="VcpusAvailable">
/// vCores ACTIVOS (capacidad <c>vCPUsAvailable</c>). Es el número que Azure usa para licenciar
/// SQL Server, y el único que distingue un tamaño de núcleo restringido: para Standard_E32-16s_v3
/// vale 16 mientras <paramref name="Vcpus"/> vale 32.
/// </param>
/// <param name="Vcpus">vCores del tamaño base (capacidad <c>vCPUs</c>).</param>
/// <param name="MemoryGb">Memoria en GB (capacidad <c>MemoryGB</c>).</param>
public sealed record VmSkuCapabilities(int? VcpusAvailable, int? Vcpus, double? MemoryGb)
{
    /// <summary>El conteo que se guarda en vm_details.vcpu_count: los activos, con el base de respaldo.</summary>
    public int? LicensableVcpus => VcpusAvailable ?? Vcpus;
}

/// <summary>Resultado del enriquecimiento de tamaños de VM.</summary>
/// <param name="Stamped">Cuántas filas quedaron con vcpuCount.</param>
/// <param name="Warnings">Advertencias visibles (nunca un cero silencioso).</param>
public sealed record VmSizeEnrichment(int Stamped, IReadOnlyList<string> Warnings);

/// <summary>
/// Enriquecimiento ARM del servicio vms: agrega a cada fila de Resource Graph el conteo real de
/// vCores y la memoria del tamaño, leídos de <c>Microsoft.Compute/skus</c>.
///
/// Por qué existe (bug encontrado 2026-08-21): Resource Graph solo entrega el NOMBRE del tamaño
/// (properties.hardwareProfile.vmSize). El conteo de vCores hay que pedirlo aparte a ARM, y como
/// nunca se pedía, vm_details.vcpu_count quedaba NULL en el 100% de las filas y el cálculo de la
/// licencia de SQL Server terminaba deduciendo el conteo del nombre. En los tamaños de núcleo
/// restringido eso cobraba el doble de licencia (Standard_E32-16s_v3 se licencia por 16 vCores, no
/// por 32) y en las familias viejas se equivocaba en cualquier dirección.
///
/// Se usa la capacidad <c>vCPUsAvailable</c> y NO el <c>numberOfCores</c> del endpoint viejo
/// <c>locations/{loc}/vmSizes</c> (el que consume ReportInventory para el informe mensual): ese
/// devuelve el conteo del tamaño base y no distingue el núcleo restringido, así que no serviría
/// para licenciar.
///
/// Doctrina de StorageFilesEnricher: token una vez por lote, tope de paginación, y todo lo que no se
/// pudo resolver sale como advertencia visible en vez de quedar en cero callado. Si ARM falla, las
/// filas van sin vcpuCount y el cálculo cae al respaldo de VmSizeVcpu, que ya entiende el guion.
/// </summary>
public interface IVmSizeEnricher
{
    /// <summary>
    /// Estampa <c>vcpuCount</c> y <c>memoryGb</c> en cada fila de VM (muta los JsonObject recibidos).
    /// </summary>
    Task<VmSizeEnrichment> EnrichAsync(
        TokenCredential credential, IReadOnlyList<JsonNode> vmRows, CancellationToken ct);
}

public sealed class VmSizeEnricher(
    IHttpClientFactory httpFactory,
    ILogger<VmSizeEnricher> logger) : IVmSizeEnricher
{
    private const string ArmScope = "https://management.azure.com/.default";

    /// <summary>Versión estable de Microsoft.Compute/skus que ya expone vCPUsAvailable.</summary>
    private const string ApiVersion = "2021-07-01";

    /// <summary>Tope de paginación (patrón de StorageFilesEnricher.MaxPages): un nextLink en bucle
    /// no debe colgar la importación. El catálogo de SKUs de una región entra de sobra.</summary>
    internal const int MaxPages = 50;

    public async Task<VmSizeEnrichment> EnrichAsync(
        TokenCredential credential, IReadOnlyList<JsonNode> vmRows, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (vmRows.Count == 0)
        {
            return new VmSizeEnrichment(0, warnings);
        }

        // Una llamada por (suscripción, región): el filtro de la API solo acepta location.
        var pairs = new HashSet<(string Sub, string Location)>();
        foreach (var node in vmRows)
        {
            var row = new RgRow(node);
            var sub = row.Str("subscriptionId");
            var loc = row.Str("location");
            if (!string.IsNullOrEmpty(sub) && !string.IsNullOrEmpty(loc))
            {
                pairs.Add((sub, loc));
            }
        }
        if (pairs.Count == 0)
        {
            warnings.Add("ninguna VM trajo subscriptionId y location; no se pudo consultar el catálogo de tamaños");
            return new VmSizeEnrichment(0, warnings);
        }

        var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        // Clave (región, tamaño): un mismo tamaño puede no existir en todas las regiones.
        var catalog = new Dictionary<(string Location, string Size), VmSkuCapabilities>(SkuKeyComparer.Instance);
        foreach (var (sub, loc) in pairs)
        {
            try
            {
                var found = await FetchSkusAsync(http, token.Token, sub, loc, ct);
                foreach (var (size, caps) in found)
                {
                    catalog[(loc, size)] = caps;
                }
                if (found.Count == 0)
                {
                    warnings.Add($"{loc}: la API de SKUs no devolvió tamaños de VM; se usará el respaldo por nombre");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Microsoft.Compute/skus falló sub={Sub} loc={Loc}: {Type}", sub, loc, ex.GetType().Name);
                warnings.Add($"{loc}: no se pudo leer el catálogo de tamaños de VM ({ex.GetType().Name}); se usará el respaldo por nombre");
            }
        }

        var stamped = 0;
        var sinResolver = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in vmRows)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }
            var row = new RgRow(obj);
            var size = row.Str("vmSize");
            var loc = row.Str("location");
            if (string.IsNullOrEmpty(size) || string.IsNullOrEmpty(loc))
            {
                continue;
            }
            if (!catalog.TryGetValue((loc, size), out var caps))
            {
                sinResolver.Add($"{size} ({loc})");
                continue;
            }
            var vcpus = caps.LicensableVcpus;
            if (vcpus is not > 0)
            {
                sinResolver.Add($"{size} ({loc})");
                continue;
            }
            obj["vcpuCount"] = vcpus.Value;
            if (caps.MemoryGb is > 0)
            {
                obj["memoryGb"] = caps.MemoryGb.Value;
            }
            stamped++;
        }

        if (sinResolver.Count > 0)
        {
            warnings.Add(
                "tamaños sin conteo de vCores en el catálogo de ARM (la licencia SQL de esas VMs se " +
                $"estimará desde el nombre): {string.Join(", ", sinResolver)}");
        }

        return new VmSizeEnrichment(stamped, warnings);
    }

    private static async Task<Dictionary<string, VmSkuCapabilities>> FetchSkusAsync(
        HttpClient http, string token, string subscriptionId, string location, CancellationToken ct)
    {
        var result = new Dictionary<string, VmSkuCapabilities>(StringComparer.OrdinalIgnoreCase);
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Compute/skus"
                  + $"?api-version={ApiVersion}&$filter=location eq '{Uri.EscapeDataString(location)}'";

        for (var page = 0; page < MaxPages && !string.IsNullOrEmpty(url); page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var sku in value.EnumerateArray())
                {
                    if (!sku.TryGetProperty("resourceType", out var rt)
                        || !string.Equals(rt.GetString(), "virtualMachines", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var name = sku.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }
                    result[name] = ReadCapabilities(sku);
                }
            }

            url = doc.RootElement.TryGetProperty("nextLink", out var next) ? next.GetString() : null;
        }

        return result;
    }

    private static VmSkuCapabilities ReadCapabilities(JsonElement sku)
    {
        int? available = null, vcpus = null;
        double? memory = null;
        if (sku.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
        {
            foreach (var cap in caps.EnumerateArray())
            {
                var name = cap.TryGetProperty("name", out var cn) ? cn.GetString() : null;
                var raw = cap.TryGetProperty("value", out var cv) ? cv.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(raw))
                {
                    continue;
                }
                if (string.Equals(name, "vCPUsAvailable", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a))
                {
                    available = a;
                }
                else if (string.Equals(name, "vCPUs", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    vcpus = v;
                }
                else if (string.Equals(name, "MemoryGB", StringComparison.OrdinalIgnoreCase)
                         && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
                {
                    memory = m;
                }
            }
        }
        return new VmSkuCapabilities(available, vcpus, memory);
    }

    /// <summary>Comparador de la clave (región, tamaño): ARM devuelve ambos con capitalización variable.</summary>
    private sealed class SkuKeyComparer : IEqualityComparer<(string Location, string Size)>
    {
        public static readonly SkuKeyComparer Instance = new();

        public bool Equals((string Location, string Size) x, (string Location, string Size) y)
            => string.Equals(x.Location, y.Location, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Size, y.Size, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Location, string Size) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Location),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Size));
    }
}
