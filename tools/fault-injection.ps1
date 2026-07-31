<#
.SYNOPSIS
  Dev convenience script — NOT an infrastructure artifact.

  Installs/removes fault triggers on the dev Azure SQL database to simulate
  failures at specific points in the write order (blob write -> tracking
  upsert -> completeness check -> index UPSERT -> offset commit), so the
  offset-commit-timing and consumer-restart-recovery invariants can be
  verified against the REAL Kafka consume/commit path (Mode B — see
  AGENTS.md). Targets THREE triggers, all idempotent to install/remove:

    trg_fault_SnapshotTracking   on dbo.SnapshotTracking, AFTER INSERT, UPDATE
                                  — THROWs, simulating a hard tracking-write failure
                                  (SELECT is untouched)
    trg_fault_SnapshotIndex      on dbo.SnapshotIndex, AFTER INSERT, UPDATE
                                  — THROWs, simulating a hard index-write failure
    trg_delay_SnapshotIndex      on dbo.SnapshotIndex, AFTER INSERT ONLY
                                  — does NOT throw: WAITFOR DELAY '00:00:20' then
                                  returns successfully, simulating a slow-but-
                                  succeeding index write (TC-20 — offset commit
                                  races the delay). AFTER INSERT only (not UPDATE)
                                  because the index write is an EF Core UPSERT: the
                                  first (completing) write is always a physical
                                  INSERT; redelivery of an already-COMPLETE
                                  snapshot's header message is a physical UPDATE
                                  and must NOT be delayed again — this is Part 1's
                                  proof recreated live.

  Safe to re-run -Install / -Remove any number of times. -Status reports
  which fault triggers currently exist. -VerifyClean exits non-zero if any
  fault trigger is still present (use this as the final check before ending
  a session against the shared dev database).

.PARAMETER Install
  Create the specified trigger(s) (idempotent — drops and recreates).

.PARAMETER Remove
  Drop the specified trigger(s) if present (idempotent — no-op if absent).

.PARAMETER Target
  Which fault trigger to act on: Tracking, Index, Delay, or Both (default: Both).
  Both = Tracking + Index (THROW triggers only, matching the original TC-13/14
  procedures) — Delay is never implied by Both and must be selected explicitly,
  since it is a different kind of fault (non-throwing, single-shot) used for a
  different scenario (TC-20). -Status and -VerifyClean always report/verify all
  three trigger names regardless of -Target.

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
  ./fault-injection.ps1 -Install -Target Delay
  ./fault-injection.ps1 -Remove -Target Delay
  ./fault-injection.ps1 -Status
  ./fault-injection.ps1 -VerifyClean

