<#
.SYNOPSIS
  Dev convenience script — NOT an infrastructure artifact.

  Installs/removes THROW triggers on the dev Azure SQL database to simulate a
  SQL-write failure at a specific point in the write order (blob write ->
  tracking upsert -> completeness check -> index UPSERT -> offset commit),
  so the offset-commit-timing and consumer-restart-recovery invariants can be
  verified against the REAL Kafka consume/commit path (Mode B — see
  AGENTS.md). Targets ONLY two triggers, both idempotent to install/remove:

    trg_fault_snapshot_tracking  on dbo.snapshot_tracking, AFTER INSERT, UPDATE
                                  (SELECT is untouched)
    trg_fault_snapshot_index     on dbo.snapshot_index, AFTER INSERT, UPDATE

  Safe to re-run -Install / -Remove any number of times. -Status reports
  which fault triggers currently exist. -VerifyClean exits non-zero if any
  fault trigger is still present (use this as the final check before ending
  a session against the shared dev database).

.PARAMETER Install
  Create the specified trigger(s) (idempotent — drops and recreates).

.PARAMETER Remove
  Drop the specified trigger(s) if present (idempotent — no-op if absent).

.PARAMETER Target
  Which table's fault trigger to act on: Tracking, Index, or Both (default: Both).

.PARAMETER Status
  List fault triggers currently installed on the dev database.

.PARAMETER VerifyClean
  Exit 0 if no fault triggers exist, exit 1 (and print the offending
  trigger names) otherwise. Always run this before finishing a live-test
  session against the shared dev database.

.PARAMETER Server
  Azure SQL logical server. Defaults to sql-kk-tier1-dev-uksouth.database.windows.net.

.PARAMETER Database
  Azure SQL database name. Defaults to platform-core-db-dev.

.EXAMPLE
  ./fault-injection.ps1 -Install -Target Tracking
  ./fault-injection.ps1 -Remove -Target Tracking
  ./fault-injection.ps1 -Status
  ./fault-injection.ps1 -VerifyClean

.NOTES
  ================================================================================
  LIVE-TEST PROCEDURES (Mode B — TC-13 / TC-14 / TC-15)
  ================================================================================
  Preconditions (all TCs):
    - Local Kafka broker up:            ./tools/kafka-local.ps1 -Up
    - Dev Azure SQL schema applied:     ./tools/apply-schema-azure.ps1
    - az login already authenticated (worker/tools use DefaultAzureCredential
      and Active Directory Default against the dev Azure SQL DB and ADLS Gen2
      container configured in src/UBS.AM.PLT.SnapshotWriter.Worker/appsettings.json
      — no Azurite/local SQL container needed for these TCs).
    - Worker bootstrap servers configurable via Kafka__BootstrapServers env var
      (defaults to localhost:9092, matching kafka-local.ps1).
    - Producer:
        dotnet run --project tools/UBS.AM.PLT.SnapshotWriter.TestProducer -- \
          --snapshots 1 --message-delay 00:00:05
      (4 payload messages per snapshot: header, instruments, calculations, settings;
      fresh snapshotId per run: corr<timestamp>-0001 — see SnapshotGenerator.cs)
    - Consumer-group offset/lag inspection (adjust bin path only if the image
      layout differs — verified against apache/kafka:3.9.1 as used by
      tools/kafka-local.ps1, container name snapshot-writer-kafka):
        docker exec snapshot-writer-kafka /opt/kafka/bin/kafka-consumer-groups.sh \
          --bootstrap-server localhost:9092 --describe --group snapshot-writer-api

  --------------------------------------------------------------------------
  TC-13 — fault between blob write and tracking write
  --------------------------------------------------------------------------
    1. ./fault-injection.ps1 -Install -Target Tracking
    2. Start the worker (dotnet run --project src/UBS.AM.PLT.SnapshotWriter.Worker),
       then publish one snapshot's messages with the producer above.
    3. Verify while the fault is active:
         - first payload's blob exists in ADLS at the expected path
         - no snapshot_tracking row for the snapshotId
         - worker logs show "Failed to process message ... attempt N" + retry cadence
         - kafka-consumer-groups.sh --describe shows LAG > 0 / committed offset
           unchanged for the partition carrying this snapshotId
         - no "Committed offset" log line for that message
    4. ./fault-injection.ps1 -Remove -Target Tracking
       Verify: retry succeeds without restarting the worker, tracking rows appear
       for all 4 payloads, the index row is written on the 4th, lag returns to 0,
       and "Committed offset" is logged per message only after the corresponding
       SQL write is confirmed.

  --------------------------------------------------------------------------
  TC-14 — fault between completeness check and index write
  --------------------------------------------------------------------------
    1. ./fault-injection.ps1 -Install -Target Index
    2. Start the worker, publish one full snapshot (4 payloads).
    3. Verify: the first 3 (non-completing) messages commit normally (lag 0
       between them). On the 4th (completing) message:
         - all 4 blobs present in ADLS
         - snapshot_tracking row shows all 4 files received but status still
           RECEIVING (proves index-write-before-MarkComplete ordering)
         - no snapshot_index row for the snapshotId
         - offset for the completing message NOT committed (lag = 1)
         - failure + retry-cadence logs for the completing message
    4. ./fault-injection.ps1 -Remove -Target Index
       Verify: retry completes — exactly one snapshot_index row, tracking
       status COMPLETE, offset committed, lag back to 0.

  --------------------------------------------------------------------------
  TC-15 — restart / new consumer instance
  --------------------------------------------------------------------------
    1. No triggers installed. Start the worker (note its PID), publish one
       snapshot with enough delay to kill/restart mid-stream, e.g.:
         dotnet run --project tools/UBS.AM.PLT.SnapshotWriter.TestProducer -- \
           --snapshots 1 --message-delay 00:00:20
    2. After at least 1 payload has committed (watch worker log / consumer-group
       describe for lag dropping), kill the worker process. Confirm via
       kafka-consumer-groups.sh --describe that the committed offset covers
       only the already-processed payload(s) for that partition.
    3. Start a brand-new worker process (new PID). Verify:
         - remaining payloads are consumed and processed
         - the snapshot completes with the SAME adls_root_path as the
           messages processed before the restart (proves the root path is
           re-derived from SQL tracking state, not held in worker memory)
         - exactly one snapshot_index row for the snapshotId
         - snapshot_tracking status COMPLETE
         - lag returns to 0
         - no duplicate blobs at the root path

  --------------------------------------------------------------------------
  CLEANUP (mandatory — shared dev database)
  --------------------------------------------------------------------------
    ./fault-injection.ps1 -Remove -Target Both
    ./fault-injection.ps1 -VerifyClean
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Remove,
    [switch]$Status,
    [switch]$VerifyClean,
    [ValidateSet('Tracking', 'Index', 'Both')]
    [string]$Target = 'Both',
    [string]$Server = 'sql-kk-tier1-dev-uksouth.database.windows.net',
    [string]$Database = 'platform-core-db-dev'
)

