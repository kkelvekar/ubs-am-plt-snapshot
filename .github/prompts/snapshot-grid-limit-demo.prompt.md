---
name: snapshot-grid-limit-demo
description: Implement a small Snapshot Writer read-API slice to demonstrate planner, developer, reviewer, and tester orchestration.
agent: snapshot-workflow
---

Implement this small Snapshot Writer feature slice through the repository orchestration workflow.

## Feature

Add an optional `limit` query parameter to the existing endpoint:

`GET /snapshots/api/portfolio-snapshots`

Requirements:

- Omitted `limit` preserves the existing behavior.
- Validate `limit` in the Application layer.
- Accept values from 1 through 1000 inclusive.
- Reject 0, negative values, and values above 1000 through the existing API validation and ProblemDetails 400 path.
- Apply the value with parameterized SQL `TOP (@limit)`.
- Never interpolate caller-supplied values into SQL text.
- Preserve account, date, and event-type filters.
- Preserve `ORDER BY SnapshotDate DESC`.
- Do not add pagination metadata or continuation tokens.

## Architecture

- Preserve the existing Clean Architecture boundaries.
- Add the property to the existing HTTP query model.
- Map it through the existing controller into `SnapshotGridFilter`.
- Validate it in `SnapshotGridFilter.Resolve`.
- Change only the existing `PortfolioSnapshotIndexRepository` query builder for SQL behavior.
- Do not add new ports, domain types, schema changes, Kafka changes, blob changes, tracking changes, or write-path changes.

## Tests and documentation

Add focused tests for omitted, valid, zero, negative, and over-maximum limits; parameterized `TOP (@limit)`; absence of the raw value from SQL text; preserved filters; and descending date ordering.

Update the README API documentation and the Screen 1 read-path section of the solution design. Explain that `limit` caps results but does not provide pagination.

Run the focused tests and full solution build and tests, then complete both tester modes through the configured local application path. Use existing documented configuration and already-available services. Do not invent, replace, regenerate, or manually substitute connection values. If a required dependency is unavailable after the one permitted documented setup attempt, return `Verdict: FAIL`.

## Orchestration

Use the repository workflow in order:

1. `snapshot-planner` returns `READY_FOR_IMPLEMENTATION` with scope, placement, invariants, implementation direction, and acceptance criteria.
2. `snapshot-developer` implements only the approved scope, adds focused tests, and reports changed files and validation.
3. `snapshot-reviewer` performs a pragmatic code review. Block only for concrete correctness, security, regression, public-contract, core-invariant, or materially missing-test issues. Treat style preferences, optional refactors, and unavailable live-service evidence as non-blocking suggestions.
4. `snapshot-tester` runs Mode A and Mode B, including focused tests, full build and tests, and a local `curl` check against `/snapshots/api/portfolio-snapshots`, then returns `PASS` or `FAIL` with exact evidence.

Report the final changed files, commands, test results, reviewer verdict, and tester verdict. Do not create handoff or artifact files solely to transfer workflow state.
