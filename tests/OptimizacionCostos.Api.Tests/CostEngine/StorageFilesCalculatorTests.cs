using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// StorageFilesCalculator (service_key "storage_files", spec 2026-07-24, revisión RI híbrida).
/// El corte de 10 TiB se aplica en la IMPORTACIÓN (StorageFilesEnricher); aquí se costea lo
/// insertado: Σ por tier de GiB × precio(tier, redundancia, región). RI 1y/3y HÍBRIDA
/// COMPARABLE: por término, cada tier aporta su tasa reservada si la tiene o su tasa PAYG si no
/// (transaction_optimized nunca tiene reserva en Azure); el término se emite si ALGÚN tier
/// aportó una tasa reservada real. Provisioned v2 → manual_required.
/// </summary>
public sealed class StorageFilesCalculatorTests
{
    private static StorageFilesCalculator NewCalc(FakePriceRepository prices)
        => new(prices, new FakePricingConstants());

    private static ResourceRow Account(
        string sku = "Standard_LRS", string kind = "StorageV2", string? tiersJson = null,
        double billable = 15000.0, int id = 1)
        => Res.Row(
            ("resource_id", id), ("resource_name", "stgcliente01"), ("files_sku", sku),
            ("kind", kind), ("share_count", 3), ("used_gib", 14000.0),
            ("provisioned_gib", 20480.0), ("billable_gib", billable),
            ("tier_breakdown_json", tiersJson ?? """{"hot":15000.0}"""),
            ("location", "eastus"));

