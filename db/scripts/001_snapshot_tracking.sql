-- snapshot_tracking (solution design §6) — transient completeness tracking with a
-- 30-day rolling retention, applied by the cleanup job (out of scope for this repo).
-- This script is the source of truth for the schema: EF Core maps to it by hand and
-- never generates migrations. Idempotent — safe to re-run at any time.
--
-- missing_files, declared_failed_at and alerted are written only by the daily cleanup
-- job (not built in this repository); they exist here so the schema is complete.

IF OBJECT_ID(N'dbo.snapshot_tracking', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.snapshot_tracking
    (
        snapshot_id        VARCHAR(50)    NOT NULL CONSTRAINT pk_snapshot_tracking PRIMARY KEY,
        account_id         VARCHAR(20)    NOT NULL,
        snapshot_type      VARCHAR(50)    NOT NULL,
        adls_root_path     VARCHAR(500)   NOT NULL,
        received_files     NVARCHAR(1000) NOT NULL,  -- JSON array of received filenames
        missing_files      NVARCHAR(1000) NULL,      -- JSON array, populated only when status = FAILED
        status             VARCHAR(20)    NOT NULL,  -- RECEIVING / COMPLETE / FAILED
        first_received_at  DATETIME2      NOT NULL,
        last_updated_at    DATETIME2      NOT NULL,
        completed_at       DATETIME2      NULL,
        declared_failed_at DATETIME2      NULL,
        alerted            BIT            NOT NULL CONSTRAINT df_snapshot_tracking_alerted DEFAULT (0)
    );
END;

-- Cleanup job scan for stale RECEIVING rows
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'ix_tracking_status_updated'
      AND object_id = OBJECT_ID(N'dbo.snapshot_tracking'))
BEGIN
    CREATE INDEX ix_tracking_status_updated
        ON dbo.snapshot_tracking (status, last_updated_at);
END;

-- 30-day purge scan
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'ix_tracking_last_updated'
      AND object_id = OBJECT_ID(N'dbo.snapshot_tracking'))
BEGIN
    CREATE INDEX ix_tracking_last_updated
        ON dbo.snapshot_tracking (last_updated_at);
END;
