-- snapshot_index (solution design §7) — permanent audit-UI grid data source, one thin
-- row per snapshot, written once all required files for the snapshot are received. This
-- script is the source of truth for the schema: EF Core maps to it by hand and never
-- generates migrations. Idempotent — safe to re-run at any time.
--
-- No table partitioning here — explicitly out of scope for this repository.

IF OBJECT_ID(N'dbo.snapshot_index', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.snapshot_index
    (
        snapshot_id   VARCHAR(50)   NOT NULL CONSTRAINT pk_snapshot_index PRIMARY KEY,
        account_id    VARCHAR(20)   NOT NULL,
        snapshot_date DATETIME2     NOT NULL,
        event_type    VARCHAR(50)   NOT NULL,
        adls_path     VARCHAR(500)  NOT NULL,
        display_data  NVARCHAR(MAX) NOT NULL,
        created_at    DATETIME2     NOT NULL
    );
END;

-- stage was removed from the index row (emitted in structured logs instead); drop it
-- from databases created before the change.
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.snapshot_index') AND name = N'stage')
    ALTER TABLE dbo.snapshot_index DROP COLUMN stage;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_index_account_snapshotdate' AND object_id = OBJECT_ID(N'dbo.snapshot_index'))
    CREATE INDEX ix_index_account_snapshotdate ON dbo.snapshot_index (account_id, snapshot_date DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_index_eventtype_snapshotdate' AND object_id = OBJECT_ID(N'dbo.snapshot_index'))
    CREATE INDEX ix_index_eventtype_snapshotdate ON dbo.snapshot_index (event_type, snapshot_date DESC);