.NOTES
  ================================================================================
  LIVE-TEST PROCEDURES (Mode B — TC-13 / TC-14 / TC-15)
  ================================================================================
  CONSUMER FAILURE SEMANTICS (applies to every TC below — design doc §8/§9):
    A failing message is retried IN-PROCESS, one attempt per configured
    Kafka:RetryDelays entry (0s, 5s, 30s => 3 attempts, ~35s total). On the
    final failure the worker logs ONE Critical "Operations alert ..." line,
    does NOT commit the offset, and exits with a NON-ZERO exit code. There is
    no seek-back and no retry-forever hold at max delay.
    Consequences for these procedures:
      - The window in which to observe "fault active" state is ~35 seconds
        from the first failure. Have the verification queries ready before
        publishing.
      - Redelivery requires the PROCESS TO BE RESTARTED. In production that is
        Kubernetes (restartPolicy: Always); locally nothing restarts it for
        you — this script cannot restart the worker either. After removing a
        fault you must start a new worker process yourself (or run the worker
        under a supervisor / `while ($true) { dotnet run ... }` loop) before
        the uncommitted message is redelivered.
      - Check the exit code of the crashed worker: `$LASTEXITCODE` for a
        foreground `dotnet run`, or `(Get-Process -Id $pid).ExitCode` /
        `$proc.ExitCode` if you started it with Start-Process -PassThru.
        Expect non-zero (1).
      - A REJECTED message (SnapshotMessageRejectedException) is the one
        exception: single LogError, offset committed past it, no retries, the
        worker keeps running.

  Preconditions (all TCs):
    - Local Kafka broker up:            ./tools/kafka-local.ps1 -Up
    - Dev Azure SQL schema applied:     ./tools/apply-schema-azure.ps1
    - az login already authenticated (worker/tools use DefaultAzureCredential
      and Active Directory Default against the dev Azure SQL DB and ADLS Gen2
      container configured in src/Clients/UBS.AM.PLT.Snapshot.Worker/appsettings.json
      — no Azurite/local SQL container needed for these TCs).
    - Worker bootstrap servers configurable via Kafka__BootstrapServers env var
      (defaults to localhost:9092, matching kafka-local.ps1).
    - Producer:
        dotnet run --project tools/UBS.AM.PLT.Snapshot.TestProducer -- \
          --snapshots 1 --message-delay 00:00:05
      (4 payload messages per snapshot: header, orders, calculations, settings;
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
    2. Start the worker (dotnet run --project src/Clients/UBS.AM.PLT.Snapshot.Worker),
       then publish one snapshot's messages with the producer above.
    3. Verify while the fault is active:
         - first payload's blob exists in ADLS at the expected path
         - no snapshot_tracking row for the snapshotId
         - worker logs show "Failed to process message ... (attempt N)" for
           N = 1..3 at the configured delays (0s, 5s, 30s)
         - kafka-consumer-groups.sh --describe shows LAG > 0 / committed offset
           unchanged for the partition carrying this snapshotId
         - no "Committed offset" log line for that message
         - after attempt 3: exactly ONE Critical "Operations alert ..." line and
           the worker process exits non-zero
    4. ./fault-injection.ps1 -Remove -Target Tracking
       Then start a NEW worker process (the old one has exited — nothing restarts
       it locally). Verify: the uncommitted message is redelivered from the last
       committed offset, tracking rows appear for all 4 payloads, the index row
       is written on the 4th, lag returns to 0, and "Committed offset" is logged
       per message only after the corresponding SQL write is confirmed.
       (Removing the fault within the ~35s retry window instead lets the running
       worker succeed on a later attempt without any restart — either path is a
       valid pass.)

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
         - 3 "Failed to process message ... (attempt N)" logs for the
           completing message, then one Critical "Operations alert ..." and a
           non-zero process exit
    4. ./fault-injection.ps1 -Remove -Target Index, then start a NEW worker
       process (or remove the fault inside the ~35s retry window and let the
       running worker recover).
       Verify: the completing message redelivers and succeeds — exactly one
       snapshot_index row, tracking status COMPLETE, offset committed, lag back
       to 0.

  --------------------------------------------------------------------------
  TC-15 — restart / new consumer instance
  --------------------------------------------------------------------------
    1. No triggers installed. Start the worker (note its PID), publish one
       snapshot with enough delay to kill/restart mid-stream, e.g.:
         dotnet run --project tools/UBS.AM.PLT.Snapshot.TestProducer -- \
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
  TC-17 / TC-18 — REGRESSIONS, not separately implemented
  --------------------------------------------------------------------------
  TC-17 and TC-18 hit the exact same fault points as TC-13 and TC-14
  respectively — there is no new mechanism to build. Run them as regression
  re-executions of the TC-13/TC-14 procedures above, verbatim, with:

    TC-17 = TC-13's procedure + ONE added assertion: while the Tracking fault
            is active, capture the first payload blob's Last-Modified/ETag
            (e.g. `Get-AzStorageBlob` or the Azure Portal/`az storage blob show`
            against the real ADLS account). After step 4 (-Remove -Target
            Tracking) and the retry succeeding, re-fetch Last-Modified/ETag and
            assert it ADVANCED with identical content — proving redelivery
            overwrote the blob (not skipped it, not duplicated it).

    TC-18 = TC-14's procedure, run exactly as written. Every TC-18 verify
            bullet in the brief is already covered by TC-14's existing verify
            list (all blobs present, tracking RECEIVING not COMPLETE, no index
            row, offset uncommitted, then convergence to exactly one index row
            after -Remove). No new assertions needed.

  --------------------------------------------------------------------------
  TC-16 — blob write fails (design doc §8 Scenario 1)
  --------------------------------------------------------------------------
  Mechanism: env-var config override (NOT a SQL trigger, NOT RBAC revocation —
  RBAC revocation against the shared storage account is explicitly out of
  scope: unsafe/non-deterministic for other concurrent users of the account).
    1. Start the worker with BlobStorage__ServiceUri pointed at an invalid
       endpoint, e.g. (bash):
         BlobStorage__ServiceUri=https://fault-injected-nonexistent.blob.core.windows.net \
           dotnet run --project src/Clients/UBS.AM.PLT.Snapshot.Worker
       ServiceUri wins over ConnectionString in BlobContainerClientFactory, and
       AzureBlobSnapshotStore sets Retry.MaxRetries=0, so the failure surfaces
       on the very first write attempt instead of being absorbed by SDK retry.
    2. Publish one payload (any type) for a fresh snapshotId.
    3. Verify while the fault is active:
         - no blob at the expected path in the real dev ADLS account
         - no snapshot_tracking row for the snapshotId
         - no snapshot_index row for the snapshotId
         - worker logs show "Failed to process message ... (attempt N)" for
           N = 1..3, then one Critical "Operations alert ..."
         - kafka-consumer-groups.sh --describe shows lag > 0, committed offset
           unchanged for the partition carrying this message
         - no "Committed offset" log line for this message
         - the worker process has exited non-zero by itself (~35s after the
           first failure) — no Stop-Process needed
    4. Start a NEW worker process WITHOUT the override (normal config —
       BlobStorage__ServiceUri unset or pointed at the real account); kill the
       old one first with Stop-Process -Force only if it somehow survived.
       Publish the remaining 3 payloads for the same snapshotId if only 1 was
       sent; the uncommitted one redelivers on its own.
       Verify: full self-heal — blob written, tracking row appears, all 4
       payloads eventually COMPLETE, index row written, lag returns to 0. No
       manual SQL/blob intervention.

  --------------------------------------------------------------------------
  TC-19 — crash mid-message (design doc §8 Scenario 4)
  --------------------------------------------------------------------------
  Mechanism: use the existing Tracking/Index THROW triggers to park the
  worker at a well-defined partial/durable state, hard-kill it there
  (Stop-Process -Force, simulating a pod crash — NOT a graceful shutdown), then
  remove the trigger and start a brand-new worker process. Kill inside the ~35s
  retry window (right after the first "attempt 1" failure log) so the kill, and
  not the consumer's own crash-on-exhaustion path, is what ends the process;
  if the worker beats you to it and exits non-zero on its own, the durable
  state is identical and the rest of the procedure is unchanged. Two variants:

    Variant 1 — crash with blob-only durable state (no tracking row):
      1. ./fault-injection.ps1 -Install -Target Tracking
      2. Publish one payload for a fresh snapshotId.
      3. Wait for the "Failed to process message ... attempt N" log (durable
         state at this point: blob exists, no tracking row, offset
         uncommitted — confirm via SQL/ADLS query before killing).
      4. Stop-Process -Force the worker PID (hard kill, not Ctrl+C).
      5. ./fault-injection.ps1 -Remove -Target Tracking
      6. Start a brand-new worker process (new PID). Publish the remaining 3
         payloads if not already sent.
      Verify convergence: exactly one snapshot_tracking row, COMPLETE; exactly
      one snapshot_index row; same adls_root_path as the pre-crash blob (proves
      no orphaned/duplicate root); no duplicate/extra blobs; lag 0.

    Variant 2 — crash with tracking-RECEIVING-all-files durable state (no
    index row) — the torn mid-message state that TC-15 does NOT cover, since
    TC-15 kills cleanly between messages, not mid-completion:
      1. ./fault-injection.ps1 -Install -Target Index
      2. Publish a full snapshot (4 payloads).
      3. Wait for the completing (4th) message's failure log (durable state:
         all 4 blobs exist, tracking row shows all 4 files but status
         RECEIVING, no index row, offset uncommitted — confirm via SQL/ADLS
         before killing).
      4. Stop-Process -Force the worker PID.
      5. ./fault-injection.ps1 -Remove -Target Index
      6. Start a brand-new worker process (new PID) — no need to republish;
         the completing message redelivers from the uncommitted offset.
      Verify the SAME convergence assertions as Variant 1.

    A third variant — "crash before any durable write at all" — is
    deliberately NOT run separately: it is covered by construction, identical
    in effect to TC-16's kill-and-restart-to-success proof (durable state is
    "nothing yet", offset uncommitted, redelivery does a full clean retry).

  --------------------------------------------------------------------------
  TC-20 — index write succeeds, offset commit fails (design doc §8 Scenario 5)
  --------------------------------------------------------------------------
  Two parts. Part 1 is a deterministic, COMMITTED Mode A integration test —
  see tests/UBS.AM.PLT.Snapshot.IntegrationTests/GroupFFailureScenarioTests.cs
  (redelivers the completing header message for an already-COMPLETE snapshot
  directly through the handler and asserts no duplicate rows / unchanged
  created_at / unchanged completed_at / blob overwrite-not-duplicate). Part 2
  is this section — the live proof that a REAL failed Kafka offset commit
  after a successful index write lands in the same retry path:
    1. ./fault-injection.ps1 -Install -Target Delay
    2. Start the worker, publish a full snapshot (4 payloads).
    3. Watch worker logs for the completing (4th) message reaching the index
       write. The delay trigger blocks the INSERT for 20s without failing it.
       While that 20s window is open, run:
         docker pause snapshot-writer-kafka
       (pausing the broker container the worker's Confluent.Kafka client is
       connected to). The index INSERT then returns successfully (after its
       20s delay) and MarkCompleteAsync succeeds, so HandleAsync returns
       successfully — but the subsequent consumer.Commit(result) against the
       paused broker fails/times out, which must land in the same in-process
       retry ladder used for handler failures elsewhere in this script's
       scenarios (and, if all 3 attempts fail, the Critical + non-zero-exit
       crash path).
    4. ./fault-injection.ps1 -Remove -Target Delay
       docker unpause snapshot-writer-kafka
       If the worker already exited non-zero, start a new one. Redelivery
       re-runs the handler against an already-COMPLETE snapshot — an idempotent
       no-op per Part 1's proof (blob overwrite, tracking touch, index UPSERT
       touch) — and commit succeeds on the retry.
    Verify: exactly one snapshot_index row, created_at from the FIRST attempt
    (unchanged by the redelivery); tracking COMPLETE; lag back to 0; no
    unexpected errors beyond the expected retry logs (and at most one Critical
    operations alert) around the commit failure.

    FALLBACK (use only if the broker-pause step proves flaky — e.g.
    consumer.Commit() does not fail cleanly against a paused container, or
    blocks past librdkafka's internal timeout in a way that hangs the run):
    rely on Part 1 alone plus one live re-send — republish the identical
    completing (header) message onto the real topic for an already-COMPLETE
    snapshot (no trigger, no broker pause) and verify no duplicate rows, no
    errors in the worker log, and the offset commits normally. State
    explicitly in the test report that the fallback was taken and why.

  ================================================================================
  LIVE-TEST PROCEDURES (Mode B — Group I infra unavailability, TC-27 / TC-28)
  ================================================================================
  Design doc §9. Both scenarios use the SAME env-var config-override mechanism as
  TC-16 (NOT SQL triggers, NOT RBAC revocation): point the worker at an
  unreachable endpoint via a local config override, observe the §9 ladder of 3
  in-process attempts (0s, 5s, 30s) followed by ONE Critical "Operations alert"
  and a non-zero process exit, prove the offset never commits, then restore
  config, start a new worker and confirm forward recovery. Do NOT take down the
  shared dev Azure SQL server and do NOT revoke RBAC — only local config
  overrides pointing at dead endpoints.

  The deterministic half of these scenarios (exception propagates + no durable
  tracking/index row) is ALSO covered, faster, in the committed Mode A feature
  tests/UBS.AM.PLT.Snapshot.IntegrationTests/Features/InfrastructureFailureDuringWrite.feature.
  Mode A bypasses the consumer entirely, so these live procedures are the ONLY
  coverage of the retry ladder, the Critical alert, the non-zero exit, the real
  broker offset-lag / no-commit proof, and forward recovery after a restart.

  Config keys (standard .NET double-underscore env-var override binding):
    - SQL connection string:  Database__ConnectionString
      (bound to DatabaseOptions.ConnectionString — see
       src/Infrastructure/Sql/DatabaseOptions.cs)
    - Blob service endpoint:  BlobStorage__ServiceUri
      (bound to BlobStorageOptions.ServiceUri — ServiceUri wins over
       ConnectionString in BlobContainerClientFactory; AzureBlobSnapshotStore
       sets Retry.MaxRetries=0 so failures surface on the first attempt)

  --------------------------------------------------------------------------
  TC-27 — Azure SQL unreachable during the tracking write (design §9)
  --------------------------------------------------------------------------
    1. No triggers needed. Start the worker with the SQL connection string
       repointed at an unreachable host + short connect timeout so it fails
       fast (bash):
         Database__ConnectionString='Server=tcp:localhost,9;Database=fault-injected;Connect Timeout=2;Encrypt=False;TrustServerCertificate=True' \
           dotnet run --project src/Clients/UBS.AM.PLT.Snapshot.Worker
    2. Publish one snapshot's messages (producer, as in the Preconditions above).
    3. Verify WHILE the fault is active:
         - no snapshot_tracking row for the snapshotId (query the REAL dev DB
           from a normally-configured connection — the worker's writes never
           reach it); no snapshot_index row either
         - blob MAY or MAY NOT exist (the write order does a SQL read
           (GetRootPathAsync) BEFORE the blob write, so the failure can surface
           before anything is written) — do NOT assert on blob presence
         - worker logs show "Failed to process message ... (attempt N)" for
           N = 1..3 at the §9 delays (0s, 5s, 30s), then EXACTLY ONE Critical
           "Operations alert ..." line — and the process exits non-zero
           (~35s after the first failure), with no further retry logs
         - docker exec ... kafka-consumer-groups.sh --describe shows LAG > 0 /
           committed offset unchanged for the partition; no "Committed offset"
           log line for the message
    4. Start a NEW worker process WITHOUT the override (normal config → the real
       dev Azure SQL); Stop-Process -Force the old one only if it somehow
       survived. Republish the remaining payloads if only some were sent; the
       uncommitted one redelivers on its own.
       Verify forward recovery: snapshot completes — exactly one
       snapshot_tracking row (COMPLETE) and exactly one snapshot_index row (no
       duplicates), lag returns to 0, "Committed offset" logged per message.

  --------------------------------------------------------------------------
  TC-28 — ADLS unreachable during the blob write (design §9)
  --------------------------------------------------------------------------
    Same shape as TC-27, reusing TC-16's BlobStorage__ServiceUri override.
    1. Start the worker with the blob endpoint repointed at a dead endpoint
       (bash):
         BlobStorage__ServiceUri='https://127.0.0.1:1/' \
           dotnet run --project src/Clients/UBS.AM.PLT.Snapshot.Worker
       (port 1 refuses immediately; MaxRetries=0 → first write fails fast. The
        TC-16 form 'https://fault-injected-nonexistent.blob.core.windows.net'
        works equally well.)
    2. Publish one payload (any type) for a fresh snapshotId.
    3. Verify WHILE the fault is active:
         - no blob at the expected path in the real dev ADLS account
         - EXPLICIT SQL check: no snapshot_tracking row for the snapshotId
           (blob-first write order means a blob failure leaves ZERO tracking
           rows even though SQL is perfectly reachable) — and no snapshot_index
           row
         - worker logs show the §9 ladder (3 attempts at 0s/5s/30s), then
           exactly one Critical "Operations alert" and a non-zero process exit
         - kafka-consumer-groups.sh --describe shows lag > 0 / offset
           uncommitted; no "Committed offset" line for the message
    4. Start a NEW worker process WITHOUT the override (real ADLS); the old one
       has already exited. Let redelivery retry / republish remaining payloads.
       Verify forward recovery: blob written, tracking row appears, snapshot
       COMPLETEs, exactly one index row, lag back to 0 — no duplicates, no
       manual SQL/blob intervention.

  --------------------------------------------------------------------------
  CLEANUP (mandatory — shared dev database)
  --------------------------------------------------------------------------
    ./fault-injection.ps1 -Remove -Target Both
    ./fault-injection.ps1 -Remove -Target Delay
    ./fault-injection.ps1 -VerifyClean
    (TC-27 / TC-28 install no triggers — just unset the env-var overrides and
     confirm the worker is back on normal config.)
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Remove,
    [switch]$Status,
    [switch]$VerifyClean,
    [ValidateSet('Tracking', 'Index', 'Delay', 'Both')]
    [string]$Target = 'Both',
    [string]$Server = 'sql-kk-tier1-dev-uksouth.database.windows.net',
    [string]$Database = 'platform-core-db-dev'
)

