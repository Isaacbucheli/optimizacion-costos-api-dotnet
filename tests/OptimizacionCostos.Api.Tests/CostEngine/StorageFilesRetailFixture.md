# Fixture: meters reales de Azure Files (Retail Prices API) — gate de exactitud

**Fecha de captura:** 2026-07-24
**Fuente:** `https://prices.azure.com/api/retail/prices?$filter=serviceName eq 'Storage' and armRegionName eq '<region>'&currencyCode=USD`, paginado siguiendo `NextPageLink` hasta agotar (2 páginas por región, ~1690 filas `serviceName=Storage` por región).
**Regiones capturadas:** `eastus` (170 filas `productName like '*Files*'`) y `eastus2` (170 filas). Ambas idénticas en forma (mismos productos/meters/skuNames), solo difieren en `retailPrice` para los meters sensibles a región (Hot/Cool/GRS/GZRS Data Stored y sus Reservation). Los meters de `Standard` (transaction optimized) y `Files` (legacy) resultaron **idénticos en ambas regiones** (0.06 LRS, 0.10 GRS, 0.075 ZRS, 0.135 GZRS).

> **Veredicto resumen:** el mapeo provisional del plan **difiere en 4 puntos** (más un 5º hallazgo: transaction_optimized no tiene Reservation) (productName de `transaction_optimized`, meterName y unidad de `premium`, y el universo real de redundancias). Ver "Mapeo corregido" abajo — **este fixture manda** sobre el mapeo provisional para las Tasks 2 y 4.

---

## 1. Meters de Consumption reales (Data Stored / Provisioned) — por tier×redundancia

Todas las filas siguientes tienen `type = Consumption`, `tierMinimumUnits = 0` (ver §5, no hay escalones).

### 1.1 `Files v2` — tier Hot

| skuName | meterName | unitOfMeasure | retailPrice eastus | retailPrice eastus2 |
|---|---|---|---|---|
| Hot LRS | Hot LRS Data Stored | 1 GB/Month | 0.0287 | 0.0255 |
| Hot ZRS | Hot ZRS Data Stored | 1 GB/Month | 0.0360 | 0.0317 |
| Hot GRS | Hot GRS Data Stored | 1 GB/Month | 0.0632 | 0.0508 |
| Hot GZRS | Hot GZRS Data Stored | 1 GB/Month | 0.0647 | 0.0593 |

No existe `Hot RA-GRS Data Stored` ni `Hot RA-GZRS Data Stored` como meter separado (ver §4.1).

### 1.2 `Files v2` — tier Cool

| skuName | meterName | unitOfMeasure | retailPrice eastus | retailPrice eastus2 |
|---|---|---|---|---|
| Cool LRS | Cool LRS Data Stored | 1 GB/Month | 0.0228 | 0.0150 |
| Cool ZRS | Cool ZRS Data Stored | 1 GB/Month | 0.0285 | 0.0188 |
| Cool GRS | Cool GRS Data Stored | 1 GB/Month | 0.0501 | 0.0300 |
| Cool GZRS | Cool GZRS Data Stored | 1 GB/Month | 0.0513 | 0.0338 |

No existe `Cool RA-GRS Data Stored` ni `Cool RA-GZRS Data Stored`.

### 1.3 `Files v2` — tier Standard (= `transaction_optimized` interno)

| skuName | meterName | unitOfMeasure | retailPrice eastus | retailPrice eastus2 |
|---|---|---|---|---|
| Standard LRS | LRS Data Stored | 1 GB/Month | 0.06 | 0.06 |
| Standard ZRS | ZRS Data Stored | 1 GB/Month | 0.075 | 0.075 |
| Standard GRS | GRS Data Stored | 1 GB/Month | 0.10 | 0.10 |
| Standard GZRS | GZRS Data Stored | 1 GB/Month | 0.135 | 0.135 |

**Nota clave:** el meterName NO lleva el prefijo "Standard" (a diferencia de Hot/Cool que sí llevan su prefijo de tier). Es literalmente `"{RED} Data Stored"`, igual que asumía el mapeo provisional — lo que cambia es el **productName** (ver §5).

### 1.4 Producto legacy `Files` (v1, GPv1 storage account) — NO USAR

