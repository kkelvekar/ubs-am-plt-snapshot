# Codex Workflow Notes

Read `AGENTS.md` first. It is the binding project contract for all agents.

## Relationship to `.claude/agents`

The Claude files under `.claude/agents/` are role contracts, not tooling that Codex can
invoke directly. Codex should reuse their workflow semantics by running the same checks in
sequence and carrying findings between phases in the main conversation.

For non-trivial implementation slices, use this pipeline:

1. **Architect validation**: before editing, read `AGENTS.md`, the relevant sections of
   `docs/Portfolio Snapshot - Solution Design - Final Draft.md`, and relevant existing
   code. Produce a brief with scope, placement, invariants, and acceptance criteria.
2. **Development**: implement only the approved scope. Keep dependencies inward:
   `Domain <- Application <- Infrastructure <- Worker`. Add focused tests for the slice.
3. **Review**: inspect the diff read-only for design conformance, write-order,
   idempotency, offset-last behavior, configuration, and test coverage. Classify findings
   as code-level or design-level.
4. **E2E verification**: after review approval, verify with committed/in-process tests
   where applicable and live local tooling when the slice touches the real consume path.

Code-level findings go back through development. Design-level findings go back through
architect validation. After any fix, repeat review before broader verification.

## Codex-specific execution

- Prefer `rg`/`rg --files` for repo inspection.
- Before editing, state the files and intent briefly.
- Use `apply_patch` for manual edits.
- Do not add deployment artifacts; local infrastructure remains ad hoc tooling under
  `tools/`.
- Run `dotnet build` and the relevant `dotnet test` command before handoff when code has
  changed. If local services are required and unavailable, report the exact blocker.
