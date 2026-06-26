# PoC — Backend en .NET 8 (ASP.NET Core)

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

## Pendiente (con OK del usuario)

1. **Cutover:** decidir si el front apunta a este backend para alertas (primer servicio "estrangulado")
   y, en producción, si `SQL_DATABASE` pasa a la BD real (donde están los usuarios reales) o se mantiene aislado.
2. (Opcional) login SQL dedicado en vez de reusar `bitadmin` — descartado por ahora por decisión del usuario.
