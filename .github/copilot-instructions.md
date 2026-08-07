# Snapshot Writer Copilot Instructions

## Required workflow for non-trivial changes

For any request that changes source code, tests, database scripts, API behavior, worker behavior, infrastructure behavior, or the approved solution design, automatically start the repository workflow at `snapshot-architect`. The user should not need to select the architect agent or repeat this instruction.

The normal Agent session must:

1. Delegate the request first to `snapshot-architect` for a read-only architecture check.
2. Allow implementation only after an `APPROVED_BRIEF` is produced at `.artifacts/handshake/architect-brief.md`.
3. Continue through the repository custom-agent chain:
   `snapshot-architect` -> `snapshot-developer` -> `snapshot-reviewer` -> `snapshot-tester`.
4. Pass the approved brief and each handoff artifact to the next role.
5. Route `code-level` findings back to `snapshot-developer` and `design-level` findings back to `snapshot-architect`.
6. Re-run review after any developer fix. Do not skip a workflow stage.

The role boundaries are strict: architect and reviewer are read-only; developer edits only approved scope and adds focused tests; tester reports reproducible evidence and does not edit code. The workflow must preserve the invariants in `AGENTS.md`, especially blob -> tracking -> completeness -> index write order, idempotency, opaque payload handling, and offset commit last.

For simple questions, explanations, read-only exploration, documentation lookups, and status checks, answer directly without starting the implementation workflow. When the user explicitly asks to bypass the workflow, explain the repository rule and ask for confirmation before proceeding.

## Custom-agent invocation

Use the `agent` tool to invoke the named repository custom agents. Do not silently substitute built-in agents with similar names. If the current chat surface cannot invoke custom agents automatically, state that limitation and provide the exact next agent name rather than editing implementation files directly.
