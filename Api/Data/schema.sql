-- T-SQL schema for the CSUB Admissions Roadmap (SQL Server).
--
-- This is the SQL Server translation of the old Postgres schema in
-- server/db/init.ts. It is run once on startup by SchemaInitializer and is
-- idempotent (safe to re-run): every object is guarded by an existence check.
--
-- Translation notes (Postgres -> T-SQL):
--   SERIAL                -> INT IDENTITY(1,1)
--   TEXT                  -> NVARCHAR(MAX), or sized NVARCHAR for keys/short cols
--   TIMESTAMPTZ           -> DATETIME2 (UTC), default SYSUTCDATETIME()
--   BOOLEAN               -> BIT
--   integer-boolean flags -> INT (kept as 0/1 to preserve the JSON contract)
--   partial unique index on lower(trim(col)) -> persisted computed column + filtered unique index
--
-- These SET options are REQUIRED before creating persisted computed columns and
-- filtered indexes (sqlcmd defaults QUOTED_IDENTIFIER OFF; SqlClient defaults it ON).
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

------------------------------------------------------------------- schema_version
-- Append-only history of which schema versions have been applied (auditable, never
-- destructive). SchemaInitializer records the current version on startup.
IF OBJECT_ID('dbo.schema_version', 'U') IS NULL
CREATE TABLE dbo.schema_version (
    version     NVARCHAR(64) NOT NULL PRIMARY KEY,
    applied_at  DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME()
);

------------------------------------------------------------------- students
IF OBJECT_ID('dbo.students', 'U') IS NULL
CREATE TABLE dbo.students (
    id                 NVARCHAR(128) NOT NULL PRIMARY KEY,
    display_name       NVARCHAR(256) NULL,
    email              NVARCHAR(256) NULL,
    azure_id           NVARCHAR(128) NULL,
    tags               NVARCHAR(MAX) NULL,
    emplid             NVARCHAR(64)  NULL,
    preferred_name     NVARCHAR(256) NULL,
    phone              NVARCHAR(64)  NULL,
    applicant_type     NVARCHAR(64)  NULL,
    major              NVARCHAR(128) NULL,
    residency          NVARCHAR(64)  NULL,
    admit_term         NVARCHAR(64)  NULL,
    term_id            INT           NULL,
    last_synced_at     DATETIME2     NULL,
    last_api_check_at  DATETIME2     NULL,
    created_at         DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    -- normalized emplid for case/space-insensitive uniqueness (computed key column)
    emplid_norm        AS LOWER(LTRIM(RTRIM(emplid))) PERSISTED
);

-- nullable-unique azure_id (SQL Server unique indexes reject multiple NULLs, so filter them out)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_students_azure_id_unique')
CREATE UNIQUE INDEX idx_students_azure_id_unique
    ON dbo.students (azure_id)
    WHERE azure_id IS NOT NULL;

-- one row per real Student ID # (emplid), case/space-insensitive
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_students_emplid_unique')
CREATE UNIQUE INDEX idx_students_emplid_unique
    ON dbo.students (emplid_norm)
    WHERE emplid IS NOT NULL AND emplid <> '';