| skuName | meterName | unitOfMeasure | retailPrice eastus | retailPrice eastus2 |
|---|---|---|---|---|
| Standard LRS | LRS Data Stored | 1 GB/Month | 0.06 | 0.06 |
| Standard GRS | GRS Data Stored | 1 GB/Month | 0.10 | 0.10 |

Mismo meterName y mismo precio que `Files v2`/Standard para LRS/GRS, pero **no tiene filas ZRS ni GZRS** (el producto v1 no soporta esas redundancias). Si Task 2 usara `productName = "Files"` como asume el plan provisional, los lookups de `transaction_optimized` + ZRS/GZRS devolverían `null` siempre. Por eso la corrección usa `Files v2` para las 4 redundancias de forma uniforme.

### 1.5 `Premium Files` — tier Premium

| skuName | meterName | unitOfMeasure | retailPrice eastus | retailPrice eastus2 |
|---|---|---|---|---|
| Premium LRS | Premium LRS Provisioned | 1 GB/Month | 0.16 | 0.16 |
| Premium ZRS | Premium ZRS Provisioned | 1 GB/Month | 0.20 | 0.20 |

No existen filas Premium GRS/GZRS/RA-GRS/RA-GZRS Provisioned — Premium Files solo soporta LRS y ZRS (limitación real de Azure, no un hueco de captura).

Meters auxiliares del mismo producto (no usados por el selector de storage, informativos): `Premium {RED} Burst Bandwidth` (1 GiB, $0.01 LRS / $0.0125 ZRS), `Premium {RED} Burst Transactions` (1M, $0.50 LRS / $0.625 ZRS), `Premium {RED} Snapshots` (1 GB/Month, $0.136 LRS / $0.17 ZRS — esto es snapshot de *file share* premium, NO de managed disk; no confundir con §5).

### 1.6 Producto `Azure Files Provisioned v2` — fuera de alcance

`Azure Files Provisioned v2` (SSD/HDD NFS provisioned v2): presente en la captura con TODOS sus meters en $0.00 (probable preview) — fuera del mapeo de este plan; los SKUs `StandardV2_*`/`PremiumV2_*` van a `manual_required` por decisión del spec.

---

## 2. Meters de Reservation reales — Files Reserved Capacity / Premium Files Reserved Capacity

Todas con `tierMinimumUnits = 0`, `unitOfMeasure = "1 GB/Month"` (ver §4a sobre qué significa realmente esta unidad en filas de Reservation).

### 2.1 `Files Reserved Capacity` (Hot / Cool) — eastus