$ErrorActionPreference = 'Stop'

$triggerNames = @{
    Tracking = 'trg_fault_SnapshotTracking'
    Index    = 'trg_fault_SnapshotIndex'
    Delay    = 'trg_delay_SnapshotIndex'
}

# Every fault-trigger name, independent of -Target — used by -Status/-VerifyClean so
# both always report/verify the full set (Tracking + Index THROW triggers, and the
# Delay trigger), never just whatever -Target happened to default to.
$allTriggerNames = $triggerNames.Values

$triggerDdl = @{
    Tracking = @"
CREATE TRIGGER dbo.trg_fault_SnapshotTracking
ON dbo.SnapshotTracking
AFTER INSERT, UPDATE
AS
BEGIN
    THROW 51000, 'fault-injection: simulated failure on SnapshotTracking write', 1;
END
"@
    Index    = @"
CREATE TRIGGER dbo.trg_fault_SnapshotIndex
ON dbo.SnapshotIndex
AFTER INSERT, UPDATE
AS
BEGIN
    THROW 51001, 'fault-injection: simulated failure on SnapshotIndex write', 1;
END
"@
    Delay    = @"
CREATE TRIGGER dbo.trg_delay_SnapshotIndex
ON dbo.SnapshotIndex
AFTER INSERT
AS
BEGIN
    -- Non-throwing: the INSERT succeeds, just delayed 20s, so the completing
    -- message's index write and MarkCompleteAsync both succeed — the fault is
    -- purely a timing race against the Kafka offset commit that follows (TC-20).
    -- AFTER INSERT only (no UPDATE): the index UPSERT's redelivery-into-an-
    -- already-COMPLETE-snapshot path is a physical UPDATE and must return
    -- immediately, matching Part 1's in-process proof.
    WAITFOR DELAY '00:00:20';
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
    Write-Host 'Usage: ./fault-injection.ps1 -Install|-Remove|-Status|-VerifyClean [-Target Tracking|Index|Delay|Both]'
    Write-Host 'See the comment-based help (Get-Help ./fault-injection.ps1 -Full) for the full TC-13..TC-20 run procedures.'
    exit 1
}

