using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Cliente HTTP de la API de Azure Retail Prices (https://prices.azure.com/api/retail/prices).
/// Port 1:1 de app/pricing/azure_retail_client.py.
///
/// Sin autenticación. Filtros OData ($filter), currencyCode = "USD", paginación por
/// NextPageLink hasta MAX_PAGES. Cada <c>FetchX</c> arma sus filtros específicos, lanza
/// la consulta y aplica el filtrado post-fetch en memoria (OS, capacity, productName) que
/// hace el <c>fetch_*</c> equivalente en Python.
///
/// El <see cref="HttpClient"/> se inyecta por constructor para poder mockear el
/// <c>HttpMessageHandler</c> en tests. No se loguean secretos (la API no tiene auth).
/// </summary>
public sealed class RetailPriceClient : IRetailPriceClient
{
    // Paridad con las constantes del módulo Python.
    private const string BaseUrl = "https://prices.azure.com/api/retail/prices";
    private const string DefaultCurrency = "USD";
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(30);
    private const int MaxPages = 50; // ~50.000 registros, suficiente para queries focalizadas

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    /// <summary>
    /// Inyectar un <see cref="HttpClient"/> (en producción con un handler real; en tests con
    /// un <c>HttpMessageHandler</c> mockeado). Si el llamador no fija <c>Timeout</c>, se aplica
    /// el de página (30s), igual que el <c>httpx.Client(timeout=...)</c> de Python.
    /// </summary>
    public RetailPriceClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (_http.Timeout == System.Threading.Timeout.InfiniteTimeSpan ||
            _http.Timeout == TimeSpan.FromSeconds(100)) // 100s es el default de HttpClient
        {
            _http.Timeout = PageTimeout;
        }
    }

    // =====================================================================================
    //  Núcleo: construcción de filtros + paginación (port de _build_filter / fetch_prices)
    // =====================================================================================

    /// <summary>
    /// Port de <c>_build_filter</c>: arma un string $filter OData a partir de un dict simple.
    /// Listas → "(k eq 'a' or k eq 'b')"; bool → "k eq true/false"; numérico → "k eq N";
    /// resto → "k eq 'valor'". Las partes se unen con " and ". Orden de inserción preservado.
    /// </summary>
    internal static string BuildFilter(IReadOnlyList<KeyValuePair<string, object>> filters)
    {
        var parts = new List<string>();
        foreach (var (key, value) in filters)
        {
            switch (value)
            {
                case IReadOnlyList<string> list:
                    var ors = string.Join(" or ", list.Select(v => $"{key} eq '{v}'"));
                    parts.Add($"({ors})");
                    break;
                case bool b:
                    parts.Add($"{key} eq {(b ? "true" : "false")}");
                    break;
                case int or long or double or float or decimal:
                    parts.Add($"{key} eq {Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)}");
                    break;
                default:
                    parts.Add($"{key} eq '{value}'");
                    break;
            }
        }
        return string.Join(" and ", parts);
    }

    /// <summary>
    /// Port de <c>fetch_prices</c>: consulta la API con los filtros dados, itera por todas las
    /// páginas (NextPageLink) hasta agotar resultados o llegar a MAX_PAGES, y devuelve cada
    /// item mapeado a <see cref="PriceRow"/>.
    /// </summary>
    private IReadOnlyList<PriceRow> FetchPrices(
        IReadOnlyList<KeyValuePair<string, object>> filters,
        string currencyCode = DefaultCurrency)
    {
        var filterStr = BuildFilter(filters);

        // Primera URL con query string ($filter + currencyCode). NextPageLink ya trae la query embebida.
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["$filter"] = filterStr;
        qs["currencyCode"] = currencyCode;
        var url = $"{BaseUrl}?{qs}";

        var allItems = new List<PriceRow>();
        var page = 0;

        while (!string.IsNullOrEmpty(url) && page < MaxPages)
        {
            page++;
            RetailPricesResponse? data;
            try
            {
                using var response = _http.GetAsync(url).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using var stream = response.Content.ReadAsStream();
                data = JsonSerializer.Deserialize<RetailPricesResponse>(stream, JsonOptions);
            }
            catch (Exception)
            {
                // Mismo comportamiento que Python: relanzar (el repositorio captura y degrada).
                throw;
            }

            if (data?.Items is { Count: > 0 } items)
            {
                foreach (var item in items)
                    allItems.Add(MapToPriceRow(item));
            }

            url = string.IsNullOrEmpty(data?.NextPageLink) ? null : data!.NextPageLink;
        }

        return allItems;
    }

    /// <summary>Mapea un item crudo de la Retail API al <see cref="PriceRow"/> cacheable.</summary>
    private static PriceRow MapToPriceRow(RetailItem item) => new()
    {
        ServiceName = item.ServiceName,
        ProductName = item.ProductName,
        SkuName = item.SkuName,
        ArmSkuName = item.ArmSkuName,
        MeterName = item.MeterName,
        MeterId = item.MeterId,
        ArmRegionName = item.ArmRegionName,
        PriceType = item.Type, // la API lo nombra "type"; en cache es price_type
        ReservationTerm = item.ReservationTerm,
        CurrencyCode = string.IsNullOrEmpty(item.CurrencyCode) ? DefaultCurrency : item.CurrencyCode,
        UnitPrice = item.UnitPrice,
        RetailPrice = item.RetailPrice,
        UnitOfMeasure = item.UnitOfMeasure,
        TierMinimumUnits = item.TierMinimumUnits,
        IsPrimaryMeter = item.IsPrimaryMeter ?? false,
    };

    // =====================================================================================
    //  Atajos por servicio (un método por cada fetch_* de Python)
    // =====================================================================================

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchVmPrices(IReadOnlyList<string> armSkuNames, string region, string? osType = null)
    {
        // armSkuName como lista es lo más exacto para VMs.
        var items = FetchPrices(new[]
        {
            Kv("serviceName", "Virtual Machines"),
            Kv("armRegionName", region),
            Kv("armSkuName", (object)armSkuNames),
        });

        // Filtro extra por OS: la API no separa OS por campo, va en productName.
        if (!string.IsNullOrEmpty(osType))
        {
            var osLower = osType.ToLowerInvariant();
            if (osLower == "windows")
                items = items.Where(i => (i.ProductName ?? "").Contains("Windows", StringComparison.Ordinal)).ToList();
            else if (osLower == "linux")
                items = items.Where(i => !(i.ProductName ?? "").Contains("Windows", StringComparison.Ordinal)).ToList();
        }

        return items;
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchVmRegionPrices(string region) => FetchPrices(new[]
    {
        Kv("serviceName", "Virtual Machines"),
        Kv("armRegionName", region),
    });

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchManagedDiskPrices(string skuName, string region)
    {
        var items = FetchPrices(new[]
        {
            Kv("serviceName", "Storage"),
            Kv("armRegionName", region),
            Kv("skuName", skuName),
        });
        // Solo Managed Disks.
        return items.Where(i => (i.ProductName ?? "").Contains("Managed Disks", StringComparison.Ordinal)).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchSqlDbPrices(string region, string? skuName = null)
    {
        var filters = new List<KeyValuePair<string, object>>
        {
            Kv("serviceName", "SQL Database"),
            Kv("armRegionName", region),
        };
        if (!string.IsNullOrEmpty(skuName) && skuName != "Basic" && skuName != "GP_S_Gen5")
            filters.Add(Kv("skuName", skuName));
        return FetchPrices(filters);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchElasticPoolPrices(string region)
    {
        var items = FetchPrices(new[]
        {
            Kv("serviceName", "SQL Database"),
            Kv("armRegionName", region),
        });
        return items.Where(i => (i.ProductName ?? "").Contains("Elastic Pool", StringComparison.Ordinal)).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchSynapseDwPrices(string region)
    {
        var items = FetchPrices(new[]
        {
            Kv("serviceName", "Azure Synapse Analytics"),
            Kv("armRegionName", region),
        });
        return items.Where(i =>
            (i.ProductName ?? "") is "Azure Synapse Analytics Dedicated SQL Pool"
                                   or "Azure Synapse Analytics SQL Provisioned DWU").ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchSqlManagedInstancePrices(string region) => FetchPrices(new[]
    {
        Kv("serviceName", "SQL Managed Instance"),
        Kv("armRegionName", region),
    });

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchAppServicePrices(string skuName, string region, bool isLinux = false)
    {
        // Candidatos de skuName (port exacto de fetch_app_service_prices).
        var skuCandidates = new List<string> { skuName };
        if (skuName == "D1")
            skuCandidates.Add("Shared");
        if (!string.IsNullOrEmpty(skuName)
            && (skuName.ToLowerInvariant().EndsWith("v2", StringComparison.Ordinal)
                || skuName.ToLowerInvariant().EndsWith("v3", StringComparison.Ordinal))
            && !skuName.ToLowerInvariant().Contains('m'))
        {
            skuCandidates.Add(skuName[..^2] + " " + skuName[^2..]);
        }

        var items = FetchPrices(new[]
        {
            Kv("serviceName", "Azure App Service"),
            Kv("armRegionName", region),
            Kv("skuName", (object)skuCandidates),
        });

        // La Retail API devuelve algunos SKUs v3 con espacio ("P2 v3"); el recurso los reporta
        // como "P2v3". Re-normalizamos el skuName devuelto al original para que el lookup posterior
        // sea exacto.
        var candidatesSet = new HashSet<string>(skuCandidates, StringComparer.Ordinal);
        items = items
            .Select(i => candidatesSet.Contains(i.SkuName ?? "")
                ? i with { SkuName = skuName }
                : i)
            .ToList();

        // Filtrar por Linux / Windows usando productName.
        items = isLinux
            ? items.Where(i => (i.ProductName ?? "").Contains("Linux", StringComparison.Ordinal)).ToList()
            : items.Where(i => !(i.ProductName ?? "").Contains("Linux", StringComparison.Ordinal)).ToList();

        return items;
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchAppServiceRegionPrices(string region) => FetchPrices(new[]
    {
        Kv("serviceName", "Azure App Service"),
        Kv("armRegionName", region),
    });

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchFunctionsPremiumPrices(string region) => FetchPrices(new[]
    {
        Kv("serviceName", "Functions"),
        Kv("armRegionName", region),
    });

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchLogicAppsStandardPrices(string region) => FetchPrices(new[]
    {
        Kv("serviceName", "Logic Apps"),
        Kv("armRegionName", region),
    });

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchMySqlFlexPrices(string skuName, string region)
    {
        var filters = new List<KeyValuePair<string, object>>
        {
            Kv("serviceName", "Azure Database for MySQL"),
            Kv("armRegionName", region),
        };
        if (!string.IsNullOrEmpty(skuName) && skuName.StartsWith("Standard_B", StringComparison.Ordinal))
            filters.Add(Kv("armSkuName", skuName.Replace("Standard_", "").ToUpperInvariant()));

        var items = FetchPrices(filters);
        return items.Where(i => (i.ProductName ?? "").Contains("Flexible Server", StringComparison.Ordinal)).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchMySqlStoragePrice(string region)
    {
        var items = FetchPrices(new[]
        {
            Kv("serviceName", "Azure Database for MySQL"),
            Kv("armRegionName", region),
        });
        return items.Where(i =>
            (i.ProductName ?? "").Contains("Flexible Server", StringComparison.Ordinal)
            && (i.MeterName ?? "").Contains("Storage", StringComparison.Ordinal)).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchCosmosPrices(string region) => FetchPrices(new[]
    {
        Kv("serviceName", "Azure Cosmos DB"),
        Kv("armRegionName", region),
    });

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchRedisPrices(string skuName, string region) => FetchPrices(new[]
    {
        Kv("serviceName", "Redis Cache"),
        Kv("armRegionName", region),
    });

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchAzureManagedRedisPrices(string region)
    {
        var items = FetchPrices(new[]
        {
            Kv("serviceName", "Azure Cache for Redis"),
            Kv("armRegionName", region),
        });
        return items.Where(i =>
            (i.ProductName ?? "").Contains("Enterprise", StringComparison.Ordinal)
            || (i.ProductName ?? "").Contains("Managed Redis", StringComparison.Ordinal)).ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceRow> FetchPublicIpPrices(string region)
    {
        var items = FetchPrices(new[]
        {
            Kv("serviceName", "Virtual Network"),
            Kv("armRegionName", region),
        });
        return items.Where(i => (i.ProductName ?? "") == "IP Addresses").ToList();
    }

    /// <inheritdoc/>
    // Fuente: Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit
    public IReadOnlyList<PriceRow> FetchSnapshotPrices(string region)
    {
        var items = FetchPrices(new[]
        {
            Kv("serviceName", "Storage"),
            Kv("armRegionName", region),
            Kv("priceType", "Consumption"),
        });
        return items.Where(i =>
            (i.MeterName ?? "").EndsWith("Snapshots", StringComparison.Ordinal)
            && (i.ProductName ?? "").Contains("Managed Disks", StringComparison.Ordinal)).ToList();
    }

    // -------------------- Helpers --------------------

    private static KeyValuePair<string, object> Kv(string key, object value) => new(key, value);

    // -------------------- DTOs de deserialización JSON --------------------

    /// <summary>Respuesta de la Retail API: lista de items + link de la siguiente página.</summary>
    private sealed class RetailPricesResponse
    {
        [JsonPropertyName("Items")]
        public List<RetailItem>? Items { get; set; }

        [JsonPropertyName("NextPageLink")]
        public string? NextPageLink { get; set; }
    }

    /// <summary>Item crudo de la Retail API (los campos que persiste el cache).</summary>
    private sealed class RetailItem
    {
        public string? ServiceName { get; set; }
        public string? ProductName { get; set; }
        public string? SkuName { get; set; }
        public string? ArmSkuName { get; set; }
        public string? MeterName { get; set; }
        public string? MeterId { get; set; }
        public string? ArmRegionName { get; set; }

        /// <summary>La API lo nombra "type" (Consumption/Reservation).</summary>
        public string? Type { get; set; }

        public string? ReservationTerm { get; set; }
        public string? CurrencyCode { get; set; }
        public double? UnitPrice { get; set; }
        public double? RetailPrice { get; set; }
        public string? UnitOfMeasure { get; set; }
        public double? TierMinimumUnits { get; set; }
        public bool? IsPrimaryMeter { get; set; }
    }
}