------------------------------------------------------------------- terms
IF OBJECT_ID('dbo.terms', 'U') IS NULL
CREATE TABLE dbo.terms (
    id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name        NVARCHAR(128) NOT NULL,
    start_date  NVARCHAR(32)  NULL,
    end_date    NVARCHAR(32)  NULL,
    is_active   INT           NOT NULL DEFAULT 1,
    created_at  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

------------------------------------------------------------------- steps
IF OBJECT_ID('dbo.steps', 'U') IS NULL
CREATE TABLE dbo.steps (
    id                INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    title             NVARCHAR(512) NOT NULL,
    description       NVARCHAR(MAX) NULL,
    icon              NVARCHAR(64)  NULL,
    sort_order        INT           NOT NULL,
    deadline          NVARCHAR(128) NULL,            -- legacy free-text deadline
    deadline_date     NVARCHAR(32)  NULL,            -- 'YYYY-MM-DD'
    guide_content     NVARCHAR(MAX) NULL,
    links             NVARCHAR(MAX) NULL,            -- JSON array
    required_tags     NVARCHAR(MAX) NULL,            -- JSON array
    required_tag_mode NVARCHAR(16)  NULL DEFAULT 'any',
    excluded_tags     NVARCHAR(MAX) NULL,            -- JSON array
    contact_info      NVARCHAR(MAX) NULL,            -- JSON object
    term_id           INT           NULL,
    step_key          NVARCHAR(128) NULL,
    is_public         INT           NULL DEFAULT 0,
    is_optional       INT           NULL DEFAULT 0,
    is_active         INT           NULL DEFAULT 1
);

-- unique step_key per term (only where both are present)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_steps_term_step_key_unique')
CREATE UNIQUE INDEX idx_steps_term_step_key_unique
    ON dbo.steps (term_id, step_key)
    WHERE term_id IS NOT NULL AND step_key IS NOT NULL AND step_key <> '';

-- fast lookup by (term_id, step_key)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_steps_step_key_lookup')
CREATE INDEX idx_steps_step_key_lookup ON dbo.steps (term_id, step_key);

------------------------------------------------------------------- student_progress
IF OBJECT_ID('dbo.student_progress', 'U') IS NULL
CREATE TABLE dbo.student_progress (
    student_id    NVARCHAR(128) NOT NULL,
    step_id       INT           NOT NULL,
    completed_at  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    status        NVARCHAR(20)  NULL DEFAULT 'completed',  -- 'completed' | 'waived' | 'not_completed'
    note          NVARCHAR(MAX) NULL,
    completed_by  NVARCHAR(20)  NULL DEFAULT 'manual',     -- 'manual' | 'integration' | 'api_check' | 'auto'
    CONSTRAINT pk_student_progress PRIMARY KEY (student_id, step_id),
    CONSTRAINT fk_progress_student FOREIGN KEY (student_id) REFERENCES dbo.students (id),
    CONSTRAINT fk_progress_step    FOREIGN KEY (step_id)    REFERENCES dbo.steps (id)
);

------------------------------------------------------------------- admin_users
IF OBJECT_ID('dbo.admin_users', 'U') IS NULL
CREATE TABLE dbo.admin_users (
    id             INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    email          NVARCHAR(256) NOT NULL CONSTRAINT uq_admin_users_email UNIQUE,
    password_hash  NVARCHAR(256) NOT NULL,
    role           NVARCHAR(32)  NOT NULL DEFAULT 'viewer',  -- viewer | admissions | admissions_editor | sysadmin
    display_name   NVARCHAR(256) NOT NULL,
    is_active      INT           NOT NULL DEFAULT 1,
    azure_id       NVARCHAR(128) NULL,
    created_at     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_admin_users_azure_id_unique')
CREATE UNIQUE INDEX idx_admin_users_azure_id_unique
    ON dbo.admin_users (azure_id)
    WHERE azure_id IS NOT NULL;

------------------------------------------------------------------- audit_log
IF OBJECT_ID('dbo.audit_log', 'U') IS NULL
CREATE TABLE dbo.audit_log (
    id           INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    entity_type  NVARCHAR(64)  NOT NULL,
    entity_id    NVARCHAR(128) NOT NULL,
    action       NVARCHAR(64)  NOT NULL,
    changed_by   NVARCHAR(256) NOT NULL,
    details      NVARCHAR(MAX) NULL,                       -- JSON
    created_at   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_audit_entity')
CREATE INDEX idx_audit_entity ON dbo.audit_log (entity_type, entity_id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_audit_created')
CREATE INDEX idx_audit_created ON dbo.audit_log (created_at DESC);

------------------------------------------------------------------- integration_clients
IF OBJECT_ID('dbo.integration_clients', 'U') IS NULL
CREATE TABLE dbo.integration_clients (
    id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name        NVARCHAR(128) NOT NULL CONSTRAINT uq_integration_clients_name UNIQUE,
    key_hash    NVARCHAR(256) NOT NULL,
    is_active   INT           NOT NULL DEFAULT 1,
    created_at  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);

------------------------------------------------------------------- integration_events
IF OBJECT_ID('dbo.integration_events', 'U') IS NULL
CREATE TABLE dbo.integration_events (
    id                     INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    integration_client_id  INT           NOT NULL,
    source_event_id        NVARCHAR(128) NOT NULL,
    student_id_number      NVARCHAR(64)  NULL,
    step_key               NVARCHAR(128) NULL,
    request_body           NVARCHAR(MAX) NULL,
    response_status        INT           NOT NULL,
    response_body          NVARCHAR(MAX) NOT NULL,
    created_at             DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT fk_integration_events_client
        FOREIGN KEY (integration_client_id) REFERENCES dbo.integration_clients (id)
);

-- idempotency: one row per (client, source_event_id)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_integration_events_unique')
CREATE UNIQUE INDEX idx_integration_events_unique
    ON dbo.integration_events (integration_client_id, source_event_id);

------------------------------------------------------------------- step_api_checks
IF OBJECT_ID('dbo.step_api_checks', 'U') IS NULL
CREATE TABLE dbo.step_api_checks (
    id                   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    step_id              INT           NOT NULL CONSTRAINT uq_step_api_checks_step UNIQUE,
    is_enabled           BIT           NOT NULL DEFAULT 0,
    http_method          NVARCHAR(10)  NOT NULL DEFAULT 'GET',
    url                  NVARCHAR(2048) NOT NULL,
    auth_type            NVARCHAR(20)  NOT NULL DEFAULT 'none',  -- none | basic | bearer
    auth_credentials     NVARCHAR(MAX) NULL,                     -- encrypted JSON
    headers              NVARCHAR(MAX) NULL,                     -- JSON object
    student_param_name   NVARCHAR(100) NOT NULL DEFAULT 'studentId',
    student_param_source NVARCHAR(50)  NOT NULL DEFAULT 'emplid',
    response_field_path  NVARCHAR(255) NOT NULL,
    created_at           DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at           DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT fk_step_api_checks_step
        FOREIGN KEY (step_id) REFERENCES dbo.steps (id) ON DELETE CASCADE
);