| skuName | meterName | reservationTerm | retailPrice (TOTAL) |
|---|---|---|---|
| Hot LRS - 10 TB | Hot LRS - 10 TB Data Stored | 1 Year | 2892 |
| Hot LRS - 10 TB | Hot LRS - 10 TB Data Stored | 3 Years | 6983 |
| Hot LRS - 100 TB | Hot LRS - 100 TB Data Stored | 1 Year | 27508 |
| Hot LRS - 100 TB | Hot LRS - 100 TB Data Stored | 3 Years | 67712 |
| Hot ZRS - 10 TB | Hot ZRS - 10 TB Data Stored | 1 Year | 3627 |
| Hot ZRS - 10 TB | Hot ZRS - 10 TB Data Stored | 3 Years | 8759 |
| Hot ZRS - 100 TB | Hot ZRS - 100 TB Data Stored | 1 Year | 34505 |
| Hot ZRS - 100 TB | Hot ZRS - 100 TB Data Stored | 3 Years | 84935 |
| Hot GRS - 10 TB | Hot GRS - 10 TB Data Stored | 1 Year | 6368 |
| Hot GRS - 10 TB | Hot GRS - 10 TB Data Stored | 3 Years | 15377 |
| Hot GRS - 100 TB | Hot GRS - 100 TB Data Stored | 1 Year | 60575 |
| Hot GRS - 100 TB | Hot GRS - 100 TB Data Stored | 3 Years | 149108 |
| Hot GZRS - 10 TB | Hot GZRS - 10 TB Data Stored | 1 Year | 6519 |
| Hot GZRS - 10 TB | Hot GZRS - 10 TB Data Stored | 3 Years | 15742 |
| Hot GZRS - 100 TB | Hot GZRS - 100 TB Data Stored | 1 Year | 62013 |
| Hot GZRS - 100 TB | Hot GZRS - 100 TB Data Stored | 3 Years | 152646 |
| Cool LRS - 10 TB | Cool LRS - 10 TB Data Stored | 1 Year | 2297 |
| Cool LRS - 10 TB | Cool LRS - 10 TB Data Stored | 3 Years | 5547 |
| Cool LRS - 100 TB | Cool LRS - 100 TB Data Stored | 1 Year | 21853 |
| Cool LRS - 100 TB | Cool LRS - 100 TB Data Stored | 3 Years | 53792 |
| Cool ZRS - 10 TB | Cool ZRS - 10 TB Data Stored | 1 Year | 2872 |
| Cool ZRS - 10 TB | Cool ZRS - 10 TB Data Stored | 3 Years | 6934 |
| Cool ZRS - 100 TB | Cool ZRS - 100 TB Data Stored | 1 Year | 27316 |
| Cool ZRS - 100 TB | Cool ZRS - 100 TB Data Stored | 3 Years | 67240 |
| Cool GRS - 10 TB | Cool GRS - 10 TB Data Stored | 1 Year | 5048 |
| Cool GRS - 10 TB | Cool GRS - 10 TB Data Stored | 3 Years | 12189 |
| Cool GRS - 100 TB | Cool GRS - 100 TB Data Stored | 1 Year | 48019 |
| Cool GRS - 100 TB | Cool GRS - 100 TB Data Stored | 3 Years | 118201 |
| Cool GZRS - 10 TB | Cool GZRS - 10 TB Data Stored | 1 Year | 5169 |
| Cool GZRS - 10 TB | Cool GZRS - 10 TB Data Stored | 3 Years | 12481 |
| Cool GZRS - 100 TB | Cool GZRS - 100 TB Data Stored | 1 Year | 49169 |
| Cool GZRS - 100 TB | Cool GZRS - 100 TB Data Stored | 3 Years | 121032 |

### 2.2 `Files Reserved Capacity` (Hot / Cool) — eastus2

| skuName | meterName | reservationTerm | retailPrice (TOTAL) |
|---|---|---|---|
| Hot LRS - 10 TB | Hot LRS - 10 TB Data Stored | 1 Year | 2569 |
| Hot LRS - 10 TB | Hot LRS - 10 TB Data Stored | 3 Years | 6204 |
| Hot LRS - 100 TB | Hot LRS - 100 TB Data Stored | 1 Year | 24441 |
| Hot LRS - 100 TB | Hot LRS - 100 TB Data Stored | 3 Years | 60162 |
| Hot ZRS - 10 TB | Hot ZRS - 10 TB Data Stored | 1 Year | 3194 |
| Hot ZRS - 10 TB | Hot ZRS - 10 TB Data Stored | 3 Years | 7713 |
| Hot ZRS - 100 TB | Hot ZRS - 100 TB Data Stored | 1 Year | 30383 |
| Hot ZRS - 100 TB | Hot ZRS - 100 TB Data Stored | 3 Years | 74790 |
| Hot GRS - 10 TB | Hot GRS - 10 TB Data Stored | 1 Year | 5119 |
| Hot GRS - 10 TB | Hot GRS - 10 TB Data Stored | 3 Years | 12360 |
| Hot GRS - 100 TB | Hot GRS - 100 TB Data Stored | 1 Year | 48690 |
| Hot GRS - 100 TB | Hot GRS - 100 TB Data Stored | 3 Years | 119852 |
| Hot GZRS - 10 TB | Hot GZRS - 10 TB Data Stored | 1 Year | 5975 |
| Hot GZRS - 10 TB | Hot GZRS - 10 TB Data Stored | 3 Years | 14428 |
| Hot GZRS - 100 TB | Hot GZRS - 100 TB Data Stored | 1 Year | 56837 |
| Hot GZRS - 100 TB | Hot GZRS - 100 TB Data Stored | 3 Years | 139906 |
| Cool LRS - 10 TB | Cool LRS - 10 TB Data Stored | 1 Year | 1511 |
| Cool LRS - 10 TB | Cool LRS - 10 TB Data Stored | 3 Years | 3650 |
| Cool LRS - 100 TB | Cool LRS - 100 TB Data Stored | 1 Year | 14377 |
| Cool LRS - 100 TB | Cool LRS - 100 TB Data Stored | 3 Years | 35389 |
| Cool ZRS - 10 TB | Cool ZRS - 10 TB Data Stored | 1 Year | 1894 |
| Cool ZRS - 10 TB | Cool ZRS - 10 TB Data Stored | 3 Years | 4574 |
| Cool ZRS - 100 TB | Cool ZRS - 100 TB Data Stored | 1 Year | 18019 |
| Cool ZRS - 100 TB | Cool ZRS - 100 TB Data Stored | 3 Years | 44355 |
| Cool GRS - 10 TB | Cool GRS - 10 TB Data Stored | 1 Year | 3023 |
| Cool GRS - 10 TB | Cool GRS - 10 TB Data Stored | 3 Years | 7299 |
| Cool GRS - 100 TB | Cool GRS - 100 TB Data Stored | 1 Year | 28754 |
| Cool GRS - 100 TB | Cool GRS - 100 TB Data Stored | 3 Years | 70779 |
| Cool GZRS - 10 TB | Cool GZRS - 10 TB Data Stored | 1 Year | 3406 |
| Cool GZRS - 10 TB | Cool GZRS - 10 TB Data Stored | 3 Years | 8224 |
| Cool GZRS - 100 TB | Cool GZRS - 100 TB Data Stored | 1 Year | 32396 |
| Cool GZRS - 100 TB | Cool GZRS - 100 TB Data Stored | 3 Years | 79744 |

