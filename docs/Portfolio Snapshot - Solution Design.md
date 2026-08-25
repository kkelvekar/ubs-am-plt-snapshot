## 1. Problem Statement

UBS Advantage captures portfolio decision events at each stage of the investment pipeline — Cash Flows, Portfolio Optimisation, Pre-Trade Compliance, and Order Generation. Currently these point-in-time snapshots are stored in Azure SQL and deleted after 30 days due to cost and volume concerns. There is no long-term audit capability.

The business requires the ability to view what decisions led to any given order at any point in time, retaining that audit history for up to 10 years.

**Current pain points:**

- Snapshots deleted after 30 days with no long-term audit trail
- Azure SQL stores fat payloads making it expensive at scale
- No structured way to view point-in-time portfolio state across pipeline stages

**Business goals:**

- Retain snapshots for 10 years minimum
- Snapshots visible in the UI instantly after capture
- Cost-effective compared to current Azure SQL storage approach
- Declarative onboarding of new snapshot types via a single required-files map, with no change to the write pipeline or consumer processing logic (**post-lift-and-shift:** that map is the library-owned `SnapshotConfigDefinition` code constant, not an appsettings section — adding a type is a one-entry lib edit)
- No new infrastructure services -- use existing Azure SQL and ADLS Gen2 only

### Workflow orchestration smoke-test feature

The following deliberately small mock feature exists to verify the repository's
planner -> developer -> reviewer -> tester handoff workflow. It is a proposed
development feature, not a production business requirement. The planner must assess
its scope and return `READY_FOR_IMPLEMENTATION` before it is implemented; a compatible
feature is not rejected merely because it was absent from this document.

Add a local-only `workflow-check` snapshot type with one required payload file,
`workflow-check.json`. The payload remains opaque JSON text and must follow the
same blob write, tracking, completeness, idempotency, and offset-commit rules as
every other snapshot. The feature is complete when a focused test proves that a
valid `workflow-check` message writes its blob and tracking row, a duplicate
delivery is harmless, and malformed JSON is rejected before any write. No new
endpoint, infrastructure, configuration section, or production deployment
artifact is allowed for this smoke test. The required-files entry must be made
through the existing declarative `SnapshotConfigDefinition` map.

---

## 2. Solution Overview

### Development-only live testing

When the Worker runs in the `Development` environment it exposes
`POST /api/live-tests/snapshots` (default address `http://localhost:5106`). The endpoint
loads one of the bundled JSON templates, generates the same ordered portfolio payload
sequence used for live testing, and awaits acknowledged delivery to the configured Kafka
request topic. Kafka broker and topic values always come from Worker configuration and
cannot be supplied by the caller. The controller and its services are not registered or
mapped outside Development; this is a development verification surface, not part of the
production business API.

![[Snapshot Solution Final.png]]

The core insight driving this design is that the two screens in the Audit app have fundamentally different access patterns and must be served by different stores.

The **Load snapshots grid** needs fast filtered queries on thin metadata. This is served by an **Azure SQL index table** on an existing server.

The **View a snapshot detail** needs one large document fetched by key with no cross-row querying. This is served by **ADLS Gen2 blob files** fetched directly using the path stored in the index row.

Upstream services publish snapshot payloads to a single Kafka topic with a shared **snapshotId** used as the correlationId. Each Kafka message carries a payloadType identifying the file -- header, orders, portfolio, settings. The Snapshot Writer API runs as stateless AKS pods. It consumes each message independently, writes each payload as a separate JSON file to ADLS Gen2, tracks completeness using a **dedicated Azure SQL tracking table**, and when all required files are confirmed received it reads header.json from ADLS, builds the permanent SQL index row, and writes it. This is the moment the snapshot becomes visible in the grid.

**Key principles:**

- Blob files are the true source of truth -- the SQL index row is a derived pointer
- The grid shows a snapshot only when the index row exists -- no incomplete snapshots visible
- The Writer API is fully stateless -- all durable state lives in Azure SQL and ADLS
- Tracking uses a dedicated 30-day rolling SQL table, kept separate from the permanent audit table
- No new infrastructure service is introduced -- only existing Azure SQL and ADLS Gen2 are used

---

## 3. Architecture

