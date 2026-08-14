using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Esquema del módulo Informe de valor (idempotente, estilo ConsultantsSchema). Sin seed.
/// El índice único va sobre natural_key_hash y no sobre las columnas de la clave: sumadas
/// en NVARCHAR llegan a 3334 bytes, muy por encima del límite de 1700 de una clave de
/// índice no agrupada (el CREATE pasa con advertencia y revienta después con el error 1946).
/// </summary>
public static class InformeValorSchema
{
    public static readonly IReadOnlyList<string> Statements =
    [
        """
        IF OBJECT_ID('dbo.informe_valor_ingesta', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_ingesta (
                ingesta_id INT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_ingesta_client REFERENCES dbo.clients(client_id),
                kind NVARCHAR(20) NOT NULL,
                source_file_name NVARCHAR(400) NOT NULL,
                rows_total INT NOT NULL CONSTRAINT DF_iv_ingesta_total DEFAULT 0,
                rows_processed INT NOT NULL CONSTRAINT DF_iv_ingesta_proc DEFAULT 0,
                rows_skipped INT NOT NULL CONSTRAINT DF_iv_ingesta_skip DEFAULT 0,
                rows_merged INT NOT NULL CONSTRAINT DF_iv_ingesta_merged DEFAULT 0,
                truncated_values INT NOT NULL CONSTRAINT DF_iv_ingesta_trunc DEFAULT 0,
                status NVARCHAR(30) NOT NULL,
                error_message NVARCHAR(1000) NULL,
                warnings_json NVARCHAR(4000) NULL,
                started_at DATETIME2 NOT NULL,
                completed_at DATETIME2 NULL,
                created_by NVARCHAR(200) NULL
            );
            CREATE INDEX IX_iv_ingesta_client ON dbo.informe_valor_ingesta (client_id, kind, started_at DESC);
        END
        """,
        """
        IF OBJECT_ID('dbo.informe_valor_facturacion', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_facturacion (
                row_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_fact_client REFERENCES dbo.clients(client_id),
                ingesta_id INT NOT NULL,
                natural_key_hash CHAR(64) NOT NULL,
                tenant NVARCHAR(100) NULL,
                subscription_name NVARCHAR(200) NULL,
                subscription_id NVARCHAR(100) NULL,
                resource_group NVARCHAR(255) NULL,
                resource_name NVARCHAR(512) NULL,
                cost_center NVARCHAR(200) NULL,
                category NVARCHAR(200) NULL,
                subcategory NVARCHAR(200) NULL,
                service NVARCHAR(200) NULL,
                quantity DECIMAL(28,10) NULL,
                unit NVARCHAR(100) NULL,
                rate DECIMAL(28,10) NULL,
                pvp DECIMAL(28,10) NOT NULL,
                period_year SMALLINT NOT NULL,
                period_month TINYINT NOT NULL
            );
            CREATE UNIQUE INDEX UX_iv_fact_key ON dbo.informe_valor_facturacion (client_id, natural_key_hash);
            CREATE INDEX IX_iv_fact_periodo ON dbo.informe_valor_facturacion (client_id, period_year, period_month);
        END
        """,
        """
        IF OBJECT_ID('dbo.informe_valor_evolucion', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_evolucion (
                row_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_evo_client REFERENCES dbo.clients(client_id),
                ingesta_id INT NOT NULL,
                natural_key_hash CHAR(64) NOT NULL,
                category NVARCHAR(200) NULL,
                subcategory NVARCHAR(200) NULL,
                resource_name NVARCHAR(512) NOT NULL,
                is_reservation BIT NOT NULL CONSTRAINT DF_iv_evo_res DEFAULT 0,
                pvp DECIMAL(28,10) NOT NULL,
                period_year SMALLINT NOT NULL,
                period_month TINYINT NOT NULL
            );
            CREATE UNIQUE INDEX UX_iv_evo_key ON dbo.informe_valor_evolucion (client_id, natural_key_hash);
            CREATE INDEX IX_iv_evo_periodo ON dbo.informe_valor_evolucion (client_id, period_year, period_month);
        END
        """,
        """
        IF OBJECT_ID('dbo.informe_valor_caso', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_caso (
                row_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_caso_client REFERENCES dbo.clients(client_id),
                ingesta_id INT NOT NULL,
                natural_key_hash CHAR(64) NOT NULL,
                caso NVARCHAR(120) NULL,
                fecha_registro DATE NULL,
                estado NVARCHAR(120) NULL,
                sla_horas DECIMAL(18,4) NULL,
                duracion_cruda DECIMAL(18,4) NULL,
                cumple NVARCHAR(20) NULL,
                categoria NVARCHAR(200) NULL,
                subcategoria NVARCHAR(300) NULL,
                horario NVARCHAR(120) NULL
            );
            CREATE UNIQUE INDEX UX_iv_caso_key ON dbo.informe_valor_caso (client_id, natural_key_hash);
        END
        """,
        """
        IF OBJECT_ID('dbo.informe_valor_rbac', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_rbac (
                row_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_rbac_client REFERENCES dbo.clients(client_id),
                ingesta_id INT NOT NULL,
                natural_key_hash CHAR(64) NOT NULL,
                sheet_name NVARCHAR(200) NULL,
                suscripcion NVARCHAR(200) NULL,
                scope NVARCHAR(900) NULL,
                nivel NVARCHAR(60) NULL,
                rol NVARCHAR(200) NULL,
                tipo NVARCHAR(60) NULL,
                nombre NVARCHAR(300) NULL,
                login NVARCHAR(300) NULL,
                cuenta_activa NVARCHAR(30) NULL,
                ultimo_login NVARCHAR(60) NULL,
                role_class NVARCHAR(30) NULL,
                is_custom_role BIT NOT NULL CONSTRAINT DF_iv_rbac_custom DEFAULT 0
            );
            CREATE UNIQUE INDEX UX_iv_rbac_key ON dbo.informe_valor_rbac (client_id, natural_key_hash);
        END
        """,
        // Bitácora de entregas (F4 del plan de la entrega 3). A diferencia de las tres tablas de
        // insumo, ésta ACUMULA: reemitir un informe del mismo período es legítimo y el historial
        // importa, así que NO lleva unicidad por (client_id, period_start, period_end).
        //
        // El criterio que fija cada columna: en el archivo va todo lo que haga falta para que el
        // mismo informe, reemitido, dé el mismo resultado. Si un dato entra al cálculo y viene de
        // una fuente que cambia sola, o se guarda acá o la fila miente. De ahí salen las columnas
        // que el spec no nombraba (ver el comentario de cada una).
        """
        IF OBJECT_ID('dbo.informe_valor_entrega', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_entrega (
                entrega_id INT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_entrega_client REFERENCES dbo.clients(client_id),
                -- El contexto de cálculo completo (ContextoInformeValor): sin los cuatro, reemitir
                -- mide otra ventana.
                period_start DATE NOT NULL,
                period_end DATE NOT NULL,
                corte DATE NOT NULL,
                -- Tri-estado del spec 12.3.3, y por eso NVARCHAR nullable y no una tabla hija:
                -- NULL = el consultor no declaró nada (manda la heurística automática);
                -- '[]' = declaró "ningún mes parcial"; '["2026-01"]' = exactamente ésos.
                -- Guardar la lista vacía como NULL cambiaría el resultado al reemitir.
                meses_parciales NVARCHAR(2000) NULL,
                variante NVARCHAR(20) NOT NULL,
                -- JSON con las claves de los ocho bloques económicos aprobados. NUNCA NULL: '[]'
                -- significa "se generó sin aprobar ninguno", que es el default y un dato en sí.
                bloques_publicados NVARCHAR(400) NOT NULL,
                -- De dónde salió el insumo de RBAC ("base"/"archivo"/NULL si ninguna fuente tenía
                -- nada) y de cuándo era la corrida de Revisión de accesos: las dos cambian solas
                -- (una corrida nueva reemplaza a la anterior y mueve los ejes medidos, que deciden
                -- qué hallazgos de seguridad se emiten y cuáles se suprimen por eje no medido).
                rbac_origen NVARCHAR(20) NULL,
                rbac_corrida_fecha DATETIME2 NULL,
                -- La bandera por cliente de "la seguridad la gestiona otro": la mueve el consultor
                -- desde otra pantalla y saca el pilar 3 de Advisor y de la matriz sin dejar rastro
                -- en las cifras. Sin esto, dos emisiones con conteos distintos no se explican.
                seguridad_gestionada_externamente BIT NOT NULL
                    CONSTRAINT DF_iv_entrega_segext DEFAULT 0,
                -- Qué carga de cada insumo alimentó ESTA entrega. Los insumos son vivos (cada carga
                -- borra la anterior), así que las filas no se pueden restaurar; lo que sí se puede
                -- es DETECTAR que ya no son las mismas y decirlo, en vez de reemitir en silencio
                -- contra otro archivo. NULL = ese insumo no estaba cargado al generar.
                facturacion_ingesta_id INT NULL,
                casos_ingesta_id INT NULL,
                rbac_ingesta_id INT NULL,
                -- La foto de reservas (F4, heredada de la entrega 2d). Sin persistirla, reemitir un
                -- informe viejo lo recalcularía contra las reservas de HOY. NULL y una foto con
                -- Medido=false NO son lo mismo: NULL es "esta entrega es anterior a la foto",
                -- Medido=false es "se intentó leerlas y no se pudo", con su motivo adentro.
                foto_reservas_json NVARCHAR(MAX) NULL,
                -- Huella de la plantilla embebida que dibujó el artefacto. La plantilla cambia con
                -- el repo, no con los datos: dos emisiones idénticas que se ven distintas se
                -- explican mirando esta columna en vez de investigando las cifras.
                plantilla_version NVARCHAR(64) NULL,
                -- El contenedor de Blob Storage, junto al nombre del blob: es lo que ya hacen
                -- client_monthly_report.storage_container y analysis_files.storage_container. El
                -- contenedor sale de una variable de entorno (STORAGE_CONTAINER_OUTPUTS): deducirlo
                -- al descargar significa que cambiarla deja sin artefacto a todas las entregas
                -- archivadas, y el 404 no se puede explicar mirando la fila.
                blob_container NVARCHAR(200) NULL,
                blob_name NVARCHAR(400) NOT NULL,
                blob_size_bytes INT NOT NULL,
                file_name NVARCHAR(400) NOT NULL,
                summary_json NVARCHAR(MAX) NULL,
                generated_by NVARCHAR(200) NULL,
                generated_at DATETIME2 NOT NULL,
                CONSTRAINT CK_iv_entrega_rango CHECK (period_end >= period_start)
            );
            CREATE INDEX IX_iv_entrega_client ON dbo.informe_valor_entrega (client_id, generated_at DESC);
        END
        """,
        // soft-migration entrega. La tabla nace completa arriba, pero EnsureSchemaAsync corre sobre
        // bases que pueden haber aplicado una versión anterior de ESTE MISMO archivo (ya pasó dos
        // veces en este módulo: rows_merged y role_class). Y justamente estas columnas son las que
        // un informe reemitido necesita para no mentir: una base a la que le falten no falla, se
        // queda callada y devuelve otras cifras.
        """
        IF COL_LENGTH('dbo.informe_valor_entrega', 'foto_reservas_json') IS NULL
            ALTER TABLE dbo.informe_valor_entrega ADD foto_reservas_json NVARCHAR(MAX) NULL;
        """,
        """
        IF COL_LENGTH('dbo.informe_valor_entrega', 'rbac_corrida_fecha') IS NULL
            ALTER TABLE dbo.informe_valor_entrega ADD rbac_corrida_fecha DATETIME2 NULL;
        """,
        """
        IF COL_LENGTH('dbo.informe_valor_entrega', 'seguridad_gestionada_externamente') IS NULL
            ALTER TABLE dbo.informe_valor_entrega ADD seguridad_gestionada_externamente BIT NOT NULL
                CONSTRAINT DF_iv_entrega_segext DEFAULT 0;
        """,
        """
        IF COL_LENGTH('dbo.informe_valor_entrega', 'facturacion_ingesta_id') IS NULL
            ALTER TABLE dbo.informe_valor_entrega ADD facturacion_ingesta_id INT NULL;
        """,
        """
        IF COL_LENGTH('dbo.informe_valor_entrega', 'casos_ingesta_id') IS NULL
            ALTER TABLE dbo.informe_valor_entrega ADD casos_ingesta_id INT NULL;
        """,
        """
        IF COL_LENGTH('dbo.informe_valor_entrega', 'rbac_ingesta_id') IS NULL
            ALTER TABLE dbo.informe_valor_entrega ADD rbac_ingesta_id INT NULL;
        """,
        """
        IF COL_LENGTH('dbo.informe_valor_entrega', 'plantilla_version') IS NULL
            ALTER TABLE dbo.informe_valor_entrega ADD plantilla_version NVARCHAR(64) NULL;
        """,
        """
        IF COL_LENGTH('dbo.informe_valor_entrega', 'blob_container') IS NULL
            ALTER TABLE dbo.informe_valor_entrega ADD blob_container NVARCHAR(200) NULL;
        """,
        // soft-migration ingesta (tablas preexistentes de la entrega 1, ya en PR): la calculadora
        // publica "revisado línea por línea sobre N registros" y en la plantilla ese N son las
        // filas aceptadas ANTES de fusionar (ver BitcostParser.ParseResult.RowsMerged). Sin esta
        // columna la cifra sale por debajo del histórico y no hay forma de reproducirla desde la
        // base para una carga ya hecha antes de este fix.
        """
        IF COL_LENGTH('dbo.informe_valor_ingesta', 'rows_merged') IS NULL
            ALTER TABLE dbo.informe_valor_ingesta ADD rows_merged INT NOT NULL CONSTRAINT DF_iv_ingesta_merged DEFAULT 0;
        """,
        // soft-migration rbac (tabla preexistente de una entrega anterior, ya en PR): mientras la
        // clasificación de roles privilegiados fuera por el nombre del rol en inglés, que estas dos
        // columnas volvieran null/false al releer era inofensivo (SeguridadCalculador ni las
        // miraba). En cuanto la clasificación pasa a ser por RoleClass (alineada con Revisión de
        // accesos), una base sin estas columnas perdería la clase de TODO rol que llegue por el
        // Excel de respaldo, y los privilegiados se contarían con el respaldo por nombre -- el
        // defecto que ese cambio corrige, reaparecido solo para los clientes que suben el Excel.
        """
        IF COL_LENGTH('dbo.informe_valor_rbac', 'role_class') IS NULL
            ALTER TABLE dbo.informe_valor_rbac ADD role_class NVARCHAR(30) NULL;
        """,
        """
        IF COL_LENGTH('dbo.informe_valor_rbac', 'is_custom_role') IS NULL
            ALTER TABLE dbo.informe_valor_rbac ADD is_custom_role BIT NOT NULL CONSTRAINT DF_iv_rbac_custom DEFAULT 0;
        """,
    ];

    public static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        foreach (var sql in Statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