    [Fact]
    public void HotLrs_CosteaGibPorPrecio_ConRi()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, red) => tier == "hot" && red == "LRS"
                ? new StorageFilesPrices(0.0255, 0.02133, 0.01706, "meter-hot-lrs")
                : new StorageFilesPrices(null, null, null, null),
        };

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(Account()), 99));

        Assert.Equal("storage_files", r.ServiceKey);
        Assert.Equal(15000.0 * 0.0255, r.PaygMonthly!.Value, 6);
        Assert.Equal(r.PaygMonthly, r.StorageMonthly);
        Assert.Equal(15000.0 * 0.02133, r.Ri1yMonthly!.Value, 6);
        Assert.Equal(15000.0 * 0.01706, r.Ri3yMonthly!.Value, 6);
        Assert.True(r.RiApplies);
        Assert.NotNull(r.Savings1yMonthly);
        Assert.Equal("meter-hot-lrs", r.PaygMeterId);
        Assert.Equal("calculated", r.CalculationStatus);
        Assert.Contains("No incluye transacciones", r.CalculationNotes);
        Assert.Contains("bloques de 10/100 TiB", r.CalculationNotes);
    }

    [Fact]
    public void MezclaDeTiers_SumaCadaUnoConSuPrecio()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(0.0255, null, null, "m-hot"),
                "cool" => new StorageFilesPrices(0.015, null, null, "m-cool"),
                "transaction_optimized" => new StorageFilesPrices(0.06, null, null, "m-txopt"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(tiersJson: """{"hot":8000.0,"cool":4000.0,"transaction_optimized":3000.0}""");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Equal(8000 * 0.0255 + 4000 * 0.015 + 3000 * 0.06, r.PaygMonthly!.Value, 6);
        // Ningún tier tiene RI publicada → sin RI, con razón.
        Assert.False(r.RiApplies);
        Assert.Null(r.Ri1yMonthly);
    }

    [Fact]
    public void RiHibrida_TierSinReservaUsaSuPropioPayg_ElTerminoConDatosSeEmite()
    {
        // hot tiene RI 1y publicada; cool no (hueco puntual de datos, cool SÍ es un tier
        // reservable en general). Regla híbrida: el término 1y se compone tier por tier —
        // hot a su tasa reservada, cool a su PAYG (nunca se descarta por "incompleto"; antes de
        // este cambio esto habría anulado el RI 1y completo).
        //   RI 1y = 8000×0.0213 (hot, reservado) + 4000×0.015 (cool, PAYG) = 170.40 + 60.00 = 230.40
        // Ningún tier tiene RI 3y publicada → el término 3y queda null (nada que reservar).
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(0.0255, 0.0213, null, "m-hot"),
                "cool" => new StorageFilesPrices(0.015, null, null, "m-cool"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(tiersJson: """{"hot":8000.0,"cool":4000.0}""", billable: 12000.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.NotNull(r.PaygMonthly);
        Assert.Equal(230.40, r.Ri1yMonthly!.Value, 6);
        Assert.Null(r.Ri3yMonthly);
        Assert.True(r.RiApplies);
        Assert.Contains("Reserva aplicable a 8000 GiB de 12000 GiB", r.CalculationNotes);
    }

    [Fact]
    public void RiHibrida_TierReservableMasTransactionOptimized_ComponePorTier()
    {
        // hot (reservable) + transaction_optimized (SIN reserva en Azure, spec 2026-07-24): el
        // término se compone tier por tier — reserva donde existe, PAYG donde no — y SE EMITE
        // porque hot sí aportó una tasa reservada real. Este es el caso real que motivó el
        // cambio: antes, transaction_optimized (el tier DEFAULT de shares sin accessTier
        // explícito) anulaba el RI completo aunque el resto de la cuenta fuera reservable.
        //   PAYG  = 8000×0.0255              + 6000×0.06        = 204.00  + 360.00  = 564.00
        //   RI 1y = 8000×0.02133 (hot, resv.) + 6000×0.06 (txopt, PAYG) = 170.64 + 360.00 = 530.64
        //   RI 3y = 8000×0.01706 (hot, resv.) + 6000×0.06 (txopt, PAYG) = 136.48 + 360.00 = 496.48
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(0.0255, 0.02133, 0.01706, "m-hot"),
                "transaction_optimized" => new StorageFilesPrices(0.06, null, null, "m-txopt"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(
            tiersJson: """{"hot":8000.0,"transaction_optimized":6000.0}""", billable: 14000.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Equal(564.00, r.PaygMonthly!.Value, 6);
        Assert.Equal(530.64, r.Ri1yMonthly!.Value, 6);
        Assert.Equal(496.48, r.Ri3yMonthly!.Value, 6);
        Assert.True(r.RiApplies);
        Assert.NotNull(r.Savings1yMonthly);
        Assert.Contains("Reserva aplicable a 8000 GiB de 14000 GiB", r.CalculationNotes);
    }

    [Fact]
    public void RiHibrida_NingunTierReservable_SinRi_ConLaNuevaRazonPrecisa()
    {
        // Ni hot ni transaction_optimized aportan una tasa reservada (en este fixture hot
        // tampoco la tiene) → ningún término se emite; la razón ya NO dice "faltan precios para
        // todos los tiers" (eso ahora es el caso común y esperado), sino que ningún tier de la
        // cuenta tiene capacidad reservada en Azure.
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(0.0255, null, null, "m-hot"),
                "transaction_optimized" => new StorageFilesPrices(0.06, null, null, "m-txopt"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(
            tiersJson: """{"hot":8000.0,"transaction_optimized":6000.0}""", billable: 14000.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Null(r.Ri1yMonthly);
        Assert.Null(r.Ri3yMonthly);
        Assert.False(r.RiApplies);
        Assert.Equal(
            "Ningún tier de este storage account tiene capacidad reservada en Azure "
            + "(transaction optimized no la soporta)",
            r.RiNotApplicableReason);
        Assert.DoesNotContain("Reserva aplicable", r.CalculationNotes);
    }

    [Fact]
    public void RiHibrida_TodosLosTiersConReserva_SumaIgualQueAntesDelCambio()
    {
        // Regresión: cuando TODOS los tiers con capacidad tienen reserva publicada para ambos
        // términos, la fórmula híbrida coincide con la suma lineal de siempre (no hay PAYG de
        // relleno porque no hay huecos).
        //   PAYG  = 8000×0.0255  + 4000×0.015  = 204.00 + 60.00 = 264.00
        //   RI 1y = 8000×0.0213  + 4000×0.0114 = 170.40 + 45.60 = 216.00
        //   RI 3y = 8000×0.0171  + 4000×0.0091 = 136.80 + 36.40 = 173.20
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(0.0255, 0.0213, 0.0171, "m-hot"),
                "cool" => new StorageFilesPrices(0.015, 0.0114, 0.0091, "m-cool"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(tiersJson: """{"hot":8000.0,"cool":4000.0}""", billable: 12000.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Equal(264.00, r.PaygMonthly!.Value, 6);
        Assert.Equal(216.00, r.Ri1yMonthly!.Value, 6);
        Assert.Equal(173.20, r.Ri3yMonthly!.Value, 6);
        Assert.True(r.RiApplies);
    }

    [Fact]
    public void Premium_UsaTierPremiumYRedundanciaDelSku()
    {
        var seen = new List<(string Tier, string Red)>();
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, red) =>
            {
                seen.Add((tier, red));
                return new StorageFilesPrices(0.16, null, null, "m-prem");
            },
        };
        var row = Account(sku: "Premium_ZRS", kind: "FileStorage",
            tiersJson: """{"premium":12000.0}""", billable: 12000.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal(12000.0 * 0.16, r.PaygMonthly!.Value, 6);
        Assert.Equal(("premium", "ZRS"), Assert.Single(seen));
    }

    [Fact]
    public void RedundanciaRaGrs_FacturaComoGrs()
    {
        // Fixture §5: RA-GRS/RA-GZRS no tienen meter propio en Azure Files, se facturan
        // bajo el meter GRS/GZRS — el calculador debe mapear antes de consultar precios.
        string? seenRed = null;
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, red) => { seenRed = red; return new StorageFilesPrices(0.04, null, null, null); },
        };
        NewCalc(prices).Calculate(Res.Rows(Account(sku: "Standard_RAGRS")), 99);
        Assert.Equal("GRS", seenRed);
    }

    [Fact]
    public void ProvisionedV2_ManualRequired_SinNumeros()
    {
        var prices = new FakePriceRepository();
        var row = Account(sku: "StandardV2_LRS");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal("manual_required", r.CalculationStatus);
        Assert.Null(r.PaygMonthly);
        Assert.Contains("provisioned v2", r.CalculationNotes);
    }

    [Fact]
    public void TierSinPrecio_PriceNotFound_SinSumaParcial()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier == "hot"
                ? new StorageFilesPrices(0.0255, null, null, "m-hot")
                : new StorageFilesPrices(null, null, null, null),
        };
        var row = Account(tiersJson: """{"hot":8000.0,"cool":4000.0}""");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
        Assert.Null(r.PaygMonthly);
        Assert.Contains("cool", r.CalculationNotes);
    }

    [Fact]
    public void SinDesglose_PriceNotFound()
    {
        var prices = new FakePriceRepository();
        var row = Account(tiersJson: """{}""");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
    }

    [Fact]
    public void RiMayorQuePayg_SeDescarta()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, _) => new StorageFilesPrices(0.0255, 0.99, 0.99, "m"),
        };

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(Account()), 99));
        Assert.Null(r.Ri1yMonthly);
        Assert.False(r.RiApplies);
        Assert.Contains("RI descartada", r.CalculationNotes);
    }

    [Fact]
    public void DesgloseConTiersEnCero_PriceNotFound_NuncaCeroSilencioso()
    {
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, _) => new StorageFilesPrices(0.0255, 0.0213, 0.017, "m"),
        };
        var row = Account(tiersJson: """{"hot":0.0,"cool":0.0}""");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
        Assert.Null(r.PaygMonthly);
        Assert.Null(r.Ri1yMonthly);
        Assert.False(r.RiApplies);
    }

    // -------------------- Change 2: nunca aceptar un precio $0.00 --------------------

    [Fact]
    public void PrecioEnCero_TratadoComoFaltante_NuncaCalculatedConCero()
    {
        // Defensa en profundidad: si el repositorio alguna vez entrega PricePerGbMonth == 0
        // (en vez de null), el calculador lo trata igual que "no encontrado" — jamás emite un
        // payg_monthly de $0 con estado "calculated".
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, _) => new StorageFilesPrices(0.0, null, null, "m-zero"),
        };

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(Account()), 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
        Assert.Null(r.PaygMonthly);
    }

    // -------------------- Change 2: la selección IA-asistida se anota --------------------

    [Fact]
    public void MatchStrategyIaAsistida_SeSurfaceaEnLasNotas()
    {
        // Convención de las calculadoras hermanas (Redis/AppService/SqlMI/Vm): el precio elegido
        // por el asistente IA (MatchStrategy "assist_match:...") se propaga a CalculationNotes
        // para que CostLabels.PriceOrigin lo detecte y el Excel muestre "IA asistida" en vez de
        // "Exacto".
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, _) =>
                new StorageFilesPrices(0.0255, null, null, "m-hot", "assist_match:data_stored", 0.82),
        };

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(Account()), 99));
        Assert.Contains("assist_match:data_stored", r.CalculationNotes);
    }

    [Fact]
    public void SinIa_LasNotasDicenMatchDeterministic()
    {
        var r = Assert.Single(NewCalc(new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, red) => tier == "hot" && red == "LRS"
                ? new StorageFilesPrices(0.0255, 0.02133, 0.01706, "meter-hot-lrs")
                : new StorageFilesPrices(null, null, null, null),
        }).Calculate(Res.Rows(Account()), 99));

        Assert.Contains("match=deterministic", r.CalculationNotes);
    }

    // -------------------- Change 3: memoización por invocación --------------------

    [Fact]
    public void MismaRegionTierYRedundancia_ConsultaElRepositorioUnaSolaVez()
    {
        var calls = 0;
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, _) =>
            {
                calls++;
                return new StorageFilesPrices(0.0255, null, null, "m-hot");
            },
        };
        // Tres cuentas con el mismo (region="eastus", tier="hot", redundancy="LRS") — Account()
        // default no cambia esos campos entre id 1/2/3.
        var resources = Res.Rows(Account(id: 1), Account(id: 2), Account(id: 3));

        NewCalc(prices).Calculate(resources, 99);

        Assert.Equal(1, calls);
    }

    // -------------------- Change 4: redundancia desconocida --------------------

    [Fact]
    public void RedundanciaDesconocida_PriceNotFound_NuncaAsumeLrs()
    {
        // Antes: sufijo no reconocido → asumía LRS (la redundancia MÁS BARATA); ahora falla
        // explícito (GZRS es ~2.3x LRS: asumir LRS sería sub-costeo silencioso).
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, _, _) => new StorageFilesPrices(0.0255, null, null, "m"),
        };
        var row = Account(sku: "Standard_XYZ");

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.Equal("price_not_found", r.CalculationStatus);
        Assert.Null(r.PaygMonthly);
        Assert.Contains("Redundancia", r.CalculationNotes);
    }

    [Theory]
    [InlineData("Standard_LRS", "LRS")]
    [InlineData("Standard_ZRS", "ZRS")]
    [InlineData("Standard_GRS", "GRS")]
    [InlineData("Standard_RAGRS", "GRS")]
    [InlineData("Premium_ZRS", "ZRS")]
    [InlineData("Premium_GZRS", "GZRS")]
    [InlineData("Standard_RAGZRS", "GZRS")]
    [InlineData("Standard_XYZ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RedundancyToken_MapeaSufijosConocidos_NullSiNoLoReconoce(string? sku, string? expected)
        => Assert.Equal(expected, StorageFilesCalculator.RedundancyToken(sku));
}
