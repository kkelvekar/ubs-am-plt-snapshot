# Snapshot Writer API

Part of the **Portfolio Snapshot solution** on the UBS Advantage F2B platform — a
long-term audit trail for portfolio decision events (Cash Flows, Portfolio
Optimisation, Pre-Trade Compliance, Order Generation) that today are deleted after
30 days with no history. The full solution retains snapshots for up to 10 years by
splitting storage across ADLS Gen2 (the fat payload files, the source of truth) and
Azure SQL (a thin, fast-filterable index), and consists of three cooperating pieces:
a write path, a read path, and a daily cleanup job.

**This repository is the write path.** A stateless .NET worker that consumes
snapshot payloads from Kafka, writes each one to ADLS Gen2, tracks per-snapshot
completeness in Azure SQL, and — once all required files for a snapshot are
received — builds the permanent index row that makes the snapshot visible in the
audit UI. The Read API (serving the two audit-UI screens) and the daily cleanup job
are separate, planned pieces of the same solution and are intentionally not built
in this repository.

The full functional contract for all three pieces lives in
[`docs/Portfolio Snapshot - Solution Design - Final Draft.md`](docs/Portfolio%20Snapshot%20-%20Solution%20Design%20-%20Final%20Draft.md).
The architecture is signed off; this repo implements it, it does not redesign it.

## Architecture at a glance

Clean Architecture, dependencies pointing inward only:

```
Domain  <--  Application  <--  Infrastructure  <--  Worker
```

- **Domain / Application** carry no framework dependencies — Application defines the
  ports (Kafka consumer contract, blob store, SQL repositories) that Infrastructure implements.
- **Infrastructure** is split by concern (`Sql`, `Adls`, `Kafka`) so each adapter can be
  swapped independently — notably the Kafka consumer, which is a deliberately thin,
  disposable adapter designed to be replaced by an org-provided consumer library at
  lift-and-shift time without touching Application or Domain.
- **Worker** is the composition root: hosting, DI wiring, configuration.

Every write is idempotent, the write order per message is fixed (blob → tracking →
completeness check → index), and the Kafka offset is committed only after all writes
for a message succeed — Kafka redelivery at any point must be harmless.
