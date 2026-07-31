-- SnapshotTracking (solution design §6) — transient completeness tracking with a 30-day
-- rolling retention applied by the cleanup job, which is out of scope for this repository.
-- This script is the source of truth for the schema: EF Core maps to it by hand and never
-- generates migrations. Safe to re-run, but not data-preserving: it drops and recreates the
-- table.
--
-- MissingFiles and Alerted are written only by the cleanup job; they exist here so the schema
-- is complete. DeclaredFailedAt and Reason are written by that job and by the write path,
-- which records a rejected message as a FAILED row and clears both again if a later valid
-- message recovers the snapshot.

DROP TABLE IF EXISTS dbo.SnapshotTracking;

CREATE TABLE dbo.SnapshotTracking
(
    -- The three identity column widths are mirrored by SnapshotFieldLimits and enforced
    -- before the first write, so an over-long value is rejected rather than failing this
    -- INSERT. Change this script, SnapshotFieldLimits and SnapshotTrackingEntityConfiguration
    -- together.
    SnapshotId       VARCHAR(100)  NOT NULL CONSTRAINT PK_SnapshotTracking PRIMARY KEY,
    AccountId        VARCHAR(100)  NOT NULL,
    SnapshotType     VARCHAR(100)  NOT NULL,
    AdlsRootPath     VARCHAR(MAX)  NOT NULL,
    ReceivedFiles    NVARCHAR(MAX) NOT NULL,  -- JSON array of received filenames
    MissingFiles     NVARCHAR(MAX) NULL,      -- JSON array, populated only when status = FAILED
    Status           VARCHAR(20)   NOT NULL,  -- RECEIVING / COMPLETE / FAILED
    Reason           NVARCHAR(MAX) NULL,      -- "{reasonCode}: {detail}", populated only when status = FAILED
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
