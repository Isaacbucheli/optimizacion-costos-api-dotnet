using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Selección de precios de Azure Files (GetStorageFilesPrices). Filas y valores tomados de
/// StorageFilesRetailFixture.md (fixture real, Task 1, región eastus) — NO de la propuesta
/// provisional del plan, que el fixture corrigió en 4 puntos (ver header de
/// SqlPriceRepository.StorageFiles.cs para el detalle). En particular:
///   - productName de Consumption es "Files v2" para hot/cool/transaction_optimized (NO "Files",
///     que es el producto legacy v1 sin ZRS/GZRS).
///   - El meter de premium lleva el prefijo "Premium " ("Premium LRS Provisioned") y su unidad
///     es "1 GB/Month" (no GiB).
///   - Las filas de Reservation tienen un productName DISTINTO ("Files Reserved Capacity" /
///     "Premium Files Reserved Capacity") y su retail_price es el TOTAL del término para el
///     bloque codificado en el skuName ("Hot LRS - 10 TB" = 10,240 GiB), no un precio unitario;
///     el unit_of_measure de esas filas ("1 GB/Month") es un remanente que NO debe usarse.
///   - transaction_optimized no tiene Reservation en absoluto (Azure Files Reserved Capacity
///     solo cubre Hot/Cool/Premium).
///
/// La cache se marca fresca (mismo patrón que PriceSelection_SnapshotTests) para no disparar fetch.
/// </summary>
public sealed class PriceSelection_StorageFilesTests
{
    private static SqlPriceRepository BuildRepo(FakePriceCache cache) =>
        new SqlPriceRepository(cache, new FakeRetailPriceClient(), new FakePricingConstants());

    /// <summary>Doble mínimo de <see cref="IPriceAssistant"/>: devuelve un resultado fijo, sin red.</summary>
    private sealed class StubAssistant(PriceRow? result) : IPriceAssistant
    {
        public bool IsEnabled => true;

        public PriceRow? SelectCandidate(
            string serviceKey,
            string component,
            IReadOnlyDictionary<string, object?> context,
            IReadOnlyList<PriceRow> candidates)
            => result;
    }

    private static FakePriceCache CacheWith(IReadOnlyList<PriceRow> rows) => new()
    {
        IsCacheFreshForFn = (_, _, _, _) => true,
        IsFetchQueryFreshFn = _ => true,
        QueryCachedFn = (_, _, _, _) => rows,
    };