**No existe reservation para el tier `transaction_optimized`/Standard.** Confirmado por ausencia total en las 170 filas de ambas regiones — Azure Files Reserved Capacity solo cubre Hot y Cool (y Premium, por separado, ver 2.3). Task 4 debe tratar `transaction_optimized` como "sin RI disponible", no como un `null` accidental.

### 2.3 `Premium Files Reserved Capacity` — eastus / eastus2 (idéntico en ambas regiones)

| skuName | meterName | reservationTerm | retailPrice (TOTAL) |
|---|---|---|---|
| Premium LRS - 10 TB | Provisioned | 1 Year | 16122 |
| Premium LRS - 10 TB | Provisioned | 3 Years | 38928 |
| Premium LRS - 100 TB | Provisioned | 1 Year | 153354 |
| Premium LRS - 100 TB | Provisioned | 3 Years | 377487 |
| Premium ZRS - 10 TB | Provisioned | 1 Year | 20152 |
| Premium ZRS - 10 TB | Provisioned | 3 Years | 48660 |
| Premium ZRS - 100 TB | Provisioned | 1 Year | 191693 |
| Premium ZRS - 100 TB | Provisioned | 3 Years | 471859 |

Nota: aquí el `meterName` es literalmente `"Provisioned"` (sin prefijo de redundancia) — la redundancia solo se distingue por `skuName`. Distinto de los meters de Consumption Premium, donde el meterName sí lleva el prefijo (`"Premium LRS Provisioned"`).

---

## 3. Meters de snapshot de Managed Disks (revalidación de `SqlPriceRepository.Snapshot.cs`) — eastus

`serviceName eq 'Storage'`, filtrado `meterName like '*Snapshots'` (más una fila con "Snapshots" duplicado, ver nota) y `productName like '*Managed Disks*'`:

| productName | skuName | meterName | unitOfMeasure | retailPrice |
|---|---|---|---|---|
| Standard HDD Managed Disks | Snapshots LRS | LRS Snapshots | 1 GB/Month | 0.05 |
| Standard HDD Managed Disks | Snapshots ZRS | ZRS Snapshots | 1 GB/Month | 0.05 |
| Premium SSD Managed Disks | Snapshots LRS | LRS Snapshots | 1 GB/Month | 0.132 |
| Premium SSD Managed Disks | Snapshots ZRS | ZRS Snapshots | 1 GB/Month | 0.198 |
| Standard SSD Managed Disks | Snapshots LRS | **Snapshots LRS Snapshots** | 1 GB/Month | 0.132 |
| Standard SSD Managed Disks | Snapshots ZRS | **Snapshots ZRS Snapshots** | 1 GB/Month | 0.198 |

