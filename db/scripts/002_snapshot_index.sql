-- SnapshotIndex (solution design §7) — permanent audit-UI grid data source, one thin
-- row per snapshot, written once all required files for the snapshot are received. This
-- script is the source of truth for the schema: EF Core maps to it by hand and never
-- generates migrations. Idempotent in schema shape (safe to re-run at any time, always
-- ends in the same state) — NOT data-preserving, since it drops and recreates the table.
--
-- Year-based partitioning (design §7). The 11 boundaries below (RANGE RIGHT) create 12
-- partitions: dedicated partitions for 2026 through 2035 (10 years), with everything
-- before 2026-01-01 falling into the leftmost catch-all and everything from 2036-01-01
-- onward into the rightmost catch-all. This is a static dev-local window; ongoing
-- production boundary maintenance (adding future years, sliding-window merges) is out of
-- scope for this repository.

DROP TABLE IF EXISTS dbo.SnapshotIndex;

IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'PS_SnapshotIndex_Year')
    DROP PARTITION SCHEME PS_SnapshotIndex_Year;

IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'PF_SnapshotIndex_Year')
    DROP PARTITION FUNCTION PF_SnapshotIndex_Year;

CREATE PARTITION FUNCTION PF_SnapshotIndex_Year (DATETIME2(7))
AS RANGE RIGHT FOR VALUES
('2026-01-01', '2027-01-01', '2028-01-01', '2029-01-01', '2030-01-01',
 '2031-01-01', '2032-01-01', '2033-01-01', '2034-01-01', '2035-01-01',
 '2036-01-01');

CREATE PARTITION SCHEME PS_SnapshotIndex_Year
AS PARTITION PF_SnapshotIndex_Year ALL TO ([PRIMARY]);

-- PK_SnapshotIndex is NONCLUSTERED on SnapshotId alone: it preserves the exact
-- index-UPSERT uniqueness guarantee the completeness invariant relies on. SQL Server
-- requires the partition column in every *aligned* unique index, and SnapshotId must
-- stay independently unique without SnapshotDate baked into the key — so the clustered
-- (partition-aligned) index lives separately on SnapshotDate below.
CREATE TABLE dbo.SnapshotIndex
(
    -- SnapshotId/AccountId widths are mirrored by SnapshotFieldLimits (Application) and
    -- enforced pre-write by SnapshotEnvelopeValidator; EventType comes from the header blob
    -- instead, so SnapshotIndexEntryBuilder.ExtractEventType falls back to an empty string
    -- rather than handing this column an over-long value. Changing a width means changing
    -- this script, SnapshotFieldLimits and SnapshotIndexEntityConfiguration together.
    SnapshotId   VARCHAR(100)  NOT NULL CONSTRAINT PK_SnapshotIndex PRIMARY KEY NONCLUSTERED,
    AccountId    VARCHAR(100)  NOT NULL,
    SnapshotDate DATETIME2     NOT NULL,
    EventType    VARCHAR(100)  NOT NULL,
    AdlsPath     VARCHAR(MAX)  NOT NULL,
    DisplayData  NVARCHAR(MAX) NOT NULL,
    CreatedAt    DATETIME2     NOT NULL
);

-- The clustered index on SnapshotDate is what actually partitions the table.
CREATE CLUSTERED INDEX CIX_SnapshotIndex_SnapshotDate
    ON dbo.SnapshotIndex (SnapshotDate)
    ON PS_SnapshotIndex_Year (SnapshotDate);

-- Secondary indexes auto-align (SnapshotDate is a key column in both), so no ON clause.
CREATE INDEX IX_SnapshotIndex_AccountId_SnapshotDate ON dbo.SnapshotIndex (AccountId, SnapshotDate DESC);
CREATE INDEX IX_SnapshotIndex_EventType_SnapshotDate ON dbo.SnapshotIndex (EventType, SnapshotDate DESC);
