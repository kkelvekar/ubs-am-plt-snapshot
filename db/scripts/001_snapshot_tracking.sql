-- SnapshotTracking (solution design §6) — transient completeness tracking with a
-- 30-day rolling retention, applied by the cleanup job (out of scope for this repo).
-- This script is the source of truth for the schema: EF Core maps to it by hand and
-- never generates migrations. Idempotent — safe to re-run at any time.
--
-- MissingFiles, DeclaredFailedAt and Alerted are written only by the daily cleanup
-- job (not built in this repository); they exist here so the schema is complete.

DROP TABLE IF EXISTS dbo.SnapshotTracking;

CREATE TABLE dbo.SnapshotTracking
(
    SnapshotId       VARCHAR(50)   NOT NULL CONSTRAINT PK_SnapshotTracking PRIMARY KEY,
    AccountId        VARCHAR(20)   NOT NULL,
    SnapshotType     VARCHAR(50)   NOT NULL,
    AdlsRootPath     VARCHAR(MAX)  NOT NULL,
    ReceivedFiles    NVARCHAR(MAX) NOT NULL,  -- JSON array of received filenames
    MissingFiles     NVARCHAR(MAX) NULL,      -- JSON array, populated only when status = FAILED
    Status           VARCHAR(20)   NOT NULL,  -- RECEIVING / COMPLETE / FAILED
    FirstReceivedAt  DATETIME2     NOT NULL,
    LastUpdatedAt    DATETIME2     NOT NULL,
    CompletedAt      DATETIME2     NULL,
    DeclaredFailedAt DATETIME2     NULL,
    Alerted          BIT           NOT NULL CONSTRAINT DF_SnapshotTracking_Alerted DEFAULT (0)
);

-- Cleanup job scan for stale RECEIVING rows
CREATE INDEX IX_SnapshotTracking_StatusLastUpdatedAt
    ON dbo.SnapshotTracking (Status, LastUpdatedAt);

-- 30-day purge scan
CREATE INDEX IX_SnapshotTracking_LastUpdatedAt
    ON dbo.SnapshotTracking (LastUpdatedAt);