**Confirmado para el código actual:** `SqlPriceRepository.Snapshot.cs` usa exactamente `product = "Standard HDD Managed Disks"` / `"Premium SSD Managed Disks"` y `meter = "{LRS|ZRS} Snapshots"` — **coincide byte a byte** con las filas reales de esos dos productos (0.05 y 0.132 respectivamente, que son también los valores ya cubiertos por `PriceSelection_SnapshotTests.cs`). ✅ Sin corrección necesaria para lo que el código soporta hoy.

**Búsqueda ampliada:** no existe ninguna fila `"GRS Snapshots"` para ningún producto de Managed Disks (se buscó sin restringir productName, en las ~1690 filas de `serviceName=Storage` de eastus). Esto es esperado — los managed disks solo soportan LRS/ZRS, no GRS — así que la rama `redundancy == "GRS"` en `SnapshotMeterFor` es código muerto inofensivo (nunca puede encontrar un match real), no un bug.

**Hallazgo fuera de alcance de este fixture pero relevante para el usuario (ver también el reporte):** `SnapshotMeterFor` no distingue `"Standard SSD Managed Disks"` — cualquier storageType que contenga `"STANDARDSSD"` cae en el branch `else` (porque no empieza con `"PREMIUM"`) y se le asigna el producto `"Standard HDD Managed Disks"` (precio 0.05/GB), cuando el precio real de un snapshot de un disco Standard SSD es 0.132/GB (2.6x más), bajo un producto y un meterName distintos (`"Snapshots LRS Snapshots"`, con "Snapshots" duplicado). No hay test que cubra `StandardSSD_LRS`/`StandardSSD_ZRS`. No se corrige aquí (Task 1 es solo fixture), se deja documentado.

---

## 4. Verificaciones del Step 3

### 4.1 Filas Reservation — forma real y semántica de `retailPrice`

**Nota de corrección:** la primera versión de esta sección dividía por bloques decimales (10,000/100,000 GB) y subestimaba el descuento en ~2 puntos; los valores de abajo ya están normalizados en TiB binarios tras la verificación contra la página pública (§6). No usar la versión decimal en ningún test.

Confirmado (§2): `productName` real es `Files Reserved Capacity` (Hot/Cool) y `Premium Files Reserved Capacity` (Premium); `skuName` codifica tier+redundancia+tamaño de bloque (`"Hot LRS - 10 TB"`, `"Premium ZRS - 100 TB"`, etc.); `reservationTerm` es `"1 Year"` o `"3 Years"` (strings reales, coincide con lo asumido); `unitOfMeasure` es literalmente `"1 GB/Month"` en TODAS las filas de reservation, sin importar el tamaño del bloque o el término — este campo es un remanente heredado del meter y **no debe leerse como "precio por GB al mes"**.

**(a) ¿`retailPrice` es el TOTAL del término?** Sí, confirmado por aritmética — es el precio total por todo el bloque reservado (tamaño en `skuName`, en TiB binarios: "10 TB" = 10 240 GiB, "100 TB" = 102 400 GiB — ver §6) durante todo el término (12 o 36 meses). Derivación con datos reales (eastus):

