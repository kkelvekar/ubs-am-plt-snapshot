# Snapshot Writer API — Integration Guide

Part of the **Portfolio Snapshot solution** on the UBS Advantage F2B platform. This
document is for **consumers integrating with this service**: how to publish a
snapshot over Kafka, how to read the status responses this service publishes back,
and how to query stored snapshots via the Read API. It does not cover running this
repository locally — see `AGENTS.md` for that.

Two integration surfaces:

1. **Kafka** — publish snapshot payloads on the request topic, consume completeness/
   failure notifications on the response topic.
2. **HTTP Read API** — query the snapshot audit index and fetch stored payload JSON.

### Development live-test publisher

In the `Development` environment only, the Worker listens on `http://localhost:5106`
by default and can publish the bundled data-driven simulation through its configured
Kafka broker and request topic:

```bash
curl -X POST http://localhost:5106/api/live-tests/snapshots \
  -H "Content-Type: application/json" \
  -d '{"snapshotCount":1,"messageDelay":"00:00:00"}'
```

The optional body fields are `snapshotCount` (default `1`), `messageDelay` (default
`00:00:30`), `snapshotDelayMin` (default `00:01:00`), `snapshotDelayMax` (default
`00:02:00`), and `templateFileName` (default `snapshot-simulation-data.json`). The
response is returned only after Kafka acknowledges every generated message and includes
the snapshot IDs, message count, and delivery metadata. Invalid input returns `400`;
Kafka delivery failure returns `503`. This endpoint is absent outside Development.

---

## 1. Publishing a snapshot (Kafka request topic)

- **Topic**: `ubs-advantage-snapshots`
- **Message value**: JSON text, one message per payload file (a "snapshot" is made of
  several messages sharing the same `SnapshotId`)
- **Message key**: `AccountId` (recommended, for partition locality)
- **Schema**: `docs/snapshot-request.schema.json` — seven string properties,
  **PascalCase on the wire**. `Payload` is a JSON **string**, not a nested object —
  it must be the already-serialized payload document, escaped as a string value.

### Request schema

| Field | Type | Required | Notes |
|---|---|---|---|
| `SnapshotId` | string | yes | Correlation id shared by every payload of one snapshot. Max 100 chars. |
| `AccountId` | string | yes | Max 100 chars. |
| `SnapshotType` | string | yes | Max 100 chars. See valid values below. |
| `PayloadType` | string | yes | Max 100 chars. See valid values below. |
| `PublishedAt` | string | yes | Producer's publish timestamp. Log-only — not used in any business logic. |
| `PublishedBy` | string | yes | Producer identity, e.g. `"PortfolioCalculation"`. Log-only. |
| `Payload` | string | yes | The payload document, serialized to a JSON **string**. Must be syntactically valid JSON. Written to blob storage byte-for-byte verbatim — never modified. |

`SnapshotId`, `AccountId`, `SnapshotType`, `PayloadType` become segments of a blob
path, so each is restricted to `[A-Za-z0-9-_.]` (no `..`) as well as the length limit
above.

### Example request message

```json
{
  "SnapshotId": "corr20260727-0001",
  "AccountId": "00675442A",
  "SnapshotType": "portfolio",
  "PayloadType": "header",
  "PublishedAt": "2026-07-27T10:45:00.0000000Z",
  "PublishedBy": "PortfolioCalculation",
  "Payload": "{\"SnapshotId\":\"corr20260727-0001\",\"Type\":\"Header\",\"Payload\":{\"Event\":\"REBALANCE\",\"portfolioStatus\":\"APPROVED\",\"orderStatus\":\"SENT\",\"benchmark\":\"MSCI World\",\"baseCcy\":\"CHF\",\"programId\":\"PRG-7\",\"batchId\":\"BATCH-2026-07-13\",\"numOrders\":17,\"ptcAlerts\":2,\"orderApprovedBy\":\"approver@ubs.com\",\"orderApprovedAt\":\"2026-07-13T10:45:00Z\",\"orderSentBy\":\"sender@ubs.com\",\"orderSentAt\":\"2026-07-13T10:50:00Z\"}}"
}
```

