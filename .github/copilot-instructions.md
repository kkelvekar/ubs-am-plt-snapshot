# Copilot Instructions

Use the GitHub Copilot in VS Code workflow defined in `AGENTS.md` for every non-trivial
source, test, schema, API, worker, infrastructure, or solution-design change:
`snapshot-planner` -> `snapshot-developer` -> `snapshot-reviewer` -> `snapshot-tester`.
Implementation requires the planner's `Verdict: READY_FOR_IMPLEMENTATION`; acceptance testing
requires reviewer `Verdict: APPROVED`.

Invoke the named repository custom agents with the `agent` tool. Pass native responses directly
between roles; never create workspace handoff files. Route only reviewer `Findings`: `level: code`
to `snapshot-developer`, and `level: design` or `level: acceptance-contract` to
`snapshot-planner`. Never route `Suggestions`. Re-run review after every developer fix.

Preserve the invariants and role boundaries in `AGENTS.md`. Never print secrets or export secret
data. Live testing must use documented existing configuration and services; do not invent,
overwrite, regenerate, or manually substitute connection values. An unavailable required
dependency means `Verdict: FAIL`. Use local `curl` evidence for API slices.

Answer simple questions, explanations, read-only exploration, documentation lookups, and status
checks directly. If custom agents cannot be invoked on the active surface, state the limitation
and identify the exact next agent instead of bypassing the workflow.
