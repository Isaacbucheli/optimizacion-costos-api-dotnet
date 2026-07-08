# Stack .NET — configuración de entorno

> Los **nombres y URLs concretos** de los recursos Azure (App Service, servidor/BD SQL,
> Key Vault, Storage, OpenAI, SWA) se administran **fuera del repo**: app settings del
> App Service + documentación privada del equipo. El repo es público — no escribirlos aquí.

## Datos

- La API usa una base Azure SQL propia (los stores hacen ensure-schema al primer uso).
- La BD se fija con `SQL_DATABASE`:
  - **Local (dev):** variables de entorno o user-secrets (`SQL_SERVER`, `SQL_DATABASE`, `SQL_USERNAME`, `SQL_PASSWORD`).
  - **Azure:** app setting del App Service.

## Recursos de la plataforma

- **Key Vault** (`KEY_VAULT_URL`): los `client_secret` de las credenciales Azure de clientes. Solo el secreto vive ahí; SQL guarda la referencia.
- **Blob Storage** (`STORAGE_ACCOUNT_NAME`; contenedores uploads/outputs/templates): plantillas Excel/Word, informes, logos.
- **Azure OpenAI** (`AZURE_OPENAI_*`): asistente de precios, curación WAF, narrativa de informes.

La **identidad administrada** (system-assigned) del App Service necesita `Storage Blob Data Contributor` en el Storage y `Key Vault Secrets Officer` en el Key Vault; sin esos roles fallan logos/Excel/informes/credenciales.

## Frontend

- Repo [`innovacion-CDC`](https://github.com/Isaacbucheli/innovacion-CDC), desplegado como Azure Static Web App.
- `CORS_ORIGINS` de esta API debe incluir el origen del SWA, y el CSP del front (`staticwebapp.config.json`, `connect-src`) debe listar el host de esta API — si falta cualquiera de los dos, el navegador da "Failed to fetch".
- En prod el front apunta aquí vía `VITE_API_BASE_URL`; en dev usa el proxy de Vite `/api` → `http://localhost:5169`.

## Variables de entorno del App Service

`SQL_SERVER`, `SQL_DATABASE`, `SQL_USERNAME`, `SQL_PASSWORD` (secreto), `JWT_SECRET`,
`KEY_VAULT_URL`, `STORAGE_ACCOUNT_NAME`, `AZURE_OPENAI_*` (incl. `AZURE_OPENAI_API_KEY`, secreto),
`CORS_ORIGINS`, y los flags opcionales (`ADVISOR_SCORE_SCHEDULER_*`, `OPTIMIZATION_ALLOWED_EMAILS`,
`USER_SESSION_*`). Detalle completo en el [README](README.md#variables-de-entorno).