**Write path**

Upstream services -- Portal, Portfolio Calculation, OFM -- publish snapshot payloads independently to a single Kafka topic named `ubs-advantage-snapshots`. The topic is partitioned by accountId so all messages for one account are routed to the same partition and processed sequentially, avoiding update conflicts on the tracking row.

The Snapshot Writer API consumes each message and performs four steps in strict order. First it writes the blob file to ADLS Gen2. Second it inserts or updates the row in the snapshot_tracking table for the snapshotId, storing the ADLS root path on first write. Third it checks completeness by comparing received files against the required file list in appsettings.json. If all files are received it reads header.json from ADLS using the stored root path, builds the permanent SQL index row, writes it via UPSERT, and updates the tracking row to status COMPLETE. The Kafka offset is committed only after all steps succeed.

**Read path**

The .NET Read API serves both screens. For the Load snapshots grid it queries the permanent Azure SQL index table. For the View a snapshot detail it reads the adls_path from the clicked row and fetches blob files from ADLS Gen2 tab by tab. No cluster is involved in either read.

**Cleanup path**

A daily job has two responsibilities against the snapshot_tracking table. First, it scans for rows in RECEIVING status whose last_updated_at is older than the agreed stale threshold, marks them FAILED, deletes their orphan blobs from ADLS, records the missing files, and raises an alert. Second, it purges all rows -- regardless of status -- older than 30 days, keeping the table bounded permanently.

---

## 4. Kafka Topic and Message Contract

### Topic Design

```
Topic name:      ubs-advantage-snapshots
Partitions:      by accountId
Retention:       7 days
Consumer group:  snapshot-writer-api
Offset commit:   manual (EnableAutoCommit = false)
```

### Message Contract

Every message uses the same envelope regardless of which service publishes it or which payload type it carries. The envelope is defined by the org-approved JSON Schema (draft-04, `additionalProperties: true`) committed at `docs/snapshot-request.schema.json`: exactly seven string properties, **PascalCase on the wire**. The payload field is opaque -- it carries already-serialised JSON as a **string**, and the consumer writes that string to ADLS verbatim, without parsing its internal structure. The only touch of the payload before the write is a syntax-only well-formedness check (parse-and-discard), so a broken payload can never land as an invalid `.json` blob. At completion, the header text is re-read from blob in a scoped `JsonDocument` parse solely to extract `$.Payload.Event` for the SQL index; it is never deserialised into a DTO or re-serialised. A missing, invalid, blank, or over-length value at that path rejects the existing snapshot as FAILED with `INVALID_HEADER_EVENT`; malformed header JSON remains retryable.

**C# contract (.NET 10):**

```csharp
public class SnapshotMessage
{
    // Envelope -- fixed, always present
    public string SnapshotId   { get; set; }  // correlationId
    public string AccountId    { get; set; }
    public string SnapshotType { get; set; }  // "portfolio"
    public string PayloadType  { get; set; }  // "header" / "orders" etc
    public string PublishedAt  { get; set; }  // log-only, never used in logic
    public string PublishedBy  { get; set; }  // "PortfolioCalculation"

    // Payload -- opaque JSON text, variable per payloadType
    // Written to ADLS verbatim; never re-serialised from anything parsed,
    // so every delivery produces byte-identical blob content
    public string Payload      { get; set; }
}

```

**Wire format example -- orders payload:**

```json
{
  "SnapshotId":   "corr98765",
  "AccountId":    "00675442A",
  "SnapshotType": "portfolio",
  "PayloadType":  "orders",
  "PublishedAt":  "2026-05-22T06:10:14Z",
  "PublishedBy":  "PortfolioCalculation",
  "Payload":      "{\"total\":21,\"equities\":[{\"assetName\":\"APPLE LTD\",\"sedol\":\"BPBAJ01\",\"ccy\":\"CHF\",\"region\":\"EMEA\",\"targetPct\":1.52,\"prevTargetPct\":1.52}],\"futures\":[],\"cash\":[]}"
}
```

The `Payload` value above is a JSON **string**, not a nested object. Unescaped, it is the exact byte sequence written to `orders.json`:

