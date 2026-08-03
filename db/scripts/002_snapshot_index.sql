-- PortfolioSnapshotIndex (solution design §7) — permanent audit-UI grid data source, one thin row per
-- snapshot, written once all required files for the snapshot are received. This script is the
-- source of truth for the schema: EF Core maps to it by hand and never generates migrations.
-- Safe to re-run, but not data-preserving: it drops and recreates the table.
--
-- Year-based partitioning (design §7). The 11 RANGE RIGHT boundaries below create 12
-- partitions: one per year for 2026 through 2035, plus a catch-all at each end. This is a
-- static dev-local window; production boundary maintenance is out of scope for this
-- repository.

-- Legacy cleanup: the table and its partition objects used to be named SnapshotIndex, so a
-- database created by an earlier run of this script still carries those names. Drop both the
-- old and the new names so the script re-runs cleanly either way.
DROP TABLE IF EXISTS dbo.SnapshotIndex;
DROP TABLE IF EXISTS dbo.PortfolioSnapshotIndex;

IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'PS_SnapshotIndex_Year')
    DROP PARTITION SCHEME PS_SnapshotIndex_Year;

IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'PF_SnapshotIndex_Year')
    DROP PARTITION FUNCTION PF_SnapshotIndex_Year;

IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'PS_PortfolioSnapshotIndex_Year')
    DROP PARTITION SCHEME PS_PortfolioSnapshotIndex_Year;

IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'PF_PortfolioSnapshotIndex_Year')
    DROP PARTITION FUNCTION PF_PortfolioSnapshotIndex_Year;

CREATE PARTITION FUNCTION PF_PortfolioSnapshotIndex_Year (DATETIME2(7))
AS RANGE RIGHT FOR VALUES
('2026-01-01', '2027-01-01', '2028-01-01', '2029-01-01', '2030-01-01',
 '2031-01-01', '2032-01-01', '2033-01-01', '2034-01-01', '2035-01-01',
 '2036-01-01');

CREATE PARTITION SCHEME PS_PortfolioSnapshotIndex_Year
AS PARTITION PF_PortfolioSnapshotIndex_Year ALL TO ([PRIMARY]);

-- PK_PortfolioSnapshotIndex is NONCLUSTERED on SnapshotId alone so that SnapshotId stays
-- independently unique, which the index UPSERT relies on. SQL Server requires the partition
-- column in every aligned unique index, so the partition-aligned clustered index lives
-- separately on SnapshotDate below.
CREATE TABLE dbo.PortfolioSnapshotIndex
(
    -- SnapshotId and AccountId widths are mirrored by SnapshotFieldLimits and enforced before
    -- the first write. EventType comes from the header blob instead, so
    -- SnapshotIndexEntryBuilder.ExtractEventType falls back to an empty string rather than
    -- handing this column an over-long value. Change this script, SnapshotFieldLimits and
    -- SnapshotIndexEntityConfiguration together.
    SnapshotId   VARCHAR(100)  NOT NULL CONSTRAINT PK_PortfolioSnapshotIndex PRIMARY KEY NONCLUSTERED,
    AccountId    VARCHAR(100)  NOT NULL,
    SnapshotDate DATETIME2     NOT NULL,
    EventType    VARCHAR(100)  NOT NULL,
    AdlsPath     VARCHAR(MAX)  NOT NULL,
    DisplayData  NVARCHAR(MAX) NOT NULL,
    CreatedAt    DATETIME2     NOT NULL
);

-- The clustered index on SnapshotDate is what actually partitions the table.
CREATE CLUSTERED INDEX CIX_PortfolioSnapshotIndex_SnapshotDate
    ON dbo.PortfolioSnapshotIndex (SnapshotDate)
    ON PS_PortfolioSnapshotIndex_Year (SnapshotDate);

-- Secondary indexes auto-align (SnapshotDate is a key column in both), so no ON clause.
CREATE INDEX IX_PortfolioSnapshotIndex_AccountId_SnapshotDate ON dbo.PortfolioSnapshotIndex (AccountId, SnapshotDate DESC);
CREATE INDEX IX_PortfolioSnapshotIndex_EventType_SnapshotDate ON dbo.PortfolioSnapshotIndex (EventType, SnapshotDate DESC);