`SnapshotType` and `PayloadType` valid values (current library-owned map — one entry per
snapshot type, adding a type is a code-constant change and library rebuild on this service's side):

```jsonc
{
  // SnapshotType — only "portfolio" is defined today.
  "SnapshotType": "portfolio",

  // PayloadType — for SnapshotType "portfolio", exactly these six are accepted.
  // A snapshot is COMPLETE once all six have arrived for the same SnapshotId.
  "PayloadType": "header" // | "portfolio" | "orders" | "compliances" | "orders-history" | "settings"
}
```

A snapshot is a set of related messages: publish one message per `PayloadType`, all
sharing the same `SnapshotId`/`AccountId`/`SnapshotType`. Order between the six
does not matter and any of them may be redelivered — writes are idempotent.

Only the `header` payload is ever inspected by this service (its `Payload.Event` field
is extracted for the audit grid). `portfolio`, `orders`, `compliances`, `orders-history`,
`settings` — and every
other field inside `header` — pass through completely opaque: send whatever JSON
document your payload type requires. A missing, invalid, blank, or over-length
`Payload.Event` rejects the snapshot with `INVALID_HEADER_EVENT`; malformed header JSON
remains retryable.

---

## 2. Snapshot status responses (Kafka response topic)

- **Topic**: `ubs-advantage-snapshot-responses`
- **Message value**: JSON text, one `SnapshotResponse` per status change
- Published as this service processes your messages — see **when responses are
  sent** below.

### Response schema

Schema: `docs/snapshot-response.schema.json`.

| Field | Type | Notes |
|---|---|---|
| `SnapshotId` | string | Matches the request's `SnapshotId`. |
| `AccountId` | string | |
| `ReceivedFiles` | string[] | Payload filenames received so far, e.g. `["header.json"]`. |
| `MissingFiles` | string[] | Required filenames not yet received. Empty once `Status` is `Complete` or `Failed`. |
| `Status` | string | One of `"Receiving"`, `"Complete"`, `"Failed"`. |
| `FirstReceivedAt` | string | ISO-8601 UTC, first payload's arrival time. |
| `LastUpdatedAt` | string | ISO-8601 UTC. |
| `CompletedAt` | string | ISO-8601 UTC. Empty unless `Status` is `Complete`. |
| `DeclaredFailedAt` | string | ISO-8601 UTC. Empty unless `Status` is `Failed`. |
| `ReasonCode` | string | Machine-readable failure cause. Empty unless `Status` is `Failed`. |
| `ReasonDetail` | string | Human-readable failure detail. Empty unless `Status` is `Failed`. |

There are effectively **two response shapes** you'll see: a progress/success
response (`Receiving` → `Complete`) and a failure response (`Failed`, with
`ReasonCode`/`ReasonDetail` populated).

#### Example — `Receiving` (first payload of a snapshot arrived)

```json
{
  "SnapshotId": "corr20260727-0001",
  "AccountId": "00675442A",
  "ReceivedFiles": ["header.json"],
  "MissingFiles": ["compliances.json", "orders-history.json", "orders.json", "portfolio.json", "settings.json"],
  "Status": "Receiving",
  "FirstReceivedAt": "2026-07-27T10:45:00.0000000Z",
  "LastUpdatedAt": "2026-07-27T10:45:00.0000000Z",
  "CompletedAt": "",
  "DeclaredFailedAt": "",
  "ReasonCode": "",
  "ReasonDetail": ""
}
```

#### Example — `Complete` (all required payloads received)

```json
{
  "SnapshotId": "corr20260727-0001",
  "AccountId": "00675442A",
  "ReceivedFiles": ["header.json", "portfolio.json", "orders.json", "compliances.json", "orders-history.json", "settings.json"],
  "MissingFiles": [],
  "Status": "Complete",
  "FirstReceivedAt": "2026-07-27T10:45:00.0000000Z",
  "LastUpdatedAt": "2026-07-27T10:45:15.0000000Z",
  "CompletedAt": "2026-07-27T10:45:15.0000000Z",
  "DeclaredFailedAt": "",
  "ReasonCode": "",
  "ReasonDetail": ""
}
```

#### Example — `Failed` (message rejected)

```json
{
  "SnapshotId": "corr20260727-0003",
  "AccountId": "00675442A",
  "ReceivedFiles": [],
  "MissingFiles": [],
  "Status": "Failed",
  "FirstReceivedAt": "2026-07-27T10:45:05.0000000Z",
  "LastUpdatedAt": "2026-07-27T10:45:05.0000000Z",
  "CompletedAt": "",
  "DeclaredFailedAt": "2026-07-27T10:45:05.0000000Z",
  "ReasonCode": "MALFORMED_PAYLOAD_JSON",
  "ReasonDetail": "Snapshot message payload is not syntactically valid JSON; rejecting the message before any payload is written."
}
```

`ReasonCode` values a producer may receive on `Failed`:

| ReasonCode | Cause |
|---|---|
| `NULL_REQUIRED_FIELD` | `SnapshotId`/`AccountId`/`SnapshotType`/`PayloadType` missing, null, or empty. |
| `FIELD_TOO_LONG` | One of the four identity fields exceeds its 100-character limit. |
| `INVALID_FIELD_CHARACTERS` | One of the four identity fields has a character outside `[A-Za-z0-9-_.]`, or contains `..`. |
| `EMPTY_PAYLOAD` | `Payload` is null, empty, or whitespace. |
| `MALFORMED_PAYLOAD_JSON` | `Payload` is not syntactically valid JSON. |
| `UNEXPECTED_PAYLOAD_TYPE` | `PayloadType` is not one of the files the given `SnapshotType` expects (e.g. `"invoices"` under `SnapshotType: "portfolio"`). |

### When responses are sent

- **`Receiving`** — published once, when the **first** payload of a new snapshot is
  successfully written (i.e. when `ReceivedFiles.Count` becomes 1). Not repeated for
  the 2nd/3rd payload of the same snapshot.
- **`Complete`** — published once, when the **last** required payload arrives and
  the snapshot's audit index row has been written. This is the confirmation the
  snapshot is now visible in the audit UI.
- **`Failed`** — published once per rejected message, **immediately**, before any
  blob write is attempted. A rejected message is not retried by this service —
  redelivery of the same bytes produces the same rejection.
- A redelivered payload for a snapshot that already reached `Complete` or `Failed`
  produces **no** further response (the write itself is still idempotent and safe).

There is no synchronous request/response pairing — responses are asynchronous,
correlate back to your request by `SnapshotId`, and a producer should not assume a
1:1 ordering between requests sent and responses received (e.g. `Complete` for
snapshot A can arrive before `Receiving` for snapshot B that was published earlier).

---

## 3. Reading snapshot data (HTTP Read API)

Two endpoints, matching the two audit-UI screens. Base address is
environment-specific (ask your platform contact); paths below are relative to it.

### 3.1 `GET /snapshots/api/portfolio-snapshots` — snapshot grid

Lists snapshots for one or more accounts, optionally filtered by date range and
event type. Returns only **completed** snapshots (a row is written only once a
snapshot is `Complete`).

**Query parameters**

| Param | Required | Notes |
|---|---|---|
| `accountIds` | yes | Repeatable, e.g. `?accountIds=00675442A&accountIds=00675443B`. At least one is required. |
| `from` | no | ISO-8601 date/time. Omitted = open lower bound. |
| `to` | no | ISO-8601 date/time. Omitted = open upper bound. |
| `event` | no | Literal substring filter on the snapshot's `eventType`; for example, `ModelChange` also matches a combined value containing `ModelChange`. |

Contains semantics are intentional because one snapshot may report more than one event in
the stored `eventType`. The complete upstream value set and the delimiter used for combined
values have not yet been confirmed; the API therefore treats the supplied `event` text as a
literal substring and does not parse or depend on a particular delimiter.

**Example request**

```
GET /snapshots/api/portfolio-snapshots?accountIds=00675442A&from=2026-07-01&to=2026-07-31&event=REBALANCE
```

**Example 200 response**

Each row is the fixed index columns plus every top-level field of the snapshot's
`header` payload's nested `Payload` object flattened in alongside them (fixed
columns win on name collision; `AdlsPath`/storage location is never exposed).