| Meter | Bloque | Término | retailPrice | Cálculo /GiB-mes | Consumption equivalente | Descuento |
|---|---|---|---|---|---|---|
| Hot LRS - 10 TB | 10 240 GiB (10 TiB) | 1 Year (12m) | 2892 | 2892 ÷ 12 ÷ 10240 = 0.023535 | Hot LRS Data Stored = 0.0287 | 18.0% |
| Hot LRS - 10 TB | 10 240 GiB (10 TiB) | 3 Years (36m) | 6983 | 6983 ÷ 36 ÷ 10240 = 0.018943 | 0.0287 | 34.0% |
| Cool LRS - 10 TB | 10 240 GiB (10 TiB) | 1 Year (12m) | 2297 | 2297 ÷ 12 ÷ 10240 = 0.018693 | Cool LRS Data Stored = 0.0228 | 18.0% |
| Cool LRS - 10 TB | 10 240 GiB (10 TiB) | 3 Years (36m) | 5547 | 5547 ÷ 36 ÷ 10240 = 0.015047 | 0.0228 | 34.0% |
| Hot GRS - 10 TB | 10 240 GiB (10 TiB) | 1 Year (12m) | 6368 | 6368 ÷ 12 ÷ 10240 = 0.051823 | Hot GRS Data Stored = 0.0632 | 18.0% |
| Premium LRS - 10 TB | 10 240 GiB (10 TiB) | 1 Year (12m) | 16122 | 16122 ÷ 12 ÷ 10240 = 0.131201 | Premium LRS Provisioned = 0.16 | 18.0% |
| Premium ZRS - 10 TB | 10 240 GiB (10 TiB) | 1 Year (12m) | 20152 | 20152 ÷ 12 ÷ 10240 = 0.163997 | Premium ZRS Provisioned = 0.20 | 18.0% |
| Hot LRS - 100 TB | 102 400 GiB (100 TiB) | 1 Year (12m) | 27508 | 27508 ÷ 12 ÷ 102400 = 0.022386 | 0.0287 | 22.0% |

Interpretando `retailPrice` como TOTAL-del-bloque-y-término (dividiendo entre GiB del bloque, en TiB binarios, y meses del término) se obtiene, de forma consistente en 8 combinaciones tier/redundancia/tamaño distintas, un precio por GiB-mes **menor** que el equivalente Consumption y con un patrón de descuento estable (~18% a 1 año, ~34% a 3 años, con descuento algo mayor en el bloque de 100 TB vs 10 TB) — exactamente el comportamiento esperado de un RI de Azure. Si en cambio se interpretara `retailPrice` como precio por GB-mes directo (ignorando el tamaño del bloque), los números no tendrían sentido (ej. $2892/GB-mes vs $0.0287/GB-mes on-demand, 100 000× más caro) — eso descarta esa lectura. **Conclusión: `retailPrice` de las filas Reservation es el costo total del compromiso completo (bloque × término), NO un precio unitario.** Tasks 2/4 deben normalizar con `retailPrice ÷ meses (12|36) ÷ (TiB del bloque × 1024)` — bloques de 10 TiB = 10,240 GiB y 100 TiB = 102,400 GiB (ver §6).

No se pudo cotejar el resultado contra la página pública `azure.microsoft.com/pricing/details/storage/files/` (ver §4.4) — la validación se basó en consistencia interna (ordenamiento de precios + patrón de descuento estable entre 8 combinaciones independientes), que es la señal más fuerte disponible sin acceso a un navegador con JS.

### 4.2 ¿El pricing estándar de Files es escalonado (`tierMinimumUnits > 0`)?

**(b) No.** Se verificó `tierMinimumUnits` en las 170 filas de `*Files*` de cada región (eastus y eastus2): el único valor distinto encontrado fue `0` en absolutamente todas las filas, tanto Consumption como Reservation. No hay escalones de precio por volumen en ningún meter de Azure Files — una sola fila por meter con precio plano, independiente de la cantidad de GB almacenados. El selector de Task 2 no necesita lógica de "tomar el tierMinimumUnits más bajo"; hay una sola fila por combinación tier×redundancia.

### 4.3 Meters de snapshots de Managed Disks — revalidación

Ver §3 completo. Confirmado sin cambios para lo que el código ya soporta (Standard HDD / Premium SSD, LRS/ZRS). Gap real encontrado en Standard SSD, documentado como hallazgo fuera de alcance.

### 4.4 Cotejo contra la calculadora/página pública de Azure

**Pendiente para un controlador con navegador JS.** Se intentó `WebFetch` contra `https://azure.microsoft.com/en-us/pricing/details/storage/files/`; la página renderiza las tablas de precio vía JavaScript del lado cliente y el fetch (que solo obtiene el HTML servido, sin ejecutar JS) devolvió las celdas de precio como placeholders `"$-"` sin valores numéricos — no hay nada que cotejar desde este entorno.

