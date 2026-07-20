<#
.SYNOPSIS
  Dev convenience script — NOT an infrastructure artifact.

  Publishes hand-built raw SnapshotMessage envelopes onto the REAL local Kafka
  topic (via `docker exec ... kafka-console-producer.sh`) so Mode B (live
  worker run, see AGENTS.md) can exercise Group G (TC-21/22 redelivery /
  idempotency) and Group H (TC-23a/24 malformed / unexpected input) against
  the actual consume-and-commit path. TestProducer only emits a fixed
  well-formed 4-payload template, so it cannot express a missing field, a
  null field, or an unconfigured snapshotType — this script fills that gap.

  Every scenario shares the message key convention used everywhere else in
  this repo: Kafka message key = accountId (see
  tools/UBS.AM.PLT.Snapshot.TestProducer/SnapshotSimulationPublisher.cs).

.PARAMETER Scenario
  Which scenario to publish: TC21a, TC21b, TC23a, TC23b, TC24, or All.

.PARAMETER BootstrapServers
  Kafka bootstrap servers used by the producer *client used to reach the
  broker container* (this script always shells into the snapshot-writer-kafka
  container itself, so this parameter only documents what the worker/tools
  should be pointed at — see Kafka__BootstrapServers).

.PARAMETER Topic
  Kafka topic. Defaults to ubs-advantage-snapshots (matches kafka-local.ps1).

.PARAMETER SnapshotIdSuffix
  Appended to the generated snapshotId so repeat runs don't collide. Defaults
  to the current timestamp.

.EXAMPLE
  ./live-groups-gh.ps1 -Scenario All
  ./live-groups-gh.ps1 -Scenario TC21a