```json
[
  {
    "snapshotId": "corr20260727-0001",
    "accountId": "00675442A",
    "snapshotDate": "2026-07-27T10:45:00Z",
    "eventType": "REBALANCE",
    "createdAt": "2026-07-27T10:45:15Z",
    "Event": "REBALANCE",
    "portfolioStatus": "APPROVED",
    "orderStatus": "SENT",
    "benchmark": "MSCI World",
    "baseCcy": "CHF",
    "programId": "PRG-7",
    "batchId": "BATCH-2026-07-13",
    "numOrders": 17,
    "ptcAlerts": 2,
    "orderApprovedBy": "approver@ubs.com",
    "orderApprovedAt": "2026-07-13T10:45:00Z",
    "orderSentBy": "sender@ubs.com",
    "orderSentAt": "2026-07-13T10:50:00Z"
  }
]
```

### 3.2 `GET /snapshots/api/portfolio-snapshots/{snapshotId}/payloads/{payloadType}` — one payload

Returns the stored payload blob for one `payloadType` (e.g. `header`, `orders`,
`portfolio`, `settings`) of one snapshot, **verbatim** — the exact bytes that
were written, byte-identical to the original `Payload` sent on the request topic.

**Example request**

```
GET /snapshots/api/portfolio-snapshots/corr20260727-0001/payloads/portfolio
```