**Verificación alternativa realizada (consistencia interna, según lo indicado si la página pública no es alcanzable):**
1. `hot < transaction_optimized` (mismo GB, misma redundancia): eastus LRS 0.0287 < 0.06 ✓; eastus2 LRS 0.0255 < 0.06 ✓.
2. `cool < hot`: eastus LRS 0.0228 < 0.0287 ✓; eastus2 LRS 0.0150 < 0.0255 ✓.
3. `reservation per-GB-mes derivado < consumption per-GB-mes equivalente`: verificado en 8 combinaciones independientes en §4.1, todas cumplen con margen de descuento coherente (18%/34%; 22% en bloques de 100 TiB).

Todas las verificaciones de consistencia interna pasaron. **Queda pendiente el cotejo 1:1 contra la calculadora pública** (requiere un entorno con navegador/JS o acceso al portal de Azure) — recomendado antes de exponer estos precios a un cliente final, pero no bloquea Tasks 2/4 dado que los valores vienen directo de la Retail Prices API (la misma fuente que usa el resto de la plataforma para todos los demás servicios).

---

## 5. Mapeo corregido (usar en Task 2 `FilesMeterFor` y Task 4)

| tier interno | productName (Consumption) | meterName (Consumption) | unidad | productName (Reservation) | ¿tiene RI? |
|---|---|---|---|---|---|
| `hot` | `Files v2` | `Hot {RED} Data Stored` | `1 GB/Month` | `Files Reserved Capacity`, skuName `Hot {RED} - {10\|100} TB` | Sí (LRS/ZRS/GRS/GZRS) |
| `cool` | `Files v2` | `Cool {RED} Data Stored` | `1 GB/Month` | `Files Reserved Capacity`, skuName `Cool {RED} - {10\|100} TB` | Sí (LRS/ZRS/GRS/GZRS) |
| `transaction_optimized` | **`Files v2`** (⚠ el plan decía `Files`) | `{RED} Data Stored` | `1 GB/Month` | — | **No** (no existe RI para este tier) |
| `premium` | `Premium Files` | **`Premium {RED} Provisioned`** (⚠ el plan decía `{RED} Provisioned`, sin el prefijo) | **`1 GB/Month`** (⚠ el plan decía `1 GiB/Month`) | `Premium Files Reserved Capacity`, skuName `Premium {RED} - {10\|100} TB`, meterName literal `Provisioned` | Sí (LRS/ZRS solamente) |

- La unidad `1 GB/Month` de TODOS los meters de Files equivale a **GiB binario** (§6.2): la calculadora multiplica GiB facturables × retail_price sin conversión decimal.
- El sufijo del skuName de Reservation (`- 10 TB` / `- 100 TB`) significa **TiB**: 10,240 / 102,400 GiB (§6.3).

`{RED}` real por tier (⚠ difiere del plan, que asumía el mismo set de 6 para los 4 tiers):
- `hot`, `cool`, `transaction_optimized`: LRS, ZRS, GRS, GZRS — **no existen RA-GRS ni RA-GZRS como meter separado**. Una cuenta de storage RA-GRS/RA-GZRS se factura bajo el mismo meter `GRS`/`GZRS` (el acceso de lectura secundario no tiene meter propio en Files). Task 2 debe mapear `RA-GRS → GRS` y `RA-GZRS → GZRS` antes de construir el nombre del meter, no tratarlos como sufijos válidos de `{RED}`.
- `premium`: solo LRS, ZRS (no soporta GRS/GZRS/RA-GRS/RA-GZRS — limitación real del servicio).

### Correcciones puntuales vs el mapeo provisional del plan