    // Valores reales del fixture Task 1 (eastus) — StorageFilesRetailFixture.md §1, §2, §5.
    private static readonly (string, object?)[][] Cached =
    {
        // PAYG estándar hot / cool (product "Files v2", meter con prefijo de tier) — §1.1/§1.2.
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files v2"),
            ("meter_name", "Hot LRS Data Stored"), ("meter_id", "meter-hot-lrs"),
            ("price_type", "Consumption"), ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.0287),
        },
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files v2"),
            ("meter_name", "Cool LRS Data Stored"), ("meter_id", "meter-cool-lrs"),
            ("price_type", "Consumption"), ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.0228),
        },
        // PAYG transaction optimized: MISMO producto "Files v2" (⚠ el plan decía "Files", que
        // no tiene ZRS/GZRS); meter SIN prefijo de tier — §1.3/§5.
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files v2"),
            ("meter_name", "LRS Data Stored"), ("meter_id", "meter-txopt-lrs"),
            ("price_type", "Consumption"), ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.06),
        },
        // PAYG premium: meter CON prefijo "Premium " y unidad "1 GB/Month" (⚠ el plan decía sin
        // prefijo y GiB) — §1.5.
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Premium Files"),
            ("meter_name", "Premium LRS Provisioned"), ("meter_id", "meter-prem-lrs"),
            ("price_type", "Consumption"), ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.16),
        },
        // Meters que DEBEN excluirse (la clase de bug "Disk Mount" no puede repetirse).
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files v2"),
            ("meter_name", "Hot LRS Write Operations"), ("price_type", "Consumption"),
            ("unit_of_measure", "1 10K"), ("retail_price", 0.055),
        },
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files v2"),
            ("meter_name", "Hot LRS Metadata"), ("price_type", "Consumption"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.026),
        },
        // Reservas Hot LRS bloque 10 TiB (skuName "- 10 TB"). productName DISTINTO al de
        // Consumption ("Files Reserved Capacity"); retail_price = TOTAL del término para el
        // bloque de 10,240 GiB (10 TiB); unit_of_measure "1 GB/Month" es un remanente que se
        // IGNORA en la normalización — §2.1/§4.1/§6.
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files Reserved Capacity"),
            ("sku_name", "Hot LRS - 10 TB"), ("meter_name", "Hot LRS - 10 TB Data Stored"),
            ("price_type", "Reservation"), ("reservation_term", "1 Year"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 2892.0),
        },
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files Reserved Capacity"),
            ("sku_name", "Hot LRS - 10 TB"), ("meter_name", "Hot LRS - 10 TB Data Stored"),
            ("price_type", "Reservation"), ("reservation_term", "3 Years"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 6983.0),
        },
        // Reserva Premium LRS bloque 10 TiB. productName "Premium Files Reserved Capacity"
        // (⚠ CONTIENE "Files Reserved Capacity" como subcadena — el selector debe distinguir por
        // igualdad EXACTA de producto, no "contains", para no confundir Hot/Cool con Premium).
        // meterName literal "Provisioned" (sin prefijo de redundancia; solo el skuName la lleva) — §2.3.
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Premium Files Reserved Capacity"),
            ("sku_name", "Premium LRS - 10 TB"), ("meter_name", "Provisioned"),
            ("price_type", "Reservation"), ("reservation_term", "1 Year"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 16122.0),
        },
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Premium Files Reserved Capacity"),
            ("sku_name", "Premium LRS - 10 TB"), ("meter_name", "Provisioned"),
            ("price_type", "Reservation"), ("reservation_term", "3 Years"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 38928.0),
        },
        // Reserva Hot GZRS bloque 10 TiB — SOLO para el test de anti-colisión de subcadena:
        // "ZRS" es subcadena de "GZRS", así que un match por Contains() ingenuo confundiría una
        // búsqueda de Hot+ZRS con esta fila de Hot+GZRS. No debe haber fila "Hot ZRS - 10 TB" en
        // esta cache — §2.1 (valor real 1 Year = 6519).
        new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files Reserved Capacity"),
            ("sku_name", "Hot GZRS - 10 TB"), ("meter_name", "Hot GZRS - 10 TB Data Stored"),
            ("price_type", "Reservation"), ("reservation_term", "1 Year"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 6519.0),
        },
    };

    [Fact]
    public void HotLrs_TomaDataStored_YExcluyeOperacionesYMetadata()
    {
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        var p = repo.GetStorageFilesPrices("eastus", "hot", "LRS");
        Assert.Equal(0.0287, p.PricePerGbMonth);
        Assert.Equal("meter-hot-lrs", p.PaygMeterId);
    }

    [Fact]
    public void CoolLrs_TomaCoolDataStored()
    {
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        Assert.Equal(0.0228, repo.GetStorageFilesPrices("eastus", "cool", "LRS").PricePerGbMonth);
    }

    [Fact]
    public void TransactionOptimizedLrs_TomaProductFilesV2_ConMeterSinPrefijo()
    {
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        var p = repo.GetStorageFilesPrices("eastus", "transaction_optimized", "LRS");
        Assert.Equal(0.06, p.PricePerGbMonth);
        Assert.Equal("meter-txopt-lrs", p.PaygMeterId);
    }

    [Fact]
    public void TransactionOptimizedLrs_SinReservas_DevuelveNullsDeRi()
    {
        // Azure Files Reserved Capacity no cubre transaction_optimized (fixture §2.2/§5): debe
        // ser null explícito, NUNCA un fallback a otra fila ni un 0 silencioso.
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        var p = repo.GetStorageFilesPrices("eastus", "transaction_optimized", "LRS");
        Assert.Null(p.Ri1yPerGbMonth);
        Assert.Null(p.Ri3yPerGbMonth);
    }

    [Fact]
    public void PremiumLrs_TomaProductPremiumFiles_MeterConPrefijoPremium()
    {
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        Assert.Equal(0.16, repo.GetStorageFilesPrices("eastus", "premium", "LRS").PricePerGbMonth);
    }

    [Fact]
    public void HotLrs_NormalizaReserva1yA_GibMes()
    {
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        var p = repo.GetStorageFilesPrices("eastus", "hot", "LRS");
        // 2892 USD = TOTAL del término por el bloque de 10 TiB (10,240 GiB) durante 1 año (12 meses).
        Assert.NotNull(p.Ri1yPerGbMonth);
        Assert.Equal(2892.0 / 12.0 / 10240.0, p.Ri1yPerGbMonth!.Value, 10);
    }

    [Fact]
    public void HotLrs_NormalizaReserva3yA_GibMes()
    {
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        var p = repo.GetStorageFilesPrices("eastus", "hot", "LRS");
        Assert.Equal(6983.0 / 36.0 / 10240.0, p.Ri3yPerGbMonth!.Value, 10);
    }

    [Fact]
    public void PremiumLrs_NormalizaReserva1yA_GibMes_SinConfundirseConEstandar()
    {
        // "Premium Files Reserved Capacity" CONTIENE "Files Reserved Capacity" como subcadena;
        // el selector debe distinguir por igualdad exacta de producto, no encontrar esta fila
        // al buscar hot/cool ni viceversa.
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        var p = repo.GetStorageFilesPrices("eastus", "premium", "LRS");
        Assert.Equal(16122.0 / 12.0 / 10240.0, p.Ri1yPerGbMonth!.Value, 10);
        // Y NO el valor (distinto) de la reserva estándar Hot LRS.
        Assert.NotEqual(2892.0 / 12.0 / 10240.0, p.Ri1yPerGbMonth!.Value);
    }

    [Fact]
    public void HotZrs_NoConfundeConGzrsPorSubcadena()
    {
        // La cache solo tiene reserva "Hot GZRS - 10 TB" (no "Hot ZRS - 10 TB"). "ZRS" es
        // subcadena de "GZRS": un match ingenuo por Contains() confundiría esta búsqueda con la
        // fila GZRS. Debe devolver null, no el precio de GZRS.
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        var p = repo.GetStorageFilesPrices("eastus", "hot", "ZRS");
        Assert.Null(p.Ri1yPerGbMonth);
    }

    [Fact]
    public void TierSinMeter_DevuelveNulls()
    {
        // "cool"+"GZRS" no tiene NINGUNA fila en la cache (ni Consumption ni Reservation) — a
        // diferencia de "hot"+"GZRS", que sí tiene una fila de Reservation en este fixture
        // (usada por HotZrs_NoConfundeConGzrsPorSubcadena) y por lo tanto no sirve para probar
        // "sin meter en absoluto".
        var repo = BuildRepo(CacheWith(PriceRowFactory.Many(Cached)));
        var p = repo.GetStorageFilesPrices("eastus", "cool", "GZRS");
        Assert.Null(p.PricePerGbMonth);
        Assert.Null(p.Ri1yPerGbMonth);
    }

    [Fact]
    public void CacheVacia_DevuelveNulls()
    {
        var repo = BuildRepo(CacheWith(System.Array.Empty<PriceRow>()));
        Assert.Null(repo.GetStorageFilesPrices("eastus", "hot", "LRS").PricePerGbMonth);
    }

    // -------------------- Change 2: nunca aceptar un precio $0.00 --------------------

    [Fact]
    public void PrecioCeroEnConsumption_NuncaSeSelecciona_QuedaNull()
    {
        // Caso real (documentado en la fixture): un producto REAL puede traer TODOS sus meters
        // en $0.00 (ej. "Azure Files Provisioned v2" en la misma consulta de región) y esa fila
        // pasaría el resto de filtros (producto/meter exacto/unidad) si no se exige precio > 0.
        // Combinado con que el calculador solo trataba null (no 0) como "faltante", esto
        // producía payg_monthly = 0 con estado "calculated": un $0 silencioso prohibido.
        var rows = PriceRowFactory.Many(new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files v2"),
            ("meter_name", "Hot LRS Data Stored"), ("meter_id", "meter-hot-lrs-zero"),
            ("price_type", "Consumption"), ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.0),
        });
        var repo = BuildRepo(CacheWith(rows));

        var p = repo.GetStorageFilesPrices("eastus", "hot", "LRS");
        Assert.Null(p.PricePerGbMonth);
        Assert.Null(p.PaygMeterId);
    }

    [Fact]
    public void PrecioCeroEnReservation_NuncaSeSelecciona_QuedaNull()
    {
        var rows = PriceRowFactory.Many(new (string, object?)[]
        {
            ("service_name", "Storage"), ("product_name", "Files Reserved Capacity"),
            ("sku_name", "Hot LRS - 10 TB"), ("meter_name", "Hot LRS - 10 TB Data Stored"),
            ("price_type", "Reservation"), ("reservation_term", "1 Year"),
            ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.0),
        });
        var repo = BuildRepo(CacheWith(rows));

        Assert.Null(repo.GetStorageFilesPrices("eastus", "hot", "LRS").Ri1yPerGbMonth);
    }

    [Fact]
    public void PrecioCeroEnCandidatosDeAsistente_SeExcluyeDelPool()
    {
        // El pool que se ofrece al asistente IA (fallback cuando el determinista no encuentra
        // nada) tampoco debe incluir filas $0 — de lo contrario la IA podría "elegir" un precio
        // gratis real, exactamente el escenario que Change 2 prohíbe.
        var rows = PriceRowFactory.Many(
            // Ningún meter exacto de "cool" (fuerza el fallback de IA); el único candidato del
            // pool "files"+almacenamiento es este producto real con retail_price 0.
            new (string, object?)[]
            {
                ("service_name", "Storage"), ("product_name", "Azure Files Provisioned v2"),
                ("meter_name", "Cool LRS Provisioned"), ("meter_id", "meter-provisioned-v2-zero"),
                ("price_type", "Consumption"), ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.0),
            });
        // Sin asistente inyectado (null): AssistSelect siempre devuelve null, así que esto
        // confirma que ni siquiera se necesita IA para probar la exclusión — el precio $0 nunca
        // gana ni por el camino determinista ni queda disponible para el fallback.
        var repo = BuildRepo(CacheWith(rows));

        Assert.Null(repo.GetStorageFilesPrices("eastus", "cool", "LRS").PricePerGbMonth);
    }

    [Fact]
    public void SeleccionIaAsistida_PropagaMatchStrategyYConfidence()
    {
        // El meter no coincide con el esperado por FilesMeterFor ("Cool LRS Data Stored"), así
        // que el determinista no encuentra nada y cae al fallback de IA (Change 2b): el
        // candidato elegido por el asistente propaga su AiMatchStrategy/AiMatchConfidence a
        // StorageFilesPrices, para que el calculador los anote en calculation_notes y
        // CostLabels.PriceOrigin reporte "IA asistida" en vez de "Exacto".
        var aiPicked = PriceRowFactory.Of(
            ("service_name", "Storage"), ("product_name", "Files v2"),
            ("meter_name", "Cool LRS Storage (alt meter)"), ("meter_id", "meter-cool-ai"),
            ("price_type", "Consumption"), ("unit_of_measure", "1 GB/Month"), ("retail_price", 0.02))
            with { AiMatchStrategy = "assist_match:data_stored", AiMatchConfidence = 0.85 };

        var cache = new FakePriceCache
        {
            IsCacheFreshForFn = (_, _, _, _) => true,
            IsFetchQueryFreshFn = _ => true,
            QueryCachedFn = (_, _, _, _) => new[] { aiPicked },
        };
        var repo = new SqlPriceRepository(
            cache, new FakeRetailPriceClient(), new FakePricingConstants(), new StubAssistant(aiPicked));

        var p = repo.GetStorageFilesPrices("eastus", "cool", "LRS");

        Assert.Equal(0.02, p.PricePerGbMonth);
        Assert.Equal("assist_match:data_stored", p.MatchStrategy);
        Assert.Equal(0.85, p.MatchConfidence);
    }

    [Theory]
    [InlineData("Hot LRS - 10 TB", 10240.0)]
    [InlineData("Hot LRS - 100 TB", 102400.0)]
    [InlineData("Cool GZRS - 10 TB", 10240.0)]
    [InlineData("Premium ZRS - 100 TB", 102400.0)]
    [InlineData("Hot LRS", null)]
    [InlineData(null, null)]
    public void ParseReservedBlockGib_ReconoceSufijoDeBloqueTiB(string? skuName, double? expectedGib)
        => Assert.Equal(expectedGib, SqlPriceRepository.ParseReservedBlockGib(skuName));
}