```json
{"total":21,"equities":[{"assetName":"APPLE LTD","sedol":"BPBAJ01","ccy":"CHF","region":"EMEA","targetPct":1.52,"prevTargetPct":1.52}],"futures":[],"cash":[]}
```

**Wire format example -- header payload:**

```json
{
  "SnapshotId":   "corr98765",
  "AccountId":    "00675442A",
  "SnapshotType": "portfolio",
  "PayloadType":  "header",
  "PublishedAt":  "2026-05-22T06:14:22Z",
  "PublishedBy":  "Portal",
  "Payload":      "{\"SnapshotId\":\"corr98765\",\"Type\":\"Header\",\"Payload\":{\"Event\":\"ModelChange\",\"portfolioStatus\":\"ReadyToSend\",\"orderStatus\":\"ReadyToSend\",\"benchmark\":\"MCCHM2EQ\",\"baseCcy\":\"CHF\",\"orderApprovedBy\":\"Anna Miller\",\"orderApprovedAt\":\"2026-05-15T06:10:14Z\",\"orderSentBy\":\"James Smith\",\"numOrders\":4,\"ptcAlerts\":0,\"programId\":\"123456\",\"batchId\":\"15884\"}}"
}
```

Unescaped, that `Payload` string is the exact content written to `header.json`. At completion, the writer makes a scoped parse to read only `Payload.Event`; it never deserialises the header into a DTO or re-serialises it:

```json
{"SnapshotId":"corr98765","Type":"Header","Payload":{"Event":"ModelChange","portfolioStatus":"ReadyToSend","orderStatus":"ReadyToSend","benchmark":"MCCHM2EQ","baseCcy":"CHF","orderApprovedBy":"Anna Miller","orderApprovedAt":"2026-05-15T06:10:14Z","orderSentBy":"James Smith","numOrders":4,"ptcAlerts":0,"programId":"123456","batchId":"15884"}}
```

**Required file list** (illustrative shape below).

> **Post-lift-and-shift note:** the required-files map is a library-owned code constant
> (`SnapshotConfigDefinition` in `Infrastructure.Sql`), **not** an appsettings section,
> because the org configuration layer cannot carry custom appsettings keys. The JSON below
> shows the logical shape only; the live map is the code constant.

```json
{
  "portfolio": {
    "requiredFiles": [
      "header.json",
      "orders.json",
      "portfolio.json",
      "settings.json"
    ]
  }
}
```

Adding a new payload type in future means adding one entry to the `SnapshotConfigDefinition` map (a one-entry lib edit plus rebuild). No consumer code change required.

---

## 5. Storage Design -- ADLS Gen2

All snapshot payload files are stored in an **existing ADLS Gen2 storage account**. A new container named `ubsadvsnapshots` is created within that account specifically for this solution.

**Folder structure:**

```
ubsadvsnapshots/
└── portfolio_snapshots/
    └── year=2026/
        └── month=05/
            ├── accountId=00675442A/
            │   ├── snapshotId=corr98765/
            │   │   ├── header.json
            │   │   ├── orders.json
            │   │   ├── portfolio.json
            │   │   └── settings.json
            │   └── snapshotId=corr98766/
            │       ├── header.json
            │       ├── orders.json
            │       ├── portfolio.json
            │       └── settings.json
            └── accountId=03485732S/
                └── snapshotId=corr98770/
                    ├── header.json
                    ├── orders.json
                    ├── portfolio.json
                    └── settings.json
```

ADLS Gen2 is used over simple Blob Storage because it provides a true hierarchical namespace with real folder semantics and ACL-level permissions per folder. JSON is the file format because producers already emit JSON. Files are stored uncompressed in ADLS; gzip compression is applied only at the HTTP response layer by the Read API.

New payload types require no code or infrastructure change. ADLS Gen2 folders are removed automatically when all files inside are deleted -- no explicit folder delete is needed during cleanup.

**Blob tier strategy:**

Azure Storage lifecycle management transitions blobs automatically -- Hot tier for recent data, Cool tier after 30 days, Cold tier after 90 days. Cold tier supports fast retrieval unlike Archive which has multi-hour rehydration delay.

---

## 6. Snapshot Tracking Design -- Azure SQL

### Why Azure SQL Instead of Redis

