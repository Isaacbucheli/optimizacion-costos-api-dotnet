# Backend en .NET 8 (ASP.NET Core)

Prueba de concepto para evaluar migrar el backend de **Python/FastAPI** a **.NET 8 / C#**,
sin tocar la API actual. Migra **un módulo aislado** (Catálogo de alertas Azure Monitor,
`/alert-catalog`) replicando 1:1 su comportamiento.

## Objetivo: probar que se puede coexistir

El punto clave de la estrategia *strangler* (migrar ruta por ruta) es que el backend nuevo
**conviva** con el FastAPI actual contra la misma BD y el mismo login. Esta PoC demuestra:

| Patrón del FastAPI | Cómo se replicó en .NET | Verificado |
|---|---|---|
| Login JWT HS256 firmado con `JWT_SECRET` | `JwtBearer` con clave simétrica, sin issuer/audience, claim `sub`=email | ✅ test `Token_estilo_FastAPI_es_aceptado` (token construido byte-a-byte como `app/auth.py`) |
| Rol vivo desde `dbo.app_users` (el token no autoriza solo) | `OnTokenValidated` re-consulta el rol y lo inyecta | ✅ tests de rol |
| `lector` solo lee; `admin`/`consultor` mutan | `[Authorize]` en GET, `[Authorize(Roles=...)]` en mutaciones | ✅ `Lector_no_puede_crear` (403), `Consultor_puede_crear` (200) |
| SQL parametrizado (pyodbc) | `Microsoft.Data.SqlClient` (TDS nativo, sin ODBC) | ✅ store + whitelist de columnas |
| Schema-ensure + seed idempotente | `AlertCatalogSchema` (crea tablas si faltan, siembra si vacío) | ✅ seed portado desde el mismo `seed_data.py` |
| Contrato JSON snake_case (lo consume el front) | `JsonNamingPolicy.SnakeCaseLower` | ✅ `Lista_devuelve_snake_case` |
| `openapi.json` | Swagger en `/swagger` | ✅ expone los 9 endpoints |

**Conclusión:** la coexistencia es viable. Los tokens del login actual sirven sin cambios,
y el nuevo backend puede atender `/alert-catalog` mientras el FastAPI sigue sirviendo el resto.

## Estructura

```
src/OptimizacionCostos.Api/
  Program.cs                      # host, auth, CORS, swagger, DI
  Configuration/AppConfig.cs      # lee las MISMAS env vars que FastAPI (SQL_*, JWT_SECRET)
  Data/SqlConnectionFactory.cs    # Microsoft.Data.SqlClient
  Auth/                           # JWT + rol vivo desde app_users
  Features/AlertCatalog/          # módulo migrado (controller, store, schema, seed)
  Features/Health/                # /health público
tests/OptimizacionCostos.Api.Tests/   # 12 tests, sin Azure ni SQL (fakes)
```

## Correr local

