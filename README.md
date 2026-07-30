# API .NET — Innovación CDC (Plataforma de Optimización y Mejoras)

Backend **único** de la Plataforma CDC: ASP.NET Core 8 (C#) sobre Azure SQL.
Todas las rutas — incluido el login — se sirven desde aquí.

Corre en Azure como **App Service Linux (.NET 8)** sobre **Azure SQL**, y comparte con la
plataforma un **Key Vault** (secretos de credenciales de clientes), una cuenta de **Blob Storage**
(plantillas, salidas, logos) y un recurso de **Azure OpenAI**. Los nombres y URLs concretos de los
recursos se administran fuera del repo (app settings del App Service / documentación privada).
El frontend es el repo [`innovacion-CDC`](https://github.com/Isaacbucheli/innovacion-CDC)
(React + Vite, Azure Static Web Apps).

Ver [STACK-NUEVO.md](STACK-NUEVO.md) para la forma de la configuración de entorno.

## Módulos (`src/OptimizacionCostos.Api/Features/`)

| Módulo | Rutas | Qué hace |
|---|---|---|
| **Identity** | `/auth` | Login (JWT HS256, hashes PBKDF2 compatibles con los usuarios existentes), CRUD de usuarios y asignación de clientes. El rol se re-consulta **vivo** en `app_users` en cada request — el token no autoriza solo. |
| **Clients** | `/clients` | CRUD de clientes, logo (Blob), purga/borrado con confirmación de nombre. |
| **AzureIntegration** | `/azure/credentials`, `/azure/subscriptions`, `/azure/user-sessions` | Credenciales de service principal por cliente (el secreto vive solo en Key Vault), sync de suscripciones (`is_managed` lo decide el usuario; el sync nunca lo toca) y sesiones Azure con cuenta de usuario vía Lighthouse (device code flow, para clientes sin app registration). |
| **Inventory** | `/azure/import` | Importación de inventario vía Resource Graph (KQL por servicio del catálogo, 11 inserters de detalle) + discovery de servicios con clasificación IA. |
| **Catalog** | `/service-catalog` | Catálogo de servicios Azure que alimenta el costeo (CRUD admin + sugerencias desde discovery). |
| **CostEngine** | `/analysis/*` (calculate, results, scenarios, manual-cost, ri-coverage, power-history), `/prices/*` | Motor de costos: 12 calculadoras, precios de la Azure Retail API con caché SQL, asistente IA de precios (solo elige entre candidatos reales, auditado), 4 escenarios de ahorro, cobertura RI, control de acceso por cliente. |
| **FinOpsData** | `/finops-data` | Datasets estilo FinOps Toolkit: elegibilidad RI, categorías, cobertura de cálculo. |
| **Optimization** | `/optimization` | Barrido de optimización del tenant: 7 checks KQL (discos huérfanos, IPs sin uso, VMs detenidas sin desasignar, etc.) con ahorro estimado y estados de hallazgo. Acceso gateado por `OPTIMIZATION_ALLOWED_EMAILS`. |
| **Cdc** | `/cdc` | Gestión CDC: reservas de capacidad (por vencer, utilización, consumidores) y power history/uptime como job en background. |
| **Waf** | `/waf`, `/waf/admin` | Matriz Well-Architected: ingesta de Advisor (CSV y sync), dedup + consolidación, curación IA, Advisor Score (scheduler semanal), export/import Excel, costo referencial Azure. |
| **Reports** | `/reports`, `/excel`, `/files` | Informe de gestión mensual (generación en background, narrativa IA, export Word) y exportación **Excel de costos v3** (generada desde código: subtotales comparables RI, margen comercial, fórmulas vivas). |
| **AlertCatalog** | `/alert-catalog` | Catálogo de alertas Azure Monitor + biblioteca KQL. |
| **Storage** | — (interno) | Acceso a Blob (uploads/outputs/plantillas) vía `DefaultAzureCredential`. |
| **Health** | `/health` | Público, para probes. |

**Jobs en background** (hosted services): power history, Advisor Score semanal (lease-lock) y generación de informes (cola en proceso).

Contrato JSON en **snake_case** (`JsonNamingPolicy.SnakeCaseLower`).

**Swagger** (`/swagger` y `/swagger/v1/swagger.json`) está **cerrado fuera de Development**: publicaba
toda la superficie de la API sin token. Para abrirlo en un entorno desplegado hay que poner
`SWAGGER_ENABLED=true` a mano y volver a apagarlo. Para comprobar que un deploy quedó vivo, usar
`/health`.

## Correr local

Requiere [.NET 8 SDK](https://dotnet.microsoft.com/download). Los tests no necesitan Azure ni BD (fakes + `WebApplicationFactory`):

```powershell
dotnet test    # 404 tests
```

Levantar la API (servidor/BD y credenciales van por variables de entorno o user-secrets; valores en la documentación privada del equipo):

```powershell
$env:SQL_SERVER="..."; $env:SQL_DATABASE="..."; $env:SQL_USERNAME="..."; $env:SQL_PASSWORD="..."
$env:JWT_SECRET="..."
dotnet run --project src/OptimizacionCostos.Api
# Swagger: http://localhost:5169/swagger  (habilitado solo en Development)
```

> Sin `KEY_VAULT_URL`/`STORAGE_ACCOUNT_NAME` el núcleo (login, results, scenarios, calculate)
> corre igual, pero credenciales/import/reservas/WAF/informes/export fallan.

## Variables de entorno

| Grupo | Variables |
|---|---|
| SQL | `SQL_SERVER`, `SQL_DATABASE`, `SQL_USERNAME`, `SQL_PASSWORD` |
| Auth | `JWT_SECRET`, `APP_AUTH_TOKEN_MINUTES` (480), `APP_AUTH_BOOTSTRAP_ENABLED` |
| Azure | `KEY_VAULT_URL`, `STORAGE_ACCOUNT_NAME`, `STORAGE_CONTAINER_UPLOADS`/`_OUTPUTS` |
| IA | `AZURE_OPENAI_ENABLED`, `_ENDPOINT`, `_API_KEY`, `_DEPLOYMENT`, `_API_VERSION`, `AZURE_OPENAI_PRICING_ASSIST_MODE` |
| Sesiones de usuario (Lighthouse) | `USER_SESSION_AUTH_ENABLED`, `USER_SESSION_CLIENT_ID`, `USER_SESSION_TENANT_ID`, `USER_SESSION_ALLOWED_EMAILS` |
| Schedulers / gating | `ADVISOR_SCORE_SCHEDULER_ENABLED`/`_TZ`/`_TIME`/`_WEEKDAY`, `OPTIMIZATION_ALLOWED_EMAILS` |
| CORS | `CORS_ORIGINS` (origen del frontend) |

La fuente de verdad es [`Configuration/AppConfig.cs`](src/OptimizacionCostos.Api/Configuration/AppConfig.cs).

## Infra y CI/CD

- App Service Linux (DOTNETCORE|8.0, Always On) con **identidad administrada** system-assigned: necesita los roles `Storage Blob Data Contributor` (Storage) y `Key Vault Secrets Officer` (Key Vault) — sin ellos fallan logos, Excel, informes y credenciales.
- **Deploy:** push a `main` → GitHub Actions ([deploy.yml](.github/workflows/deploy.yml)): restore → test → publish → deploy. Auth a Azure por **OIDC** con un service principal de rol Website Contributor acotado al App Service (sin secretos de larga vida).
- ⏱️ Tras un deploy el App Service tarda **~5-8 min** en estabilizar (rutas nuevas dan 404 mientras el worker reinicia).
- Cada deploy a producción requiere OK explícito.
