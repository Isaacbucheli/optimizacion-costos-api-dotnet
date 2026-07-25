using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// StorageFilesCalculator (service_key "storage_files", spec 2026-07-24, revisión RI híbrida
/// POR BLOQUES post-review). El corte de 10 TiB se aplica en la IMPORTACIÓN
/// (StorageFilesEnricher); aquí se costea lo insertado: Σ por tier de GiB × precio(tier,
/// redundancia, región). RI 1y/3y HÍBRIDA COMPARABLE POR BLOQUES: Azure Files Reserved Capacity
/// se compra en bloques ENTEROS de 10 TiB (10.240 GiB, <see cref="StorageFilesCalculator.ReservationBlockGib"/>)
/// — un tier con menos de un bloque se cotiza 100% PAYG aunque Azure publique tasa reservada
/// para ese tipo de tier; un tier con varios bloques reserva solo los bloques completos y el
/// remanente va a PAYG. El término (1y/3y) se emite si ALGÚN tier aportó al menos un bloque
/// reservado real. Provisioned v2 → manual_required.
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

    // -------------------- Tasas reales eastus2 LRS (fixture StorageFilesRetailFixture.md) --------------------
    // Usadas en los tests de bloques de reserva (FIX 1) para que los números sean verificables
    // contra el fixture, en vez de constantes arbitrarias. §1.1/§1.2/§1.3 (Consumption) y
    // §2.2 (Reservation, ya normalizada retailPrice ÷ meses ÷ GiB del bloque de 10 TiB).
    private const double HotPaygEastus2Lrs = 0.0255; // §1.1 Hot LRS Data Stored
    private const double HotRi1yEastus2Lrs = 2569.0 / 12 / 10240; // §2.2: 2569 ÷ 12 ÷ 10240 ≈ 0.0209066
    private const double HotRi3yEastus2Lrs = 6204.0 / 36 / 10240; // §2.2: 6204 ÷ 36 ÷ 10240 ≈ 0.0168294
    private const double CoolPaygEastus2Lrs = 0.0150; // §1.2 Cool LRS Data Stored
    private const double CoolRi1yEastus2Lrs = 1511.0 / 12 / 10240; // §2.2: 1511 ÷ 12 ÷ 10240 ≈ 0.0122966
    private const double CoolRi3yEastus2Lrs = 3650.0 / 36 / 10240; // §2.2: 3650 ÷ 36 ÷ 10240 ≈ 0.0099013
    private const double TxOptPaygEastus2Lrs = 0.06; // §1.3, idéntico eastus/eastus2, sin Reservation (§2.1 nota)

    [Fact]
    public void HotLrs_CosteaGibPorPrecio_ConRi()
    {
        // hot = 10.240 GiB = EXACTAMENTE 1 bloque de reserva (fixture §6.3): toda la capacidad
        // es reservable, SIN remanente PAYG — caso base sin la complejidad de bloques
        // parciales (ver los tests de "RiPorBloques_*"/"RiHibrida_*" para eso).
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, red) => tier == "hot" && red == "LRS"
                ? new StorageFilesPrices(0.0255, 0.02133, 0.01706, "meter-hot-lrs")
                : new StorageFilesPrices(null, null, null, null),
        };
        var row = Account(tiersJson: """{"hot":10240.0}""", billable: 10240.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Equal("storage_files", r.ServiceKey);
        Assert.Equal(10240.0 * 0.0255, r.PaygMonthly!.Value, 6);
        Assert.Equal(r.PaygMonthly, r.StorageMonthly);
        Assert.Equal(10240.0 * 0.02133, r.Ri1yMonthly!.Value, 6);
        Assert.Equal(10240.0 * 0.01706, r.Ri3yMonthly!.Value, 6);
        Assert.True(r.RiApplies);
        Assert.NotNull(r.Savings1yMonthly);
        Assert.Equal("meter-hot-lrs", r.PaygMeterId);
        Assert.Equal("calculated", r.CalculationStatus);
        Assert.Contains("No incluye transacciones", r.CalculationNotes);
        Assert.Contains("bloques de 10/100 TiB", r.CalculationNotes);
    }

    // -------------------- FIX 1: RI respeta el bloque mínimo comprable de 10 TiB --------------------

    [Fact]
    public void RiPorBloques_TierBajoUnBloque_CosteaPaygYNoCuentaComoReservable()
    {
        // hot = 3.349,27 GiB (< 10.240 = 1 bloque): Azure Files Reserved Capacity se compra en
        // bloques COMPLETOS de 10 TiB (fixture §6.3/§4.1, skuName "Hot LRS - 10 TB") — no existe
        // "reserva parcial" de un bloque. Aunque Azure SÍ publica una tasa reservada para el
        // tier hot, esta cuenta no alcanza a comprar ni un bloque → se cotiza 100% PAYG para
        // AMBOS términos, y el tier no debe contar como "reservable" en la nota.
        // Caso real del E2E (eastus2, Standard_LRS): antes se costeaba a la tasa reservada
        // ($70.02 = 3349.27 × HotRi1yEastus2Lrs) en vez de PAYG — el bug que motivó este fix.
        //   PAYG = RI 1y = RI 3y = 3349.27 × 0.0255 = 85.406385 ≈ $85.41
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier == "hot"
                ? new StorageFilesPrices(HotPaygEastus2Lrs, HotRi1yEastus2Lrs, HotRi3yEastus2Lrs, "m-hot")
                : new StorageFilesPrices(null, null, null, null),
        };
        var row = Account(tiersJson: """{"hot":3349.27}""", billable: 3349.27);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Equal(3349.27 * HotPaygEastus2Lrs, r.PaygMonthly!.Value, 6);
        Assert.False(r.RiApplies);
        Assert.Null(r.Ri1yMonthly);
        Assert.Null(r.Ri3yMonthly);
        Assert.Equal(
            "Ningún tier de este storage account alcanza el bloque mínimo de 10 TiB que "
            + "exige la reserva de Azure Files (sin un bloque completo no hay nada que comprar)",
            r.RiNotApplicableReason);
        Assert.DoesNotContain("Reserva SOLO puede cubrir", r.CalculationNotes);
    }

    [Fact]
    public void RiPorBloques_TierDe2Coma4Bloques_ReservaSoloLosBloquesCompletos()
    {
        // cool = 24.963,10 GiB = 2,4378... bloques de 10.240 GiB: Azure solo vende bloques
        // ENTEROS, así que únicamente 2 bloques (20.480 GiB) pueden reservarse; el remanente
        // (4.483,10 GiB) se cotiza PAYG. Caso real del E2E (eastus2, Standard_LRS) que antes se
        // costeaba 100% reservado (el bug de este fix).
        //   bloques = floor(24963.10 / 10240) = 2 → reservado = 20480, remanente = 4483.10
        //   RI 1y = 20480×0.0122966 (reservado) + 4483.10×0.0150 (remanente PAYG)
        //         = 251.833 + 67.247 = 319.08
        //   RI 3y = 20480×0.0099013 (reservado) + 4483.10×0.0150 (remanente PAYG)
        //         = 202.779 + 67.247 = 270.03
        //   PAYG  = 24963.10 × 0.0150 = 374.4465 (referencia; no se afirma en este test)
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier == "cool"
                ? new StorageFilesPrices(CoolPaygEastus2Lrs, CoolRi1yEastus2Lrs, CoolRi3yEastus2Lrs, "m-cool")
                : new StorageFilesPrices(null, null, null, null),
        };
        var row = Account(tiersJson: """{"cool":24963.10}""", billable: 24963.10);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        var expectedRi1 = 20480.0 * CoolRi1yEastus2Lrs + 4483.10 * CoolPaygEastus2Lrs;
        var expectedRi3 = 20480.0 * CoolRi3yEastus2Lrs + 4483.10 * CoolPaygEastus2Lrs;
        Assert.Equal(expectedRi1, r.Ri1yMonthly!.Value, 6);
        Assert.Equal(expectedRi3, r.Ri3yMonthly!.Value, 6);
        Assert.True(r.RiApplies);
        // 20480 (bloque reservable), NUNCA 24963.1 (el total del tier).
        Assert.Contains("Reserva SOLO puede cubrir hasta 20480 GiB", r.CalculationNotes);
    }

    [Fact]
    public void RiPorBloques_TodosLosTiersBajoUnBloque_SinRiConRazonDeBloqueMinimo()
    {
        // hot=3000 y cool=4000 GiB, ambos por debajo del bloque de 10.240 — aunque Azure SÍ
        // publica tasa reservada para los dos, ninguno alcanza a comprar ni un bloque. La razón
        // debe nombrar esta causa (tamaño), distinta de "Azure no publica reserva para el tier"
        // (ver RiHibrida_NingunTierConTasaReservada_SinRi_ConLaRazonDeAzureNoPublica).
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(HotPaygEastus2Lrs, HotRi1yEastus2Lrs, HotRi3yEastus2Lrs, "m-hot"),
                "cool" => new StorageFilesPrices(CoolPaygEastus2Lrs, CoolRi1yEastus2Lrs, CoolRi3yEastus2Lrs, "m-cool"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(tiersJson: """{"hot":3000.0,"cool":4000.0}""", billable: 7000.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Null(r.Ri1yMonthly);
        Assert.Null(r.Ri3yMonthly);
        Assert.False(r.RiApplies);
        Assert.Equal(
            "Ningún tier de este storage account alcanza el bloque mínimo de 10 TiB que "
            + "exige la reserva de Azure Files (sin un bloque completo no hay nada que comprar)",
            r.RiNotApplicableReason);
        Assert.DoesNotContain("Reserva SOLO puede cubrir", r.CalculationNotes);
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
    public void RiHibrida_TierConSoloUnTerminoPublicado_ElOtroTerminoQuedaNull()
    {
        // hot = 20.480 GiB = EXACTAMENTE 2 bloques (sin remanente): Azure publica su tasa 1y
        // pero, en este fixture, NO publica la tasa 3y (hueco puntual de datos — hot SÍ es un
        // tier reservable en general). cool = 4.000 GiB, bajo el bloque mínimo: nunca es
        // reservable sin importar si tiene tasa, así que siempre aporta su PAYG a ambos términos.
        //   RI 1y = 20480×0.02133 (hot, reservado, bloque completo) + 4000×0.015 (cool, PAYG)
        //         = 436.6944 + 60.00 = 496.6944
        //   RI 3y: hot no tiene tasa 3y (null) y cool no alcanza bloque → NINGÚN tier aporta una
        //   tasa reservada real para 3y → el término queda null (nada que reservar).
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(0.0255, 0.02133, null, "m-hot"),
                "cool" => new StorageFilesPrices(0.015, null, null, "m-cool"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(tiersJson: """{"hot":20480.0,"cool":4000.0}""", billable: 24480.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));
        Assert.NotNull(r.PaygMonthly);
        Assert.Equal(20480.0 * 0.02133 + 4000.0 * 0.015, r.Ri1yMonthly!.Value, 6);
        Assert.Null(r.Ri3yMonthly);
        Assert.True(r.RiApplies);
        Assert.Contains("Reserva SOLO puede cubrir hasta 20480 GiB (1 año)", r.CalculationNotes);
    }

    [Fact]
    public void RiHibrida_TierParcialmenteReservablePorBloques_MasTransactionOptimized_ComponePorTier()
    {
        // hot = 12.000 GiB = 1 bloque completo (10.240 GiB reservado) + 1.760 GiB de remanente
        // PAYG. transaction_optimized (6.000 GiB) NUNCA tiene reserva en Azure (fixture §2.1
        // nota, §5): siempre aporta su propia tasa PAYG, sin importar el tamaño. Este es el caso
        // real que motivó el cambio original (transaction_optimized ya no anula el RI completo),
        // ahora correcto a nivel de bloque: el bloque de hot se reserva, pero su remanente de
        // 1.760 GiB NO (menos de un bloque adicional).
        //   PAYG  = 12000×0.0255 (hot) + 6000×0.06 (txopt) = 306.00 + 360.00 = 666.00
        //   RI 1y = 10240×HotRi1y (bloque reservado) + 1760×0.0255 (remanente hot, PAYG)
        //                + 6000×0.06 (txopt, PAYG siempre)
        //         = (2569÷12) + 44.88 + 360.00 = 214.0833 + 44.88 + 360.00 = 618.9633
        //   RI 3y = 10240×HotRi3y (bloque reservado) + 1760×0.0255 (remanente hot, PAYG)
        //                + 6000×0.06 (txopt, PAYG siempre)
        //         = (6204÷36) + 44.88 + 360.00 = 172.3333 + 44.88 + 360.00 = 577.2133
        //   (10240×HotRi1y = 10240 × [2569/(12×10240)] = 2569÷12 EXACTO: el tamaño del bloque se
        //   cancela porque HotRi1y ya está normalizada a esa misma cantidad de GiB.)
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(HotPaygEastus2Lrs, HotRi1yEastus2Lrs, HotRi3yEastus2Lrs, "m-hot"),
                "transaction_optimized" => new StorageFilesPrices(TxOptPaygEastus2Lrs, null, null, "m-txopt"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(
            tiersJson: """{"hot":12000.0,"transaction_optimized":6000.0}""", billable: 18000.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Equal(666.00, r.PaygMonthly!.Value, 6);
        Assert.Equal(2569.0 / 12 + 1760.0 * HotPaygEastus2Lrs + 360.0, r.Ri1yMonthly!.Value, 6);
        Assert.Equal(6204.0 / 36 + 1760.0 * HotPaygEastus2Lrs + 360.0, r.Ri3yMonthly!.Value, 6);
        Assert.True(r.RiApplies);
        Assert.NotNull(r.Savings1yMonthly);
        Assert.Contains("Reserva SOLO puede cubrir hasta 10240 GiB", r.CalculationNotes);
    }

    [Fact]
    public void RiHibrida_NingunTierConTasaReservada_SinRi_ConLaRazonDeAzureNoPublica()
    {
        // hot alcanza el bloque mínimo (12.000 ≥ 10.240) pero, en este fixture, Azure no publica
        // ninguna tasa reservada para él (hueco de datos); transaction_optimized nunca tiene
        // reserva. Como AL MENOS UN tier sí alcanzó el bloque (solo le faltó la tasa), la causa
        // es "Azure no publica reserva para estos tiers" — DISTINTA de "ningún tier alcanza el
        // bloque mínimo" (ver RiPorBloques_TodosLosTiersBajoUnBloque_SinRiConRazonDeBloqueMinimo).
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(HotPaygEastus2Lrs, null, null, "m-hot"),
                "transaction_optimized" => new StorageFilesPrices(TxOptPaygEastus2Lrs, null, null, "m-txopt"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(
            tiersJson: """{"hot":12000.0,"transaction_optimized":6000.0}""", billable: 18000.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Null(r.Ri1yMonthly);
        Assert.Null(r.Ri3yMonthly);
        Assert.False(r.RiApplies);
        Assert.Equal(
            "Ningún tier de este storage account tiene capacidad reservada en Azure "
            + "(transaction optimized no la soporta)",
            r.RiNotApplicableReason);
        Assert.DoesNotContain("Reserva SOLO puede cubrir", r.CalculationNotes);
    }

    [Fact]
    public void RiHibrida_TodosLosTiersEnBloquesExactos_SumaIgualQueLaSumaLineal()
    {
        // hot y cool son EXACTAMENTE 1 bloque cada uno (10.240 GiB, sin remanente): con bloques
        // exactos la fórmula híbrida coincide con la suma lineal de siempre (no hay PAYG de
        // relleno porque no hay remanente). Antes de este fix esto era cierto para CUALQUIER
        // tamaño; ahora solo es cierto cuando el tamaño es un múltiplo exacto del bloque de 10 TiB.
        //   PAYG  = 10240×0.0255 + 10240×0.015  = 261.12  + 153.60  = 414.72
        //   RI 1y = 10240×0.0213 + 10240×0.0114 = 218.112 + 116.736 = 334.848
        //   RI 3y = 10240×0.0171 + 10240×0.0091 = 175.104 + 93.184  = 268.288
        var prices = new FakePriceRepository
        {
            GetStorageFilesPricesFn = (_, tier, _) => tier switch
            {
                "hot" => new StorageFilesPrices(0.0255, 0.0213, 0.0171, "m-hot"),
                "cool" => new StorageFilesPrices(0.015, 0.0114, 0.0091, "m-cool"),
                _ => new StorageFilesPrices(null, null, null, null),
            },
        };
        var row = Account(tiersJson: """{"hot":10240.0,"cool":10240.0}""", billable: 20480.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Equal(414.72, r.PaygMonthly!.Value, 6);
        Assert.Equal(334.848, r.Ri1yMonthly!.Value, 6);
        Assert.Equal(268.288, r.Ri3yMonthly!.Value, 6);
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

    // -------------------- FIX 2: una tasa reservada de $0 no debe fabricar ~100% de ahorro --------------------

    [Fact]
    public void RiEnCero_TratadaComoNoDisponible_NuncaFabricaAhorroDelCienPorCiento()
    {
        // Defensa en profundidad simétrica a PrecioEnCero_TratadoComoFaltante: las ramas RI
        // comparaban "p.Ri1yPerGbMonth is not null" (un 0.0 explícito pasaba esa guarda), a
        // diferencia de la rama PAYG que ya exigía "is null or <= 0". Si el repositorio alguna
        // vez entrega Ri1y/Ri3y == 0 (en vez de null), antes esto habría dado ri1 = gib×0 = 0
        // con RiApplies = true → ~100% de ahorro fabricado. Ahora "is > 0" rechaza el cero igual
        // que rechaza null: el tier se cotiza 100% PAYG, sin importar que alcance el bloque.
        var prices = new FakePriceRepository
        {
            // hot = 10.240 GiB (Account() default) = exactamente 1 bloque, para aislar esta
            // guarda del bloqueo por tamaño de FIX 1 (RiPorBloques_TierBajoUnBloque_...).
            GetStorageFilesPricesFn = (_, _, _) => new StorageFilesPrices(0.0255, 0.0, 0.0, "m-ri-cero"),
        };
        var row = Account(tiersJson: """{"hot":10240.0}""", billable: 10240.0);

        var r = Assert.Single(NewCalc(prices).Calculate(Res.Rows(row), 99));

        Assert.Null(r.Ri1yMonthly);
        Assert.Null(r.Ri3yMonthly);
        Assert.False(r.RiApplies);
        Assert.Equal("calculated", r.CalculationStatus); // el PAYG sí es válido, solo se rechaza la RI
        Assert.Equal(10240.0 * 0.0255, r.PaygMonthly!.Value, 6);
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
