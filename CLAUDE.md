@AGENTS.md

## Claude Code specifics

- The four workflow roles in AGENTS.md are implemented as subagents in `.claude/agents/`
  (`architect-validator`, `dev`, `reviewer`, `tester-e2e`). Delegate to them for any
  non-trivial implementation slice; the main conversation orchestrates the pipeline and
  carries verdicts/findings between roles, since subagents do not share context.
- Route feedback per AGENTS.md: code-level findings back to `dev`, design-level findings
  to `architect-validator`. After a dev fix, always re-run `reviewer` before `tester-e2e`.
- Loop budget 3 iterations, then stop and escalate to the user with the full history.