Redis was considered earlier in this design process as the tracking layer for in-flight snapshot completeness. It is replaced with a dedicated Azure SQL table for two reasons confirmed by the architect. First, Redis persistence (AOF or RDB) is not currently planned to be enabled in this environment -- if Redis restarts or fails, all in-flight tracking state would be lost with no recovery path. Second, Redis would introduce a net-new infrastructure service to provision, operate, and pay for, when Azure SQL is already available and operated by the team.

The tracking table is explicitly designed as **transient operational data with a 30-day rolling retention**, not permanent audit data. This bounded retention is what keeps the table small regardless of write volume, addressing the earlier performance concern about a SQL tracking table growing unbounded over years. At 1,000 snapshots per day across a 30-day rolling window, the table holds roughly 30,000 to 120,000 rows at any time depending on the mix of RECEIVING, COMPLETE, and FAILED rows -- a trivial size for SQL Server.

The tracking table lives in the **same database** as the permanent snapshot_index table.

### snapshot_tracking Table (Representative Schema)

|Column|Type|Notes|
|---|---|---|
|snapshot_id|VARCHAR(50)|Primary key|
|account_id|VARCHAR(20)|For filtering and alerting|
|snapshot_type|VARCHAR(50)|e.g. "portfolio"|
|adls_root_path|VARCHAR(500)|Root folder path, set on first payload write|
|received_files|NVARCHAR(1000)|JSON array of received filenames|
|missing_files|NVARCHAR(1000)|JSON array, populated only when status = FAILED|
|status|VARCHAR(20)|RECEIVING / COMPLETE / FAILED|
|first_received_at|DATETIME2|When the first payload arrived|
|last_updated_at|DATETIME2|When the most recent payload arrived|
|completed_at|DATETIME2 NULL|When status became COMPLETE|
|declared_failed_at|DATETIME2 NULL|When the cleanup job marked it FAILED|
|alerted|BIT|Whether an alert was raised for this row|

This single table serves as a rolling 30-day log of all snapshot processing activity -- every snapshot that is currently receiving files, every snapshot that has completed, and every snapshot that has been declared failed all remain visible here until the 30-day purge removes them. This gives operations a full traceability window without needing to query the permanent audit table.

### Indexing

```sql
-- Cleanup job scan for stale RECEIVING rows
CREATE INDEX ix_tracking_status_updated
    ON snapshot_tracking (status, last_updated_at)

-- 30-day purge scan
CREATE INDEX ix_tracking_last_updated
    ON snapshot_tracking (last_updated_at)
```

These two indexes keep both cleanup responsibilities fast even as the table approaches its bounded ceiling.

### Write Flow Per Message

```
ANY payload arrives on Kafka:

  Step 1: Write blob to ADLS Gen2
          Always first, unconditionally
          Returns confirmed write path

  Step 2: Row exists in snapshot_tracking
          for this snapshotId?

    NO -- first payload for this snapshotId:
      INSERT INTO snapshot_tracking
        snapshot_id        = corr98765
        account_id         = from envelope
        snapshot_type       = from envelope
        adls_root_path     = folder path from Step 1
        received_files     = [this filename]
        status              = RECEIVING
        first_received_at  = now
        last_updated_at     = now

    YES -- subsequent payload:
      UPDATE snapshot_tracking
      SET received_files  = [append this filename]
          last_updated_at  = now
      WHERE snapshot_id = corr98765

  Step 3: Check completeness
    received_files == requiredFiles
    from appsettings.json?

    NOT complete:
      Commit Kafka offset
      Done

    COMPLETE:
      Read adls_root_path from the tracking row
      Read adls_root_path + "/header.json" from ADLS
      Deserialise to HeaderPayload
      Build snapshot_index row + display_data JSON
      Write snapshot_index UPSERT (permanent audit table)

      UPDATE snapshot_tracking
      SET status        = COMPLETE
          completed_at  = now
      WHERE snapshot_id = corr98765
      -- row remains as a log entry until 30-day purge

      Commit Kafka offset
      Snapshot now visible in grid
```

### Daily Job -- Stale Detection and 30-Day Purge