$ErrorActionPreference = 'Stop'

$triggerNames = @{
    Tracking = 'trg_fault_snapshot_tracking'
    Index    = 'trg_fault_snapshot_index'
}

$triggerDdl = @{
    Tracking = @"
CREATE TRIGGER dbo.trg_fault_snapshot_tracking
ON dbo.snapshot_tracking
AFTER INSERT, UPDATE
AS
BEGIN
    THROW 51000, 'fault-injection: simulated failure on snapshot_tracking write', 1;
END
"@
    Index    = @"
CREATE TRIGGER dbo.trg_fault_snapshot_index
ON dbo.snapshot_index
AFTER INSERT, UPDATE
AS
BEGIN
    THROW 51001, 'fault-injection: simulated failure on snapshot_index write', 1;
END
"@
}

function Get-AccessToken {
    $token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
    if (-not $token) {
        throw 'Could not acquire an access token. Run `az login` first.'
    }
    return $token
}

function Invoke-Sql {
    param([string]$Query, [string]$Token)
    Invoke-Sqlcmd -ServerInstance $Server -Database $Database -AccessToken $Token -ConnectionTimeout 120 -Query $Query
}

function Get-Targets {
    if ($Target -eq 'Both') { return @('Tracking', 'Index') }
    return @($Target)
}

if (-not (Get-Module -ListAvailable SqlServer)) {
    Write-Host 'Installing the SqlServer PowerShell module (current user)...'
    Install-Module SqlServer -Scope CurrentUser -Force -AllowClobber
}

$modeCount = @($Install, $Remove, $Status, $VerifyClean) | Where-Object { $_ } | Measure-Object | Select-Object -ExpandProperty Count
if ($modeCount -ne 1) {
    Write-Host 'Usage: ./fault-injection.ps1 -Install|-Remove|-Status|-VerifyClean [-Target Tracking|Index|Both]'
    Write-Host 'See the comment-based help (Get-Help ./fault-injection.ps1 -Full) for the full TC-13/14/15 run procedures.'
    exit 1
}

$token = Get-AccessToken

if ($VerifyClean) {
    $existing = Invoke-Sql -Token $token -Query "SELECT name FROM sys.triggers WHERE name IN ('trg_fault_snapshot_tracking', 'trg_fault_snapshot_index')"
    if ($existing) {
        Write-Host 'FAULT TRIGGERS STILL PRESENT:' -ForegroundColor Red
        $existing | ForEach-Object { Write-Host "  $($_.name)" -ForegroundColor Red }
        exit 1
    }
    Write-Host 'Clean: no fault triggers present on the dev database.' -ForegroundColor Green
    exit 0
}

if ($Status) {
    $existing = Invoke-Sql -Token $token -Query "SELECT name, OBJECT_NAME(parent_id) AS table_name FROM sys.triggers WHERE name IN ('trg_fault_snapshot_tracking', 'trg_fault_snapshot_index')"
    if (-not $existing) {
        Write-Host 'No fault triggers currently installed.'
    } else {
        $existing | ForEach-Object { Write-Host "  $($_.name) on $($_.table_name)" }
    }
    exit 0
}

foreach ($t in Get-Targets) {
    $name = $triggerNames[$t]

    if ($Install) {
        Write-Host "Installing fault trigger $name on dbo.snapshot_$($t.ToLower())..."
        # Idempotent: drop first (no-op if absent) then create.
        Invoke-Sql -Token $token -Query "IF OBJECT_ID(N'dbo.$name', N'TR') IS NOT NULL DROP TRIGGER dbo.$name"
        Invoke-Sql -Token $token -Query $triggerDdl[$t]
        Write-Host "  installed."
    }

    if ($Remove) {
        Write-Host "Removing fault trigger $name (if present)..."
        Invoke-Sql -Token $token -Query "IF OBJECT_ID(N'dbo.$name', N'TR') IS NOT NULL DROP TRIGGER dbo.$name"
        Write-Host "  removed (or was already absent)."
    }
}

$remaining = Invoke-Sql -Token $token -Query "SELECT name FROM sys.triggers WHERE name IN ('trg_fault_snapshot_tracking', 'trg_fault_snapshot_index')"
if ($remaining) {
    Write-Host 'Fault triggers currently installed:'
    $remaining | ForEach-Object { Write-Host "  $($_.name)" }
} else {
    Write-Host 'No fault triggers currently installed.'
}
