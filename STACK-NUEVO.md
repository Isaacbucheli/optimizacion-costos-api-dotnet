# Stack nuevo (.NET) — configuración de destino

> **Regla:** el stack nuevo NUNCA apunta a la producción antigua (Python). Coexistencia, no corte.

## Datos
- **Servidor SQL:** `sqldb-optimizacion-costos.database.windows.net`
- **Base de datos del stack nuevo:** **`sqldb-optimizacion-costos-valida`** (clon de prod; su PROPIA base, evoluciona aparte).
- **NUNCA** usar `sqldb-optimizacion-costos` (esa es la BD de **producción / Python**).

La BD se fija por la variable `SQL_DATABASE`:
- **Local (dev):** ya está en `src/OptimizacionCostos.Api/appsettings.Development.json` → `SQL_DATABASE = sqldb-optimizacion-costos-valida`.
- **Prod (App Service del stack nuevo):** `SQL_DATABASE = sqldb-optimizacion-costos-valida` en *app settings* (lo fija la tarea I).

## Recursos reutilizados (compartidos con prod, NO se re-credencializa)
- **Key Vault** (`KEY_VAULT_URL`): mismos `client_secret` de las credenciales Azure de clientes.
- **Storage** (`STORAGE_ACCOUNT_NAME` + contenedores uploads/outputs/templates): plantillas Excel/Word, informes, logos.
- **Azure OpenAI** (`AZURE_OPENAI_*`): asistente de precios + narrativa de informes.
- **JWT_SECRET:** el mismo que emite/valida el login (para que los tokens sirvan).

## Variables de entorno (App Service del stack nuevo) — las fija la tarea I
`SQL_SERVER`, `SQL_DATABASE=sqldb-optimizacion-costos-valida`, `SQL_USERNAME`, `SQL_PASSWORD` (secreto),
`JWT_SECRET`, `KEY_VAULT_URL`, `STORAGE_ACCOUNT_NAME`, `AZURE_OPENAI_*`, `CORS_ORIGINS` (origen del SWA nuevo).

## Front
- SWA del stack nuevo: **`swa-optimizacion-costos-frontend`** (repo `innovacion-CDC`).
- El front habla SOLO con este backend .NET (ver `innovacion-CDC/STACK-NUEVO.md`).

## Pendiente de la tarea I (requiere OK del usuario)
- Crear/registrar el App Service .NET del stack nuevo y apuntarlo a `-valida`.
- **Nunca** repuntar el `app-optimizacion-costos-api-dotnet` existente a `-valida`: el front de prod (vanilla) lee de él contra la BD de prod; cambiarlo rompería prod.
