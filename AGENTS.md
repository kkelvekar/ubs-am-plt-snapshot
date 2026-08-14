# Snapshot Writer API — Agent Context

Shared project context for all AI coding agents (GitHub Copilot, Claude Code, and others).
Read this file fully before making any change in this repository.

## Project

The Snapshot Writer API is the write path of the Portfolio Snapshot solution on the UBS
Advantage F2B platform. It runs as a stateless .NET worker (AKS pods in production),
consumes portfolio snapshot payloads from a single Kafka topic, writes each payload as a
JSON file to ADLS Gen2, tracks completeness per snapshot in an Azure SQL tracking table,
and — once all required files for a snapshot are received — builds and writes the permanent
Azure SQL index row that makes the snapshot visible in the audit UI.

The full functional contract is `docs/Portfolio Snapshot - Solution Design.md`.
The design doc wins over any assumption in this file or in code. The architecture is
signed off — do not redesign it.

## Stack

- **.NET 10** / C# latest, nullable reference types enabled, implicit usings on
- **Kafka**: `Confluent.Kafka`, consumer group `snapshot-writer-api`, topic
  `ubs-advantage-snapshots`, `EnableAutoCommit = false` — offsets are committed manually,
  only after all writes for a message succeed
- **Blob storage**: `Azure.Storage.Blobs` (NOT `Azure.Storage.Files.DataLake`) so the same
  code runs against Azurite locally and ADLS Gen2 in production with no change
- **Database**: Azure SQL (local SQL Server for development). EF Core mapped to
  hand-written schema in `db/scripts/` — never add EF migrations; a schema change means
  updating the `.sql` script and the EF mapping together
- **Serialization**: `System.Text.Json`. The wire contract is the approved JSON schema
  (`docs/snapshot-request.schema.json`): seven string properties, **PascalCase** on the
  wire. `JsonSerializerDefaults.Web` (case-insensitive) binds it, and camelCase and unknown
  extra properties keep binding too. Message payloads arrive as opaque JSON **text** in a
  `string` and are written to blob verbatim — never re-serialised from anything parsed, so
  every delivery is byte-identical. No payload is ever deserialised into a DTO — not even
  `header`: at completion time the header is re-read from blob, a single `eventType`
  property is extracted for its filterable SQL column, and the header text itself is
  persisted verbatim to `display_data`, so a new header field needs no code change here

## Architecture

Clean Architecture. Dependencies point inward only:

```text
Domain  <--  Application  <--  Infrastructure  <--  Worker
```

- `Domain` references nothing
- `Application` depends only on `Domain`; ports (interfaces) live here
- `Infrastructure` implements Application ports (Kafka consumer, blob client, EF Core repositories)
- `Worker` is the composition root: hosting, DI wiring, configuration

### Core invariants (violating any of these is a blocker)

1. **Strict write order per message**: blob write → tracking upsert → completeness check →
   index UPSERT. Each layer is touched only after the previous one is confirmed successful.
2. **Idempotency everywhere**: every write at every layer is safe to repeat. Blob overwrite
   is content-idempotent, tracking upsert is idempotent, index write is an UPSERT.
   Kafka redelivery of any message at any point must be harmless.
3. **Offset committed last**: the Kafka offset is committed only after all writes for the
   message succeed. No commit on any failure path. No rollback — recovery is always
   forward (retry via redelivery).
4. **Completeness is driven by a single declarative required-files map**: the required
   file list is one declarative source of truth, never scattered through processing logic.
   The index row is written only when all required files are received.
   That map lives in the library-owned `SnapshotConfigDefinition` code constant
   (`Infrastructure.Sql`), not in appsettings, because the configuration layer cannot carry
   custom appsettings keys. Adding a new payload type is one entry in that constant plus a
   library rebuild — never a change to the write pipeline or consumer processing logic.
5. **Payloads are opaque**: the payload is JSON text, written to blob verbatim, and is
   never deserialised into a DTO. Exactly three sanctioned touches exist, and none of them
   ever re-serialises anything. The first two are a scoped `JsonDocument.Parse` disposed
   immediately:
   - the handler's **syntax-only well-formedness check** before the first write — no field
     is ever inspected, so a broken payload cannot land as an invalid `.json` blob;
   - the **`eventType` extraction** from `header` at completion time — that one value has
     its own filterable column on `snapshot_index`. No other header field may be read; the
     header text is persisted to `display_data` verbatim, byte-identical to the blob.

   The third is not a parse at all:
   - the read edge's **raw-value embedding** when composing the all-payloads detail
     response — `Utf8JsonWriter.WriteRawValue` validates the stored text is syntactically
     well-formed JSON and copies those exact bytes into the response, so no payload is ever
     parsed into a DTO or re-serialised.

## Repository layout

```text
src/        Core/ (Domain, Application) | Infrastructure/ (Sql, Adls, Kafka) | Platform/ | Clients/ (Worker, Api)
tests/      test projects (unit + in-process integration)
tools/      developer utilities (e.g. Kafka test message producer)
db/scripts/ hand-written SQL schema (source of truth for tables/indexes)
docs/       solution design document and diagrams
deploy/     local-only Docker Desktop Kubernetes run (Dockerfiles + minimal Helm chart)
```