**Example 200 response** (`Content-Type: application/json`)

```json
{"nav":5555.55,"ccy":"CHF"}
```

### 3.3 `GET /snapshots/api/portfolio-snapshots/{snapshotId}/payloads` — all payloads

Returns every stored payload of the snapshot as one JSON object keyed by
`payloadType`, each value embedded verbatim.

**Example request**

```
GET /snapshots/api/portfolio-snapshots/corr20260727-0001/payloads
```

**Example 200 response**

```json
{
  "header": {"SnapshotId":"corr20260727-0001","Type":"Header","Payload":{"Event":"REBALANCE","portfolioStatus":"APPROVED","orderStatus":"SENT","benchmark":"MSCI World","baseCcy":"CHF","programId":"PRG-7","batchId":"BATCH-2026-07-13","numOrders":17,"ptcAlerts":2,"orderApprovedBy":"approver@ubs.com","orderApprovedAt":"2026-07-13T10:45:00Z","orderSentBy":"sender@ubs.com","orderSentAt":"2026-07-13T10:50:00Z"}},
  "orders": {"positions":[{"isin":"CH0038863350","qty":250}]},
  "portfolio": {"nav":5555.55,"ccy":"CHF"},
  "settings": {"tolerance":0.05}
}
```

### 3.4 Error responses

All three endpoints share one error shape: [RFC 7807](https://datatracker.ietf.org/doc/html/rfc7807)
`application/problem+json`, with a `traceId` extension for support correlation.

**400 Bad Request** — invalid query/route input (e.g. no `accountIds`, an inverted
`from`/`to` window, a missing or path-unsafe `payloadType`):

```json
{
  "status": 400,
  "title": "Invalid request.",
  "detail": "At least one accountId is required; an unfiltered all-rows query is not allowed.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

**404 Not Found** — no snapshot with that `snapshotId`, or no such `payloadType`
stored for it:

```json
{
  "status": 404,
  "title": "Not found.",
  "detail": "Snapshot 'corr-does-not-exist' was not found.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

**500 Internal Server Error** — unexpected server fault. Detail is always this
fixed generic message (never the underlying exception); use `traceId` when
reporting an issue:

```json
{
  "status": 500,
  "title": "An unexpected error occurred.",
  "detail": "An unexpected error occurred while processing your request.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

---

## Reference

The full functional contract (message flow, blob layout, SQL schema, sequence
diagrams) is in
[`docs/Portfolio Snapshot - Solution Design.md`](docs/Portfolio%20Snapshot%20-%20Solution%20Design.md).
The org-approved request wire schema is
[`docs/snapshot-request.schema.json`](docs/snapshot-request.schema.json); the response wire
schema is [`docs/snapshot-response.schema.json`](docs/snapshot-response.schema.json).
