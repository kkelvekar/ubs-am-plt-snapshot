# Snapshot Writer API — Agent Context

Shared project context for all AI coding agents (GitHub Copilot, Claude Code, and others).
Read this file fully before making any change in this repository.

Codex-specific execution notes live in `CODEX.md`; they adapt the four-role workflow below
to a single Codex session while keeping this file as the source of truth.

## Project

The Snapshot Writer API is the write path of the Portfolio Snapshot solution on the UBS
Advantage F2B platform. It runs as a stateless .NET worker (AKS pods in production),
consumes portfolio snapshot payloads from a single Kafka topic, writes each payload as a
JSON file to ADLS Gen2, tracks completeness per snapshot in an Azure SQL tracking table,
and — once all required files for a snapshot are received — builds and writes the permanent
Azure SQL index row that makes the snapshot visible in the audit UI.

The full functional contract is `docs/Portfolio Snapshot - Solution Design - Final Draft.md`.
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
- **Serialization**: `System.Text.Json`. The wire contract is the org-approved JSON schema
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

```
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
   **Exception (post-lift-and-shift reality):** that map lives in the library-owned
   `SnapshotConfigDefinition` code constant (`Infrastructure.Sql`), not in appsettings,
   because the org configuration layer cannot carry custom appsettings keys. Adding a new
   payload type is one entry in that constant plus a library rebuild (previously an
   appsettings edit) — never a change to the write pipeline or consumer processing logic.
5. **Payloads are opaque**: the payload is JSON text, written to blob verbatim, and is
   never deserialised into a DTO. Exactly two sanctioned touches exist, both a scoped
   `JsonDocument.Parse` disposed immediately, with nothing ever re-serialised from the
   parse:
   - the handler's **syntax-only well-formedness check** before the first write — no field
     is ever inspected, so a broken payload cannot land as an invalid `.json` blob;
   - the **`eventType` extraction** from `header` at completion time — that one value has
     its own filterable column on `snapshot_index`. No other header field may be read; the
     header text is persisted to `display_data` verbatim, byte-identical to the blob.

## Repository layout

```
src/        Core/ (Domain, Application) | Infrastructure/ (Sql, Adls, Kafka) | Clients/ (Worker)
tests/      test projects (unit + in-process integration)
tools/      developer utilities (e.g. Kafka test message producer)
db/scripts/ hand-written SQL schema (source of truth for tables/indexes)
docs/       solution design document and diagrams
```

## Conventions

- Match the style of surrounding code. Comments only where code cannot express a constraint.
- Structured logging with `snapshotId`, `accountId`, `payloadType` on every processing
  log line. No `Console.WriteLine` in `src/`.
- No secrets in code or committed config. Connection strings and bootstrap servers come
  from configuration with environment-variable overrides.
- **Kafka bootstrap servers are externally configurable** via standard .NET configuration
  binding (`Kafka__BootstrapServers` environment variable overrides appsettings). The
  worker and all test tooling must point at local Docker Kafka or the org Kafka server
  purely via config — no code change between environments.
- `dotnet build` and `dotnet test` must be green before any handoff or commit.

### Thin Kafka consumer (lift-and-shift constraint)

The Kafka consumer is a deliberately thin, disposable adapter. It deserialises the
message envelope, calls straight into an Application-layer use case, and commits the
offset on success — nothing else. No business logic, no branching on `payloadType`, no
orchestration of its own. At org lift-and-shift time this consumer is replaced by an
org-provided consumer library, and that swap must touch **only the Infrastructure layer**
— never Application or Domain. The reviewer role checks this on every slice.

## Scope guards

- The daily cleanup job is **out of scope** for this repository — do not build it. The
  Load-snapshots grid Read API (`GET /snapshot/api/portfolio-snapshots`) **is in scope**
  (`src/Clients/UBS.AM.PLT.Snapshot.Api`). The Screen-2 snapshot-detail blob fetch
  remains out of scope — do not build it.
- **No deployment artifacts anywhere in this repo**: no Bicep, ARM templates, Helm charts,
  K8s manifests, CI/CD pipeline files, or AKS deployment YAML. Deployment is handled
  entirely at the org side after lift-and-shift. This repo's scope ends at local
  development and local testing.
- No speculative abstractions. Build only what the current approved slice needs.

## Development workflow — four-role pipeline

All non-trivial changes flow through four roles, in order. Any AI tool (or human) can play
a role; the contract between roles is the same everywhere.

1. **architect-validator** (read-only) — before implementation starts, validates the
   proposed approach for a slice against the design doc and the Clean Architecture
   boundaries above. Does not redesign; the architecture is signed off. Output: an
   approved brief (scope, layer placement, invariants to hold, acceptance criteria).
2. **dev** — implements the approved brief exactly, including unit tests. Raises a
   design-level question back to architect-validator instead of improvising.
3. **reviewer** (read-only) — reviews the diff for Clean Architecture layering violations
   and the core invariants (write order, idempotency, offset-last). Produces findings;
   never fixes code. Verdict: APPROVED or CHANGES_REQUESTED.
4. **tester-e2e** — two distinct testing modes, both required:
   - **Mode A — in-process integration tests** (committed to `tests/`): builds
     `SnapshotMessage` envelopes in code and feeds them directly into the
     message-handling pipeline, bypassing real Kafka; asserts on resulting ADLS Gen2 blob
     writes and Azure SQL `snapshot_tracking` / `snapshot_index` rows against real Azure
     dev resources (`DefaultAzureCredential`), configured via `appsettings.json` in the
     integration test project. Fast, deterministic, CI-friendly.
   - **Mode B — live worker run** (repeatable tooling in `tools/`, not committed tests):
     starts the actual worker process, publishes real messages onto a real Kafka topic via
     the producer utility, verifies actual end-to-end output (blobs in ADLS Gen2/Azurite,
     rows in SQL, per local environment configuration). Exercises the real
     consume-and-commit path.

**Feedback routing**: reviewer/tester findings tagged **code-level** go back to dev with
full context; findings that imply a gap in the agreed design are tagged **design-level**
and go to architect-validator. After any dev fix, the change goes through reviewer again.
Loop budget: 3 iterations, then stop and escalate to a human with the full history.

**Definition of done**: reviewer APPROVED, tester-e2e PASS on both modes, build and tests
green.

## Local environment

No committed docker-compose or Dockerfile — local infrastructure is spun up **ad hoc**
via small setup/teardown scripts under `tools/` (`docker run` on demand). These are dev
convenience scripts, not infra artifacts.

- Kafka: ad-hoc container via `tools/` setup script, started when a dev or the
  tester-e2e role needs a broker, torn down after
- Azurite standing in for ADLS Gen2 (same ad-hoc-on-demand principle) — used for manual/
  Worker-level local runs (Mode B) and unit tests
- SQL Server in an ad-hoc container; schema applied from `db/scripts/` only
- All endpoints (Kafka bootstrap, blob connection string, SQL connection string)
  overridable via environment variables
- Exception: the committed Mode A integration test project (`tests/UBS.AM.PLT.Snapshot.IntegrationTests`)
  is configured to target real Azure dev ADLS Gen2 + Azure SQL resources (`DefaultAzureCredential`) rather
  than this local Azurite/SQL-container stack — see its `appsettings.json` and `SnapshotFixture`