```
Responsibility 1 -- Stale detection

  SELECT * FROM snapshot_tracking
  WHERE status = 'RECEIVING'
  AND last_updated_at < DATEADD(hour, -X, GETUTCDATE())
  -- X is the agreed stale threshold

  For each stale row:
    Read adls_root_path and received_files
    Calculate missing_files
      = requiredFiles (appsettings) minus received_files
    Delete orphan blob files from ADLS
      for each file in received_files:
        DELETE adls_root_path + "/" + filename
      (delete blobs before updating the row, so a
       crash mid-job leaves adls_root_path intact
       for the next run to retry)

    UPDATE snapshot_tracking
    SET status              = FAILED
        declared_failed_at  = now
        missing_files       = calculated list
        alerted              = 1
    WHERE snapshot_id = [this row]

    Raise alert to operations team
    listing snapshotId, accountId, missing_files

Responsibility 2 -- 30-day purge

  DELETE FROM snapshot_tracking
  WHERE last_updated_at < DATEADD(day, -30, GETUTCDATE())
  -- removes COMPLETE, FAILED, and any
  -- impossibly old RECEIVING rows
  -- this keeps the table bounded permanently
```

---

## 7. Index Table Design -- Azure SQL

The Azure SQL index table holds exactly one thin row per snapshot containing the filterable grid columns, a single JSON display column for all non-filterable display fields, and the ADLS folder path. Fat payload data stays entirely in blob.

This table is created in a **new database on an existing Azure SQL server**, shared with the snapshot_tracking table described above, for clean separation from existing application databases.

**Note on schema:** The schema below is representative. The exact fields in display_data will be confirmed based on the header payload structure, agreed with upstream teams before implementation. `display_data` holds the header's nested `Payload` object text verbatim (the `SnapshotId`/`Type`/`Payload` envelope wrapper is not stored), including `Event`, which is also copied into its own filterable `event_type` column.

**Architect recommendation adopted -- JSON display column:**

Columns displayed in the grid but not used as filter criteria are stored as a single JSON column. This keeps indexed SQL columns to the minimum needed for filtering and makes the schema flexible as new display fields arrive from the header payload in future without requiring a schema migration.

```
Filterable SQL columns (indexed):
  snapshot_id, account_id, snapshot_date,
  event_type, adls_path

JSON display column:
  display_data -- all non-filterable grid fields:
  {
    "benchmark":       "MCCHM2EQ",
    "baseCcy":         "CHF",
    "programId":       "123456",
    "batchId":         "15884",
    "numOrders":       4,
    "ptcAlerts":       0,
    "orderApprovedBy": "Anna Miller",
    "orderApprovedAt": "2026-05-15T06:10:14Z",
    "orderSentBy":     "James Smith",
    "orderSentAt":     "2026-05-15T06:10:14Z"
  }
```

**snapshot_index table (representative schema):**

|Column|Type|Notes|
|---|---|---|
|snapshot_id|VARCHAR(50)|Primary key -- correlationId from Kafka|
|account_id|VARCHAR(20)|Indexed -- always in grid query|
|snapshot_date|DATETIME2|Partition column -- year-based|
|event_type|VARCHAR(50)|ModelChange / Cashflow / NoEvent|
|adls_path|VARCHAR(500)|Root folder path to snapshot files|
|display_data|NVARCHAR(MAX)|JSON -- all non-filterable grid fields|
|created_at|DATETIME2|Row write timestamp|

**Indexing and partitioning:**

|Object|Columns|Purpose|
|---|---|---|
|Table partition|snapshot_date by year|Partition elimination -- 7-day default touches current year only|
|Non-clustered index 1|account_id, snapshot_date DESC|Dominant query -- account plus date range|
|Non-clustered index 2|event_type, snapshot_date DESC|Event type filter queries|

**Grid query pattern:**

```sql
SELECT   snapshot_id,
         account_id,
         snapshot_date,
         event_type,
         adls_path,
         display_data
FROM     snapshot_index
WHERE    account_id   IN ('00675442A', '03485732S', ...)
AND      snapshot_date >= '2026-05-21'
AND      snapshot_date <= '2026-05-27 23:59:59'
AND      event_type    = 'ModelChange'    -- optional
ORDER BY snapshot_date DESC
```

**Performance over 10 years:**