.NOTES
  ================================================================================
  LIVE-TEST PROCEDURES (Mode B — Group G / Group H, TC-21..TC-24)
  ================================================================================
  Preconditions:
    - Local Kafka broker up:        ./tools/kafka-local.ps1 -Up
    - Dev Azure SQL schema applied: ./tools/apply-schema-azure.ps1
    - az login already authenticated (worker uses DefaultAzureCredential /
      Active Directory Default against the dev Azure SQL DB and the real ADLS
      Gen2 container configured in src/Clients/UBS.AM.PLT.Snapshot.Worker/appsettings.json
      — no Azurite/local SQL container needed, matching fault-injection.ps1's
      TC-13..TC-20 live tests).
    - Worker running: dotnet run --project src/Clients/UBS.AM.PLT.Snapshot.Worker
    - Consumer-group offset/lag inspection:
        docker exec snapshot-writer-kafka /opt/kafka/bin/kafka-consumer-groups.sh \
          --bootstrap-server localhost:9092 --describe --group snapshot-writer-api

  --------------------------------------------------------------------------
  TC-21 — redelivery of a NON-header payload after the snapshot is COMPLETE
  --------------------------------------------------------------------------
    1. ./live-groups-gh.ps1 -Scenario TC21a
       Publishes a full 4-payload snapshot (header, instruments, calculations,
       settings) for a fresh snapshotId, waits for it to complete, then
       redelivers the exact same instruments.json message.
    2. Verify:
         - exactly one snapshot_tracking row, status COMPLETE, completed_at
           UNCHANGED after the redelivery
         - exactly one snapshot_index row, created_at UNCHANGED
         - instruments.json blob overwritten (Last-Modified advances,
           content byte-identical) not duplicated
         - kafka-consumer-groups.sh --describe shows lag 0 for the partition
           (the duplicate's offset WAS committed — a harmless redelivery must
           not get stuck, this is the one thing Mode A cannot prove: the real
           commit path)
         - worker log shows the message was processed and committed, with NO
           exception/retry logged for it

  --------------------------------------------------------------------------
  TC-23a — envelope missing a required field (raw JSON, no `snapshotId` key)
  --------------------------------------------------------------------------
    1. ./live-groups-gh.ps1 -Scenario TC23a
       Publishes one syntactically-valid-JSON message with the `snapshotId`
       field entirely absent.
    2. Verify:
         - kafka-consumer-groups.sh --describe shows LAG > 0 and the
           committed offset for that partition unchanged (offset NOT
           committed)
         - worker log shows repeated "Failed to process message ... attempt
           N" / retry-cadence lines and, after AlertRepeatEveryFailures
           attempts, a Critical alert log line — the partition is genuinely
           blocked-and-retried, not silently skipped (no "Committed offset"
           line for this message ever appears)
         - the worker process is still alive and NOT stuck/hung on this
           message forever without logging (confirm liveness: the log
           keeps emitting retry lines at the configured RetryDelays cadence)
         - no snapshot_tracking / snapshot_index row for this message (it
           never reached the handler)
       Cleanup: this message blocks the partition it landed on until removed
       from the equation. Recover by publishing a small number of harmless
       messages is NOT possible (this is a poison message at a fixed
       offset) — recovery in production is an ops/alerting action (topic
       compaction, offset skip, or a schema fix upstream); for this local
       run, simply `./kafka-local.ps1 -Down` when done observing to release
       the blocked consumer rather than leaving it retrying indefinitely.

  --------------------------------------------------------------------------
  TC-23b — envelope with an explicit null required field (accountId: null)
  --------------------------------------------------------------------------
    1. ./live-groups-gh.ps1 -Scenario TC23b
       Publishes one message that deserialises successfully (System.Text.Json
       `required` is satisfied — the key is present) but `accountId` is
       JSON null, so SnapshotMessageHandler.ValidateIdentity rejects it
       before any write.
    2. Verify: same checklist as TC-23a (no commit, lag > 0, retry-cadence
       logs, no tracking/index row) — this proves the Application-layer
       null-identity guard (not just deserialisation) also lands in the
       consumer's no-commit/seek-back/retry path, live.
       NOTE: this message has AccountId = null, so Kafka partitions it by a
       null key (round-robin / random partition, NOT deterministic) —
       identify the affected partition from the consumer-groups --describe
       output (whichever partition's LAG is > 0), not by pre-computing it.

  --------------------------------------------------------------------------
  TC-24 — unconfigured snapshotType (no SnapshotConfig entry)
  --------------------------------------------------------------------------
    1. ./live-groups-gh.ps1 -Scenario TC24
       Publishes one instruments.json payload with snapshotType "mystery"
       (absent from Worker/appsettings.json SnapshotConfig).
    2. Verify:
         - blob IS written to the real ADLS container at
           mystery_snapshots/... (blob write precedes the completeness
           check that fails)
         - snapshot_tracking row IS written, status RECEIVING, never
           COMPLETE
         - no snapshot_index row ever appears for this snapshotId
         - offset for this message NOT committed; kafka-consumer-groups.sh
           --describe shows lag > 0, worker log shows repeated
           KeyNotFoundException-driven retry lines — the partition is
           blocked-with-retry (matches Mode A's GroupHMalformedInputTests
           Unknown_snapshotType_writes_blob_and_tracking_but_never_completes),
           NOT silently skipped
    3. This is the expected/designed blocked state for an unconfigured type
       (design doc: completeness/required-file list comes from config; there
       is no separate pre-write config validation step) — do not "fix" it by
       editing the live worker's SnapshotConfig mid-run; report the blocked
       state as expected and move on. `./kafka-local.ps1 -Down` releases it.

  --------------------------------------------------------------------------
  CLEANUP (mandatory)
  --------------------------------------------------------------------------
    Stop the worker process.
    ./tools/kafka-local.ps1 -Down
    Delete the SQL rows / blobs created by these scenarios under the
    snapshotIds this script prints (they use the AccountId below, disjoint
    from IntegrationTestSettings:TestAccountIds so Mode A cleanup never
    touches them) — or leave them; they do not collide with any other test.
#>
[CmdletBinding()]
param(
    [ValidateSet('TC21a', 'TC23a', 'TC23b', 'TC24', 'All')]
    [string]$Scenario = 'All',
    [string]$BootstrapServers = 'localhost:9092',
    [string]$Topic = 'ubs-advantage-snapshots',
    [string]$SnapshotIdSuffix = (Get-Date -Format 'yyyyMMddHHmmss'),
    [string]$ContainerName = 'snapshot-writer-kafka'
)

$ErrorActionPreference = 'Stop'

# Distinct account id (=> distinct Kafka partition, deterministic hash-partitioned by
# key) per scenario, so a scenario that deliberately jams its partition forever
# (TC-23a/b, TC-24) can never block a different scenario's messages sitting behind it
# on the same partition.
$AccountIdTc21 = 'LIVE-GH-ACC-21'
$AccountIdTc23a = 'LIVE-GH-ACC-23A'
$AccountIdTc24 = 'LIVE-GH-ACC-24'

function Send-RawMessage {
    param([string]$Key, [string]$Json)

    # docker exec via Git-Bash-safe invocation: avoid MSYS path mangling of the
    # in-container script path.
    $env:MSYS_NO_PATHCONV = '1'
    $line = "$Key|$Json"
    Write-Host "  -> key=$Key value=$Json"
    $line | docker exec -i $ContainerName /opt/kafka/bin/kafka-console-producer.sh `
        --bootstrap-server $BootstrapServers --topic $Topic `
        --property "parse.key=true" --property "key.separator=|"
}

function New-Envelope {
    param(
        [string]$SnapshotId,
        [string]$AccountIdValue,
        [string]$SnapshotType = 'portfolio',
        [string]$PayloadType,
        [string]$PayloadJson
    )
    $publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    return @"
{"snapshotId":"$SnapshotId","accountId":"$AccountIdValue","snapshotType":"$SnapshotType","payloadType":"$PayloadType","stage":"PreTrade","publishedAt":"$publishedAt","publishedBy":"LiveGroupGH","schemaVersion":"1.0","payload":$PayloadJson}
"@
}

function Invoke-TC21a {
    $snapshotId = "livegh-tc21a-$SnapshotIdSuffix"
    $AccountId = $AccountIdTc21
    Write-Host "TC-21: snapshotId=$snapshotId accountId=$AccountId"
    $header = '{"eventType":"ModelChange","portfolioStatus":"ReadyToSend","orderStatus":"ReadyToSend","benchmark":"MCCHM2EQ","baseCcy":"CHF","orderApprovedBy":"Anna Miller","orderApprovedAt":"2026-05-15T06:10:14Z","orderSentBy":"James Smith","orderSentAt":"2026-05-15T06:14:22Z","programId":"123456","batchId":"15884","numOrders":4,"ptcAlerts":0}'
    $instruments = '{"positions":[{"isin":"CH0038863350","qty":250}]}'
    $calculations = '{"nav":5555.55,"ccy":"CHF"}'
    $settings = '{"tolerance":0.05}'

    Send-RawMessage -Key $AccountId -Json (New-Envelope -SnapshotId $snapshotId -AccountIdValue $AccountId -PayloadType 'instruments' -PayloadJson $instruments)
    Send-RawMessage -Key $AccountId -Json (New-Envelope -SnapshotId $snapshotId -AccountIdValue $AccountId -PayloadType 'calculations' -PayloadJson $calculations)
    Send-RawMessage -Key $AccountId -Json (New-Envelope -SnapshotId $snapshotId -AccountIdValue $AccountId -PayloadType 'settings' -PayloadJson $settings)
    Send-RawMessage -Key $AccountId -Json (New-Envelope -SnapshotId $snapshotId -AccountIdValue $AccountId -PayloadType 'header' -PayloadJson $header)

    Write-Host 'Waiting 15s for the snapshot to complete before redelivering instruments.json...'
    Start-Sleep -Seconds 15

    Write-Host 'Redelivering the SAME instruments.json message (duplicate, post-completion):'
    Send-RawMessage -Key $AccountId -Json (New-Envelope -SnapshotId $snapshotId -AccountIdValue $AccountId -PayloadType 'instruments' -PayloadJson $instruments)
    Write-Host "TC-21 snapshotId for verification: $snapshotId"
}

function Invoke-TC23a {
    $snapshotId = "livegh-tc23a-$SnapshotIdSuffix"
    $AccountId = $AccountIdTc23a
    Write-Host "TC-23a: envelope missing snapshotId entirely (poison message)"
    $publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $json = "{`"accountId`":`"$AccountId`",`"snapshotType`":`"portfolio`",`"payloadType`":`"instruments`",`"stage`":`"PreTrade`",`"publishedAt`":`"$publishedAt`",`"publishedBy`":`"LiveGroupGH`",`"schemaVersion`":`"1.0`",`"payload`":{`"positions`":[]}}"
    Send-RawMessage -Key $AccountId -Json $json
    Write-Host 'TC-23a published — this is a poison message; check kafka-consumer-groups.sh --describe for LAG > 0 and worker log for repeated retry/alert lines.'
}

function Invoke-TC23b {
    $snapshotId = "livegh-tc23b-$SnapshotIdSuffix"
    Write-Host "TC-23b: explicit null accountId (present-but-null identity field)"
    $publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $json = "{`"snapshotId`":`"$snapshotId`",`"accountId`":null,`"snapshotType`":`"portfolio`",`"payloadType`":`"instruments`",`"stage`":`"PreTrade`",`"publishedAt`":`"$publishedAt`",`"publishedBy`":`"LiveGroupGH`",`"schemaVersion`":`"1.0`",`"payload`":{`"positions`":[]}}"
    # Key intentionally left as the null-marker string so this message can still be
    # located; a null AccountId means the PRODUCER key below is a literal empty key
    # (round-robin partition) rather than the SnapshotMessage.AccountId (which is null).
    Send-RawMessage -Key '' -Json $json
    Write-Host "TC-23b published, snapshotId=$snapshotId — identify the affected partition from kafka-consumer-groups.sh --describe (whichever partition shows LAG > 0), since the null-keyed message lands on a non-deterministic partition."
}

function Invoke-TC24 {
    $snapshotId = "livegh-tc24-$SnapshotIdSuffix"
    $AccountId = $AccountIdTc24
    Write-Host "TC-24: unconfigured snapshotType 'mystery'"
    $instruments = '{"positions":[{"isin":"CH0038863350","qty":250}]}'
    Send-RawMessage -Key $AccountId -Json (New-Envelope -SnapshotId $snapshotId -AccountIdValue $AccountId -SnapshotType 'mystery' -PayloadType 'instruments' -PayloadJson $instruments)
    Write-Host "TC-24 snapshotId for verification: $snapshotId (expect blob + tracking RECEIVING, no index row, offset never committed)"
}

switch ($Scenario) {
    'TC21a' { Invoke-TC21a }
    'TC23a' { Invoke-TC23a }
    'TC23b' { Invoke-TC23b }
    'TC24'  { Invoke-TC24 }
    'All'   {
        Invoke-TC21a
        Invoke-TC24
        Write-Host ''
        Write-Host 'NOTE: TC-23a and TC-23b are poison messages that permanently block the' -ForegroundColor Yellow
        Write-Host 'partition they land on. Run them LAST and separately (./live-groups-gh.ps1' -ForegroundColor Yellow
        Write-Host '-Scenario TC23a / -Scenario TC23b), one at a time, after you are done observing' -ForegroundColor Yellow
        Write-Host 'TC-21a/TC-24, since nothing else on that partition will process until the' -ForegroundColor Yellow
        Write-Host 'worker/topic is torn down.' -ForegroundColor Yellow
    }
}
