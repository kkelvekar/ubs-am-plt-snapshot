# Copilot Instructions

## Required workflow for non-trivial changes

For any request that changes source code, tests, database scripts, API behavior, worker behavior, infrastructure behavior, or the approved solution design, automatically start the repository workflow at `snapshot-architect`. The user should not need to select the architect agent or repeat this instruction.

The normal Agent session must:

1. Delegate the request first to `snapshot-architect` for a read-only architecture check.
2. Allow implementation only after `snapshot-architect` returns an `APPROVED_BRIEF` in its native Copilot response.
3. Continue through the repository custom-agent chain:
   `snapshot-architect` -> `snapshot-developer` -> `snapshot-reviewer` -> `snapshot-tester`.
4. Pass the approved brief, implementation summary, review verdict, and tester evidence directly through native Copilot agent context.
5. Route `code-level` findings back to `snapshot-developer` and `design-level` findings back to `snapshot-architect`.
6. Re-run review after any developer fix. Do not skip a workflow stage.

Do not create `.artifacts/`, handoff files, or other workspace files solely to transfer workflow state between agents. Native Copilot agent responses are the source of truth for workflow handoffs. Create workspace files only when they are part of the requested product, source, test, or documentation change.

The role boundaries are strict: architect and reviewer are read-only; developer edits only approved scope and adds focused tests; tester reports reproducible evidence and does not edit code. The workflow must preserve the invariants in `AGENTS.md`, especially blob -> tracking -> completeness -> index write order, idempotency, opaque payload handling, and offset commit last.

## Live testing configuration rule

For live testing, use the client project's existing configuration and already-available local services first. Inspect the project's documented settings, verify the required dependencies are reachable, and run the real application path with the effective configuration already provided by the project or environment. Do not invent, overwrite, regenerate, or manually substitute connection values.

Use setup tools only when a required dependency is unavailable or the approved test explicitly requires creating a missing database, schema, or equivalent test resource. Do not start, replace, or tear down services that are already available. Live-test evidence must state the configuration source used, readiness checks performed, commands or actions executed, observed result, and the reason for any provisioning or resource mutation.

When the slice includes an API, test the locally running API only through `curl` against its configured `localhost` endpoint. Keep API live testing local; do not call external, shared, or cloud endpoints, and do not substitute browser automation for the `curl` request and response evidence.

For simple questions, explanations, read-only exploration, documentation lookups, and status checks, answer directly without starting the implementation workflow. When the user explicitly asks to bypass the workflow, explain the repository rule and ask for confirmation before proceeding.

## Custom-agent invocation

Use the `agent` tool to invoke the named repository custom agents. Do not silently substitute built-in agents with similar names. If the current chat surface cannot invoke custom agents automatically, state that limitation and provide the exact next agent name rather than editing implementation files directly.