At 1,000 snapshots per day over 10 years the snapshot_index table holds approximately 3.65 million rows. With year-based partitioning and the account_id index, a 7-day default query touches one year partition and performs an index seek. Performance is identical in year 10 as in year 1. This permanent table is unaffected by the design change above -- only the tracking table changed from Redis to a bounded 30-day SQL table.

---

## 8. Consistency Model -- Atomicity Across Azure SQL and ADLS Gen2

True distributed atomicity spanning Azure SQL and ADLS Gen2 is not implemented and is not required for this use case. Azure SQL and ADLS Gen2 have no shared transaction coordinator. The system instead achieves **sequential consistency with safe recovery** through three principles.

**Write ordering:** Blob is always written before the tracking row is updated, and the tracking row is always updated before the permanent index row is touched. Each layer is updated only after the previous one is confirmed successful.

**Idempotency:** All writes at every layer are safe to repeat. ADLS blob overwrites are content-idempotent. The tracking table upsert is idempotent. The snapshot_index write uses UPSERT so re-delivery of a header message after partial failure produces no duplicate rows.

**Kafka offset commitment:** The offset is committed only as the final step after all writes succeed. A failure classified transient (§9) causes the worker to exit non-zero and restart, and the restarted consumer resumes from the last committed offset so the message is re-delivered and all steps retried from the beginning. A failure classified poison (§9) is recorded as FAILED and the offset is committed past it deliberately, since the same bytes would fail identically forever.

The guarantee to users: a snapshot is either fully visible in the grid with all blob files present, or it is not visible at all. There is no intermediate state.

### Failure Scenario Analysis

**Scenario 1 -- Blob write fails:**

```
Consumer attempts blob write to ADLS
Write fails

ADLS Gen2 behaviour:
  Partial uploads automatically discarded
  No partial file ever committed

State:    ADLS no file, tracking row not touched,
          index row not touched
Offset:   NOT committed
Recovery: Kafka re-delivers, full retry
Result:   Self-healing, no human needed
```

**Scenario 2 -- Tracking table update fails after blob write succeeds:**

```
Blob confirmed written
Tracking INSERT/UPDATE fails

State:    ADLS file exists, tracking row not
          updated, index row not touched
Offset:   NOT committed
Recovery: Kafka re-delivers, blob overwritten
          (idempotent), tracking write retried
Result:   Self-healing, no human needed
```

**Scenario 3 -- Index write fails after all files confirmed received:**

```
All required files in snapshot_tracking
Consumer reads header.json from ADLS
snapshot_index UPSERT fails

State:    ADLS all files exist,
          tracking status still RECEIVING
          (not yet updated to COMPLETE),
          index row not written,
          snapshot invisible in grid
Offset:   NOT committed
Recovery: Kafka re-delivers header message
          Blob overwritten (idempotent)
          Tracking row already shows all files
          Index UPSERT retried
Result:   Self-healing for transient failures
```

**Scenario 4 -- Consumer pod crashes mid-processing:**

```
Pod crashes at any point before
Kafka offset commit

State:    Depends on crash point,
          covered by scenarios 1, 2, 3
Offset:   NOT committed
Recovery: Kafka re-delivers to next pod
          All writes are idempotent
          Any pod can safely retry
Result:   Self-healing, no human needed
```

**Scenario 5 -- Index write succeeds but Kafka offset commit fails:**

```
All writes succeed including index row
Snapshot visible in grid
Kafka offset commit fails

Recovery: Kafka re-delivers header message
          Blob overwritten (idempotent)
          Tracking row already COMPLETE
          Index UPSERT succeeds silently
          No duplicate row
Result:   Self-healing, no human needed
```

---

## 9. Failure Handling and Recovery

The system does not roll back on failure. Data that has been successfully written is never deleted as a result of a downstream failure. The recovery model is always forward -- retry, complete, or flag for investigation.

**Failure classification.** There is no fixed-attempt in-process retry ladder. Instead, `SnapshotRequestCommand` classifies every exception raised while processing a message into one of three outcomes, using a declarative `ITransientFailureClassifier` per infrastructure dependency (SQL, blob) plus a BCL-only network classifier:

- **Rejected** -- an envelope or contract violation (see "Rejected message" below).
- **Poison** -- any other exception, once the message has been durably recorded as a FAILED tracking row. Not retryable: the same bytes would fail identically forever, so the offset is committed past it, deliberately.
- **Transient infrastructure failure** -- recognised by a classifier as a condition expected to resolve on its own (SQL/blob throttling, timeout, network blip). A recognised SQL or blob error is transient unless it is one a single message's own content can cause (a bad value, a constraint violation, an absent header blob); every other recognised infrastructure error is treated as transient, deliberately, because an allow-list of transient codes can never be complete and an unknown infrastructure error must never be committed away. The worker logs a Critical operations alert, sets a non-zero exit code, calls `IHostApplicationLifetime.StopApplication()`, and then parks for 30 seconds -- comfortably inside Kafka's `max.poll.interval.ms` (default 300 seconds) -- before returning failure. The park exists because stopping the host only signals shutdown, it does not suspend the consume loop; without it, the loop would take the next message, succeed, and commit a HIGHER offset, permanently skipping the still-uncommitted failed one (Kafka commits are positional). Kubernetes (`restartPolicy: Always`, CrashLoopBackOff on repeated failure) then restarts the pod, and Kafka redelivers the message from the last committed offset -- recovery is always forward via redelivery, never via in-process seek-back.

**Failure summary:**

|Failure point|Auto recovery|Human needed|User impact|Data lost|
|---|---|---|---|---|
|Blob write fails (transient)|Yes -- park + pod restart, Kafka redelivery|No|Snapshot delayed|No|
|Tracking write fails (transient)|Yes -- park + pod restart, Kafka redelivery|No|Snapshot delayed|No|
|SQL unavailable (tracking or index)|Yes -- park + pod restart, Kafka redelivery|No|Snapshot delayed|No|
|Consumer pod crashes|Yes -- Kafka re-delivery|No|None|No|
|Daily job fails|Yes -- next scheduled run|No|None|No|
|Files never arrive (stale)|Detected by daily job|Yes -- investigate|Snapshot never visible|No (blobs deleted, logged)|
|Message rejected (bad envelope)|Not retryable -- producer must resend|Yes -- producer fixes the message|Snapshot delayed, still recoverable|No (nothing was written)|

