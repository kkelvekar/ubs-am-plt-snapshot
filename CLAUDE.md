@AGENTS.md
@.claude/workflow.md

## Claude Code specifics

Users describe the outcome; do not require them to name a workflow or role. Route repository
mutation, formal review, and testing requests through the classification and orchestration
contract in `.claude/workflow.md`, which owns gates, routing, and completion. Answer ordinary
non-mutating questions, explanations, status checks, read-only exploration, and documentation
lookups directly.

The four workflow roles are implemented as subagents in `.claude/agents/` (`planner`, `dev`,
`reviewer`, `tester-e2e`). The main conversation is the coordinator: subagents do not share
context and cannot invoke one another, so it carries verdicts and findings between roles.

Always preserve the project constraints in `AGENTS.md` and never expose secrets.