1. **`transaction_optimized`: productName `Files` → `Files v2`.** El producto `Files` (v1) sí existe y tiene el mismo meterName/precio para LRS/GRS, pero no tiene filas ZRS/GZRS — usarlo rompería esas dos redundancias. `Files v2` con skuName `Standard {RED}` cubre las 4 redundancias con el mismo meterName sin prefijo `{RED} Data Stored` (esto último sí coincidía con el plan).
2. **`premium`: meterName `{RED} Provisioned` → `Premium {RED} Provisioned`.** El meter real lleva el prefijo `Premium`.
3. **`premium`: unidad `1 GiB/Month` → `1 GB/Month`.** La API rotula la unidad como `GB`, pero el valor equivale a GiB binario (§6.2), igual que todos los demás meters de Files.
4. **Universo de `{RED}`: quitar RA-GRS/RA-GZRS como sufijos de meter propios; mapearlos a GRS/GZRS antes del lookup.** Ningún tier de Files tiene meter `RA-GRS`/`RA-GZRS` real.
5. **`transaction_optimized` no tiene Reservation disponible** — el plan no lo decía explícitamente pero tampoco lo contradice; se deja explícito para Task 4 (si el análisis intenta buscar RI de Standard, debe devolver "no aplica", no `null` silencioso que se confunda con "no encontrado").

Todo lo demás del mapeo provisional (unidad `1 GB/Month` para hot/cool/transaction_optimized, patrón `{RED} Data Stored` y `Hot/Cool {RED} Data Stored`) **coincide exactamente** con lo capturado.

---

## 6. Cotejo 1:1 contra la página pública — CERRADO (controlador con navegador JS, 2026-07-24)

Verificado en vivo en `https://azure.microsoft.com/en-us/pricing/details/storage/files/` (USD, vista mensual):

- **East US 2 (LRS)** — PAYG: TxOpt **$0.0600**, Hot **$0.0255**, Cool **$0.0150** "per used GiB per month" → idénticos a §2. Reservas PAYG (HDD): Hot 10 TiB = **$214.09/mes** (1y) / $172.34/mes (3y); Cool 10 TiB = $125.92/mes (1y); Hot 100 TiB = $2,036.75/mes (1y). SSD provisioned v1: 10 TiB = $1,343.50/mes (1y) / $1,081.34/mes (3y).
- **East US (LRS)** — PAYG: TxOpt **$0.0600**, Hot **$0.0287**, Cool **$0.0228** → idénticos a §2. Reserva Hot 10 TiB 1y = **$241.00/mes** → ×12 = **$2,892.00 = exactamente el retailPrice de la fila Reservation del API** (§4.1). Igualdad cerrada.

### Conclusiones DEFINITIVAS que Tasks 2 y 4 deben usar

1. **`retailPrice` de Reservation = TOTAL del término del bloque.** Normalización a GiB-mes: `retailPrice ÷ meses (12|36) ÷ (TiB_bloque × 1024)`. Ej.: 2892 ÷ 12 ÷ 10,240 = **$0.023535/GiB-mes** (eastus Hot LRS 1y).
2. **El "GB" de los meters de Files equivale a GiB binario**: la página publica "per used GiB per month" con el MISMO número que el meter "1 GB/Month" del API. La calculadora multiplica GiB facturables × precio del meter, sin conversión decimal.
3. **Bloques de reserva: 10 TiB y 100 TiB** ("increments of 10 TiB and 100 TiB", texto literal de la página). El "TB" del skuName del API (`Hot LRS - 10 TB`) significa TiB.
4. La página confirma que el modelo PAYG solo ofrece reserva para **Hot y Cool** (TxOpt no aparece en su tabla de reservas) y que premium (SSD provisioned v1) tiene la suya — consistente con §5.
5. La reserva de Files cubre "used storage"; metadata y transacciones quedan fuera (consistente con la exclusión de transacciones/metadata del plan).

---

## 7. Cotejo público de snapshots — CERRADO (controlador con navegador JS, 2026-07-24)

La página pública de Managed Disks (`https://azure.microsoft.com/en-us/pricing/details/managed-disks/`, región East US) publica: snapshots en Standard storage se cobran a **$0.05/GB por mes tanto en LRS como ZRS**, sobre el espacio ocupado por los deltas ('the storage occupied by the delta changes since the last snapshot'). Coincide exactamente con el meter del API (`Standard HDD Managed Disks` / `LRS Snapshots` = 0.05, §3) que ya usa `GetSnapshotPricePerGb` y `PriceSelection_SnapshotTests`. La página también confirma que Azure factura snapshots por espacio OCUPADO — consistente con la nota referencial ('techo por tamaño del disco de origen') que usará la calculadora de snapshots.