**Rejected message.** A message whose envelope is unusable (null, over-long or path-unsafe identity field; empty or syntactically invalid payload; payload type outside the snapshot type's file contract) is non-retryable: the same bytes would fail identically forever and block the partition. It is refused before any payload is written -- no blob, no index row -- and handled as follows: the snapshot's tracking row is set to FAILED with the reason (`{reasonCode}: {detail}`) and declared_failed_at, a Failed response carrying reasonCode and reasonDetail is published to the producer on the response topic, and only then is the offset committed past the message. If the snapshot has no tracking row yet, a minimal FAILED row is inserted so the rejection is visible in SQL; if it is already COMPLETE, the row is left untouched.

FAILED is not terminal here. A later valid message for the same snapshot returns the row to RECEIVING, clears the reason and declared_failed_at, and the snapshot completes normally once all required files have arrived -- so a producer that resends a corrected message needs no intervention.

In no failure scenario is permanent audit data deleted or corrupted. The worst outcome is a snapshot delayed until the issue is resolved and the message re-processed, or a snapshot eventually declared failed and logged for investigation with full traceability of what was received and what was missing.

---

## 10. Read Path -- The Two Screens

**Screen 1 -- Load snapshots grid**

The UI enforces that at least one portfolio must be selected before the grid loads. The SQL query always has at least one account_id in the IN clause. It hits the non-clustered index on (account_id, snapshot_date DESC), applies the date range as a row range within the year partition, and optionally filters event_type server-side. The last-7-days date range is a UI convention, not API behaviour: the UI sends explicit from/to values when it wants that view. account_id is the only mandatory filter -- the API applies no implicit default to any optional filter, so from, to and event_type each constrain the query only when the caller supplies them, and an omitted date bound is an open bound. The Read API deserialises the display_data JSON column in application code before returning to the UI. Typical query across 5-20 accounts returns in under 100ms.

**Screen 2 -- View a snapshot detail**

Clicking a grid row reads the adls_path column already in the grid result. No second SQL query needed. The Read API fetches blob files in load order matching the UI tab structure:

On row click -- fetch header.json. Summary header bar and cash section render immediately.

After header renders -- fetch orders.json page 1 (first 50 rows). Further pages load on scroll. Tab switching between All, Draft orders, PTC, and Trading is client-side filtering on already-loaded data with no additional API call.

On Orders tab click -- fetch orders.json.

On Portfolio section open -- fetch portfolio.json. This is the only heavy fetch and most audit users never trigger it.

No server-side search is required within a snapshot detail. All filtering is client-side on already-loaded data.

The two production routes above (`GET /snapshots/api/portfolio-snapshots` and
`GET /snapshots/api/portfolio-snapshots/{snapshotId}/payloads[/{payloadType}]`) always read
real Azure SQL and ADLS Gen2 data and are available in every environment.

---

## 11. Technologies Explored and Not Selected

**Azure Table Storage** was a strong candidate for the permanent index table -- schemaless, extremely cheap, Azure native. It was set aside in favour of Azure SQL because Table Storage does not support the IN operator and optional multi-field filtering requires property scans with no secondary indexes. Azure SQL on an existing server offers richer indexing at effectively zero additional cost.

**Redis for in-flight tracking** was the original design for the snapshot completeness tracking layer. It was replaced with a dedicated Azure SQL table per architect direction, for two reasons: Redis persistence is not currently planned to be enabled in this environment, meaning a Redis restart would silently lose all in-flight tracking state with no recovery path; and Redis would introduce a net-new infrastructure service when Azure SQL is already available and operated by the team. The performance concern with a permanent SQL tracking table -- write contention as the table grows over years -- is addressed by giving this table a strict 30-day rolling retention, which keeps it bounded to roughly 30,000-120,000 rows regardless of how long the system runs.

**Cosmos DB for NoSQL** auto-indexes all fields which handles optional filters well. It was not selected because the RU/s billing model is expensive for a low-write, low-read workload and is a net-new service.

**Azure Cosmos DB for PostgreSQL** offers open-source economics. It was not selected because it requires provisioning a new managed service when Azure SQL skills and infrastructure already exist.

**Azure Data Explorer (ADX / Kusto)** is purpose-built for append-only immutable time-series at massive scale. It is technically strong but overkill for the current write volume and user count. It remains the recommended upgrade path if query complexity or user volume grows significantly.

**Azure Synapse Analytics** was explicitly avoided because Microsoft is retiring Synapse in favour of Microsoft Fabric.

**Single JSON blob per snapshot** was rejected because it forces the Read API to download the entire payload to render only the summary header and prevents lazy loading of tabs.

**Databricks Delta for serving** was discussed because the team already has it in the MI MDS stack. It was not selected for the serving layer because Delta is not designed for synchronous single-row inserts from a Kafka consumer. It remains a future candidate for cross-snapshot analytical queries on top of the same ADLS blob files if that requirement arises.

---

## 12. Cost Comparison

**Data volume at worst case over 10 years:**

The permanent snapshot_index table accumulates 1,000 snapshots per day x 365 days x 10 years = 3,650,000 rows. The snapshot_tracking table never exceeds roughly 120,000 rows due to its 30-day rolling purge, regardless of how many years the system runs.

**Azure SQL -- both tables on existing server:**

|Component|Cost|Notes|
|---|---|---|
|Compute|$0 additional|Shared existing instance already paid|
|snapshot_index storage|Minimal|3.65M thin rows fits within included tier storage|
|snapshot_tracking storage|Negligible|Bounded at ~120,000 rows by 30-day purge|
|10-year total|Negligible|Effectively zero on existing infrastructure, no new service|

**ADLS Gen2 payload store:**

|Tier|Cost per GB per month|Notes|
|---|---|---|
|Hot|~$0.018|Recent active data|
|Cool|~$0.013|After 30 days via lifecycle policy|
|Cold|~$0.004|After 90 days -- recommended for audit archive|

**Current approach for comparison:**

Storing fat payloads in Azure SQL at ~$0.115 per GB per month is why the 30-day deletion policy exists today. Moving JSON files to ADLS blob reduces payload storage cost by 10-30x while enabling 10-year retention that was previously not viable.