$token = Get-AccessToken

$triggerNameList = "'" + ($allTriggerNames -join "', '") + "'"

$tableForTarget = @{
    Tracking = 'SnapshotTracking'
    Index    = 'SnapshotIndex'
    Delay    = 'SnapshotIndex'
}

if ($VerifyClean) {
    $existing = Invoke-Sql -Token $token -Query "SELECT name FROM sys.triggers WHERE name IN ($triggerNameList)"
    if ($existing) {
        Write-Host 'FAULT TRIGGERS STILL PRESENT:' -ForegroundColor Red
        $existing | ForEach-Object { Write-Host "  $($_.name)" -ForegroundColor Red }
        exit 1
    }
    Write-Host 'Clean: no fault triggers present on the dev database.' -ForegroundColor Green
    exit 0
}

if ($Status) {
    $existing = Invoke-Sql -Token $token -Query "SELECT name, OBJECT_NAME(parent_id) AS table_name FROM sys.triggers WHERE name IN ($triggerNameList)"
    if (-not $existing) {
        Write-Host 'No fault triggers currently installed.'
    } else {
        $existing | ForEach-Object { Write-Host "  $($_.name) on $($_.table_name)" }
    }
    exit 0
}

foreach ($t in Get-Targets) {
    $name = $triggerNames[$t]
    $table = $tableForTarget[$t]

    if ($Install) {
        Write-Host "Installing fault trigger $name on dbo.$table..."
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

$remaining = Invoke-Sql -Token $token -Query "SELECT name FROM sys.triggers WHERE name IN ($triggerNameList)"
if ($remaining) {
    Write-Host 'Fault triggers currently installed:'
    $remaining | ForEach-Object { Write-Host "  $($_.name)" }
} else {
    Write-Host 'No fault triggers currently installed.'
}
