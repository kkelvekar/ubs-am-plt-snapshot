---
name: snapshot-grid-limit-demo
description: Add an optional result limit to the Snapshot Writer grid API.
agent: snapshot-workflow
---

Add an optional `limit` query parameter to `GET /snapshots/api/portfolio-snapshots`.

- Omitted `limit` preserves existing behavior.
- Validate it in the Application layer and accept 1 through 1000 inclusive.
- Reject other values through the existing ProblemDetails 400 path.
- Apply it with parameterized SQL `TOP (@limit)`; never interpolate caller input.
- Preserve all current filters and `ORDER BY SnapshotDate DESC`.
- Do not add pagination metadata or continuation tokens.
- Add focused tests for omitted, valid, zero, negative, and over-maximum values.
- Prove SQL uses parameterized `TOP (@limit)`, contains no raw caller value, preserves every filter, and retains descending snapshot-date ordering.
- Update the README API documentation and Screen 1 read-path solution design.