## Conventions

- Match the style of surrounding code. Comments only where code cannot express a constraint.
- Structured logging with `snapshotId`, `accountId`, `payloadType` on every processing
  log line. No `Console.WriteLine` in `src/`.
- No secrets in code or committed config. Connection strings and bootstrap servers come
  from configuration with environment-variable overrides.
- **Kafka bootstrap servers are externally configurable** via standard .NET configuration
  binding (`Kafka__BootstrapServers` environment variable overrides appsettings). The
  worker and all test tooling must point at local Docker Kafka or a real broker purely via
  config — no code change between environments. Topics are configured by key under
  `Kafka:Topics` (`snapshot-request`, `snapshot-response`), never hard-coded.
- `dotnet build` and `dotnet test` must be green before any handoff or commit.

### Kafka layer

Both directions of the Kafka layer are the platform's **command pattern**, and the layer holds
nothing else — `Commands/`, `Services/` and `KafkaRegistration.cs`, no consumer loop, no producer
plumbing, no options of its own. Those come from `Ubs.Advantage.Core.Messaging.Kafka`, registered
by topic key:

```csharp
services.AddMessageConsumerService<string, SnapshotRequest, SnapshotRequestCommand>("snapshot-request");
services.AddMessageProducerService<string, SnapshotResponse, SnapshotResponseCommand>("snapshot-response");
```

- `Commands/SnapshotRequestCommand` — an `ACommand<IMessage<string, SnapshotRequest>>`. It maps
  the request onto the domain envelope (a private static method, not a separate mapper class),
  calls straight into an Application-layer use case, and returns a `CommandResult`. No business
  logic, no branching on `payloadType`, no orchestration of its own.
- `Commands/SnapshotResponseCommand` — an
  `ACommand<CommandStatusParameter<IMessage<string, SnapshotResponse>, bool>>`. The producer
  service reports the outcome of each send through it, and it turns that `bool` into a
  `CommandResult`.
- `Services/KafkaSnapshotResponsePublisher` — the `ISnapshotResponsePublisher` implementation.
  It maps the domain notification with `SnapshotResponseMapper` and calls `IProducer.Publish`;
  nothing more.

A consumer command's only lever over the offset is its `CommandResult`: `Success` commits, `Fail`
does not. There is no seek, no requeue and no in-process retry ladder. A `Fail` means the consumer
service logs one Critical alert, leaves the offset uncommitted and exits non-zero, so the process
restarts and Kafka redelivers from the last committed offset. A **rejected** message is the one
case that looks like a failure but returns `Success`: the handler has already written the FAILED
tracking row and published the Failed response, and the same bytes would fail identically forever,
so the offset must move past it.

Publishing is fire-and-forget. `Publish` queues the message and returns, so a response that cannot
be sent is reported to `SnapshotResponseCommand` and logged — it never fails the message being
written, and redelivery republishes it.

`src/Platform/UBS.Advantage.Platform` is a local build of the platform contracts these commands
are written against (`Ubs.Advantage.Core.Infrastructure.Commands`,
`Ubs.Advantage.Core.Messaging.Kafka`, `UBS.Advantage.CommunicationModels.Snapshot`), so the
service compiles and runs against a local broker. Its types, namespaces and signatures match the
platform packages; see that project's README before changing anything in it.

## Scope guards

- The daily cleanup job is **out of scope** for this repository — do not build it. The
  Read API (`src/Clients/UBS.AM.PLT.Snapshot.Api`) **is in scope**, covering both audit
  screens: the Load-snapshots grid (`GET /snapshots/api/portfolio-snapshots`) and the
  Screen-2 snapshot-detail read
  (`GET /snapshots/api/portfolio-snapshots/{snapshotId}/payloads[/{payloadType}]`). For the
  detail read the server resolves `snapshotId` to its `AdlsPath` from `dbo.PortfolioSnapshotIndex`
  and returns the stored payload blobs verbatim.
- **No production deployment artifacts**: no Bicep, ARM templates, CI/CD pipeline files, or
  AKS deployment YAML. Real deployment is owned by the org, outside this repository.
  The one exception is `deploy/`, which exists purely to run the two services on a local
  Docker Desktop Kubernetes cluster: two Dockerfiles and a deliberately minimal Helm chart
  (a Deployment plus Service for the API, a three-replica Deployment for the consumer, and
  one shared ConfigMap/Secret). It targets host-local Kafka, Azurite and SQL Server via
  `host.docker.internal` and must never grow production concerns — no ingress, no TLS, no
  autoscaling, no cloud identity. See `deploy/README.md`. Nothing in `deploy/` is a
  template for how the org deploys this service.
- No speculative abstractions. Build only what the current approved slice needs.

## Repository AI workflow

[workflow.agent.md](.github/agents/workflow.agent.md) is the single authority for automatic request classification,
role orchestration, gates, rerouting, testing-mode selection, and workflow completion. Keep this
file focused on shared project architecture, constraints, and conventions; do not duplicate the
workflow lifecycle here.