Requiere [.NET 8 SDK](https://dotnet.microsoft.com/download). Tests (no necesitan Azure):

```powershell
dotnet test
```

Levantar la API (sin BD solo responden /health y los 401):

```powershell
$env:JWT_SECRET="<mismo secreto que el FastAPI>"
$env:SQL_SERVER="..."; $env:SQL_DATABASE="..."; $env:SQL_USERNAME="..."; $env:SQL_PASSWORD="..."
dotnet run --project src/OptimizacionCostos.Api
# Swagger: http://localhost:5xxx/swagger
```

> Variables de entorno idénticas a las del FastAPI → apunta a la misma BD.

## Round-trip real (✅ hecho 2026-06-26)

BD separada **`sqldb-optimizacion-costos-dotnet`** (tier Basic, mismo servidor `sqldb-optimizacion-costos`,
datos aislados de prod). El test `DbRoundTripTests` ejecuta contra Azure SQL real:
schema-ensure → seed (48 alertas + 14 KQL) → create → get → update → soft-delete, y verifica
seed idempotente. Pasa. Se activa con `BIT_INTEGRATION_DB=1` + `SQL_*` en el entorno
(con `SQL_DATABASE=sqldb-optimizacion-costos-dotnet`); fuera de eso es no-op y la suite sigue DB-free.

## Desplegado en Azure (✅ 2026-06-26)

- **App Service:** `app-optimizacion-costos-api-dotnet` (Linux, runtime DOTNETCORE|8.0), reusando el
  plan B1 `asp-optimizacion-costos-api` (sin costo de plan extra). HTTPS-only. Startup `dotnet OptimizacionCostos.Api.dll`.
  URL: https://app-optimizacion-costos-api-dotnet.azurewebsites.net
- **App settings:** mismas credenciales SQL del FastAPI (login `bitadmin`) y mismo `JWT_SECRET`
  (→ los tokens del login actual sirven), con `SQL_DATABASE=sqldb-optimizacion-costos-dotnet`. `CORS_ORIGINS` = el del front.
- **Deploy:** `dotnet publish -c Release -p:UseAppHost=false` → zip → `az webapp deploy --type zip`.
- **Verificación en vivo:** `/health` 200, `/alert-catalog` sin token 401, y con token admin: GET 48 alertas,
  GET 14 KQL, POST crea (id 50). Stack completo (auth + rol vivo en `app_users` + CRUD SQL) confirmado en Azure.

> Costo recurrente nuevo: solo la BD Basic (~$5/mes). El App Service comparte el plan B1 existente.

## CI/CD (✅ 2026-06-26)

- **Repo:** https://github.com/Isaacbucheli/optimizacion-costos-api-dotnet (privado).
- **Workflow:** `.github/workflows/deploy.yml` — en push a `main`: restore → test → publish → deploy.
- **Auth a Azure: OIDC** (sin secretos de larga vida). Service principal `github-optimizacion-costos-api-dotnet`
  con credencial federada para `repo:Isaacbucheli/optimizacion-costos-api-dotnet:ref:refs/heads/main` y rol
  **Website Contributor** acotado solo a este App Service. Secrets en el repo: `AZURE_CLIENT_ID`,
  `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (son IDs, no credenciales).
- Primer run verde end-to-end; app desplegada por Actions verificada en vivo (GET autenticado → alertas).

## Cutover de demo (✅ 2026-06-26)

El piloto React **`innovacion-CDC`** (SWA `ambitious-moss-00f9c9610`) ya consume el catálogo de alertas
desde este backend .NET (primer servicio "estrangulado"): en `src/lib/api.ts`, las llamadas a
`/alert-catalog` y `/kql` van a `catalogBase()` (.NET); el login y todo lo demás siguen en el FastAPI.
- **BD: aislada** (`sqldb-optimizacion-costos-dotnet`) por decisión del usuario → es un cutover de
  **demo/staging**, no toca datos ni el catálogo real. Se sembró el email del usuario en `app_users`
  de la BD aislada para que el .NET lo reconozca tras el login del FastAPI.
- Verificado en vivo: GET autenticado devuelve las 48 alertas sembradas; el bundle desplegado apunta
  el catálogo al .NET y mantiene auth en FastAPI.

## Motor de costos — Fase 0 (✅ 2026-06-26)

Migración del módulo de costos (el más grande y delicado) por fases, con **paridad exacta** contra
el FastAPI validada por la suite de regresión. La Fase 0 cubre los fundamentos y las 12 calculadoras.

- **Fundamentos** (`Features/CostEngine/`): `CostResult` (savings + descarte de RI), `ResourceRow`,
  `IPriceRepository` + DTOs de precios, `IPricingConstants`, `ICostCalculator`, `CalculatorRegistry`
  (mapeo `calculator_key` → calculadora, igual que `app/calculators/__init__.py`).
- **12 calculadoras** (`Features/CostEngine/Calculators/`): compute_vm, managed_disk, sql_database,
  sql_managed_instance, app_service_plan, mysql_flex, cosmos_account, redis_cache, public_ip,
  sql_vm_metadata, synapse_dedicated_pool, elastic_pool — port 1:1 de `app/calculators/*.py`.
- **Tests de paridad** (`tests/CostEngine/`): ~55 tests xUnit que portan la suite de regresión de
  precios mockeados con los **montos exactos** del Python. Suite total **69/69 verde**.
- **Verificación adversarial**: revisores escépticos compararon fórmula a fórmula .NET vs Python;
  detectaron y se corrigió 1 divergencia de borde en Synapse (fallback de nivel DWc: `or` vs `??`).
- Construido con un workflow multi-agente (1 agente por calculadora + verificadores en paralelo).

### Pendiente del motor de costos (próximas fases)
- **Fase 1**: capa de precios real (Azure Retail API client + `price_repository` matching determinista).
- **Fase 2**: `cost_engine` (orquestación, carga/enriquecimiento de recursos, inserción) + 4 escenarios;
  validar con diff fila a fila de `cost_results` (Python vs .NET) sobre un análisis real.
- **Fase 3**: endpoints + cutover.

## Pendiente (con OK del usuario)

1. **Pasar el cutover de alertas a producción** (cuando el usuario quiera): apuntar `SQL_DATABASE` del .NET
   a la BD real `sqldb-optimizacion-costos` (datos + usuarios reales) y/o repuntar el front principal `alert-catalog.js`.
2. (Opcional) login SQL dedicado en vez de reusar `bitadmin` — descartado por ahora por decisión del usuario.
