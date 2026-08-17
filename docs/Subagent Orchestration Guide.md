# Subagent Orchestration in GitHub Copilot — Concepts and a Worked Example

> Audience: anyone who wants to understand how GitHub Copilot's custom agents and
> subagents work, and how to design their own multi-agent orchestration. Part 1 is
> pure product knowledge (not specific to this repository). Part 2 shows how this
> repository, the Snapshot Writer API, applies that knowledge to orchestrate a real
> plan → build → review → test pipeline.
>
> Sources are official Visual Studio Code / GitHub Copilot documentation, linked
> throughout and listed in [References](#references). Nothing here is guessed —
> where the docs did not give a definitive answer, that is called out explicitly.

## Table of contents

1. [Part 1 — How custom agents and subagents work in GitHub Copilot](#part-1--how-custom-agents-and-subagents-work-in-github-copilot)
2. [Part 2 — Applied example: the Snapshot Writer workflow](#part-2--applied-example-the-snapshot-writer-workflow)
3. [Part 3 — How to design your own subagent orchestration](#part-3--how-to-design-your-own-subagent-orchestration)
4. [References](#references)

---

## Part 1 — How custom agents and subagents work in GitHub Copilot

### 1.1 What is a custom agent?

A **custom agent** is a reusable persona for Copilot Chat: a fixed set of
instructions, a restricted tool list, and (optionally) a specific model, all
packaged into one file. Instead of manually re-selecting tools and re-typing
instructions every time you want a "planner" mindset versus an "implementer"
mindset, you switch to the matching custom agent and Copilot applies that
configuration automatically.

Custom agents were previously called **custom chat modes** (`.chatmode.md` files).
The concept is unchanged; only the file extension and terminology changed to
`.agent.md`.

Custom agents are plain Markdown files with YAML frontmatter:

```markdown
---
name: security-reviewer
description: Reviews code for security vulnerabilities
tools: ['read', 'search']
---

You are a security-focused code reviewer. Look for injection risks, missing
input validation, secret exposure, and authentication/authorization gaps.
```

### 1.2 Where custom agent files live

| Scope | Location |
|---|---|
| Workspace (shared via source control) | `.github/agents/*.agent.md` |
| Workspace, Claude-compatible format | `.claude/agents/*.md` |
| User profile (personal, all workspaces) | `~/.copilot/agents/` |
| Organization-wide (GitHub) | Defined at the GitHub organization level; discovered automatically when `github.copilot.chat.organizationCustomAgents.enabled` is `true` |

Additional workspace locations can be added with the `chat.agentFilesLocations`
setting, and `chat.useCustomizationsInParentRepositories` lets a monorepo
subfolder discover agent files stored at the repository root.

### 1.3 Frontmatter reference

All frontmatter fields are optional; a bare Markdown body with no frontmatter is
a valid custom agent.

| Field | Purpose |
|---|---|
| `name` | Agent name. Defaults to the file name if omitted. |
| `description` | Shown as placeholder text in the chat input when the agent is selected. |
| `argument-hint` | Hint text guiding how to invoke the agent. |
| `tools` | List of tool/tool-set names available to this agent (built-in, tool sets, MCP tools, or extension-contributed). Use `<server-name>/*` to include every tool from an MCP server. |
| `agents` | Which custom agents this agent may invoke **as subagents**. `*` = all, `[]` = none, or an explicit list. Requires `agent` (or `runSubagent`) to also be present in `tools`. |
| `model` | A single model name, or a prioritized array tried in order until one is available. Falls back to the model picker's current selection if omitted. |
| `user-invocable` | Whether the agent shows up in the agents dropdown (default `true`). Set `false` to make an agent subagent-only. |
| `disable-model-invocation` | Prevents the agent from being picked as a subagent by another agent (default `false`). |
| `target` | The environment the agent is meant for: `vscode` or `github-copilot`. |
| `mcp-servers` | MCP server configuration for agents run with `target: github-copilot`. |
| `handoffs` | Suggested next-agent buttons offered after a response (label, target agent, prompt, auto-send flag, optional model). |
| `hooks` (preview) | Hook commands scoped to only run while this agent is active. |
| `infer` | **Deprecated** — superseded by `user-invocable` + `disable-model-invocation`. |

The Markdown **body** of the file is prepended to the user's prompt whenever
that agent is active (as the main agent) or invoked (as a subagent). It is where
the persona's actual working instructions live — the frontmatter only wires up
capabilities.

### 1.4 What is a subagent, and why use one?

A **subagent** is a custom (or built-in) agent invoked *by another agent*, in an
isolated context, to do one bounded piece of work and return a summary. The
parent agent's context window is not polluted by the subagent's intermediate
tool calls — only the final result comes back.

Typical reasons to reach for a subagent instead of doing everything inline:

- **Context isolation** — a long research or exploration pass would otherwise
  fill up the main conversation with noise.
- **Different capabilities per task** — a planning task should not have edit
  tools; an implementation task needs them.
- **Different cost/latency per task** — route cheap, narrow tasks to a faster
  or cheaper model, and reserve the most capable model for the step that
  actually needs it.
- **Parallelism** — several independent subagents (e.g. multiple review
  perspectives) can run side by side and be synthesized afterward.

Each subagent invocation is **stateless**: the parent cannot send it a
follow-up message, so the task and every piece of context the subagent needs
must be included in the initial call. Built-in conveniences such as
clarifying-question tools and todo-list tools are not available inside a
subagent.

### 1.5 How subagents are invoked

Subagents are normally **agent-initiated**, not something a user manually
triggers turn by turn. Two things make this possible:

1. The parent agent (or agent/prompt file) must have the `agent` (a.k.a.
   `runSubagent`) tool enabled in its `tools` list.
2. The parent's instructions describe *when* to delegate — e.g. "use a subagent
   to research X before implementing."

By default, a subagent **cannot itself spawn subagents** — this avoids runaway
recursion. Enabling `chat.subagents.allowInvocationsFromSubagents` allows nested
delegation, up to a maximum nesting depth of 5, and is what makes recursive
"divide and conquer" agents possible.

A subagent, when it is a *custom* agent, overrides the parent's model and tools
with its own configured values; when the invocation targets a generic subagent
with no custom agent specified, it inherits the parent's model and tools.

**Model selection priority for a subagent run:**

1. An explicit model requested by the parent at invocation time.
2. The `model` property in the subagent's own `.agent.md` frontmatter.
3. The model currently running the parent conversation.

A subagent can never be given a **more expensive** model tier than the one
driving the parent conversation; if requested, the run is rejected and the
available alternatives are reported instead.

### 1.6 Restricting which subagents an agent may use

Because agent names/descriptions can be ambiguous, the `agents` frontmatter
property lets a coordinator whitelist exactly which subagents it may call:

```markdown
---
name: TDD
tools: ['agent']
agents: ['Red', 'Green', 'Refactor']
---
Implement the following feature using test-driven development. Use subagents to
guide the following steps:
1. Use the Red agent to write failing tests
2. Use the Green agent to implement code to pass the tests
3. Use the Refactor agent to improve the code quality
```

An agent explicitly listed in another agent's `agents` array remains callable
by that coordinator even if the callee itself sets
`disable-model-invocation: true` — i.e. an agent can be "locked" against
*general* subagent use while still being reachable by one specific, trusted
orchestrator.

### 1.7 Orchestration patterns

Two patterns from the official docs cover most real orchestration needs:

- **Coordinator/worker pattern** — a coordinator agent owns the overall
  workflow and never edits or tests anything itself; it only sequences calls to
  specialized worker agents (e.g. planner → implementer → reviewer), each with
  its own tool access and model.
- **Multi-perspective review** — several subagents review the *same* change
  from different angles (correctness, security, architecture, code quality) in
  parallel, and the parent synthesizes their independent findings into one
  report.

**Handoffs vs. subagents** — these solve different problems and are not
interchangeable. A **handoff** is a user-facing button that appears after a
response and lets a human choose to switch to another agent with a pre-filled
prompt; it is a *manual*, reviewed step-through. A **subagent** invocation is
*agent-initiated* and automatic, with no human approval gate between steps by
default. A pipeline that needs a human to review and approve each stage should
use handoffs; a pipeline that needs the model to autonomously delegate and
recombine work should use subagents.

---

## Part 2 — Applied example: the Snapshot Writer workflow

This repository is a working, in-production-style example of the
coordinator/worker pattern from §1.7. It uses five agent files, all in
`.github/agents/`, to turn "implement a feature" into a governed
plan → build → review → test pipeline with no separate approval clicks needed
from the developer — the coordinator drives every step itself.

### 2.1 The five files

| File | Role | `user-invocable` | Tools | Model |
|---|---|---|---|---|
| [workflow.agent.md](../.github/agents/workflow.agent.md) | Coordinator (`snapshot-workflow`) | *(default `true`)* | `agent, read, search, edit, execute` | *(inherits selection)* |
| [planner.agent.md](../.github/agents/planner.agent.md) | Read-only planner (`snapshot-planner`) | `false` | `read, search` | `GPT-5.6 Terra (copilot)` |
| [developer.agent.md](../.github/agents/developer.agent.md) | Implementer (`snapshot-developer`) | `false` | `read, search, edit, execute` | `Claude Sonnet 5 (copilot)` |
| [reviewer.agent.md](../.github/agents/reviewer.agent.md) | Read-only reviewer (`snapshot-reviewer`) | `false` | `read, search, execute` | `['GPT-5.6 Sol (copilot)', 'Claude Sonnet 5 (copilot)']` |
| [tester.agent.md](../.github/agents/tester.agent.md) | Read-only verifier (`snapshot-tester`) | `false` | `read, search, execute` | `Claude Sonnet 5 (copilot)` |

This table is a direct illustration of the frontmatter reference in §1.3:

- **`user-invocable: false`** on the four workers means none of them appears in
  the agents dropdown — a developer never accidentally "becomes" the reviewer.
  They are reachable only as subagents, invoked by the coordinator (§1.6).
- **Tool scoping mirrors responsibility.** The planner, reviewer, and tester
  have no `edit` tool at all — they are structurally incapable of changing
  files, regardless of what they are asked to do. Only `snapshot-developer`
  carries `edit`, because implementation is the only role permitted to mutate
  the repository. This is the least-privilege principle from §1.3/§1.7 applied
  literally.
- **Model choice is tuned per task.** The reviewer lists a *prioritized array*
  of two models — if the first is unavailable, Copilot falls back to the
  second, per the model-selection rule in §1.5. The developer pins a strong
  coding model; the planner pins a distinct model suited to architecture
  reasoning.
- **The coordinator restricts its own subagents.** `workflow.agent.md` declares:

  ```yaml
  agents: ['snapshot-planner', 'snapshot-developer', 'snapshot-reviewer', 'snapshot-tester']
  ```

  This is the `agents` allow-list from §1.6 — it prevents the coordinator from
  ever selecting an unrelated or ambiguous agent, and it is why `agent` also
  appears in the coordinator's own `tools` list (a prerequisite for using
  `agents` at all).

### 2.2 What the coordinator actually does

`snapshot-workflow` never edits, reviews, or tests anything itself — it is a
pure dispatcher, matching the coordinator/worker pattern in §1.7:

1. **Classifies the request** into one of seven ordered outcomes: plan-only,
   test-only, review-only, full feature/fix delivery, read-only
   question/investigation, trivial static edit, or out-of-scope/ambiguous. This
   classification step exists because a single coordinator agent has to serve
   many different shapes of request, not just "implement a feature."
2. **Runs the full delivery pipeline** for classification 4 only:
   `snapshot-planner` → (gate: `READY_FOR_IMPLEMENTATION`) → `snapshot-developer`
   → `snapshot-reviewer` → (gate: `APPROVED`) → `snapshot-tester`.
3. **Routes findings back**, not just forward: a reviewer or tester failure is
   itself classified (`level: code` vs. `level: design`/`level:
   acceptance-contract`) and sent back to the specific worker responsible —
   `snapshot-developer` for code-level fixes, `snapshot-planner` for design-level
   rework — then the pipeline re-runs downstream stages. This is the
   "iterate until convergence" idea from the coordinator/worker pattern,
   made concrete with an explicit **iteration budget of three** to guarantee
   termination instead of looping indefinitely.
4. **Keeps handoffs lean.** Each worker returns a compact, contract-shaped
   response (verdict + fixed fields) rather than a free-form narrative, and the
   coordinator passes only the active verdict, scope, and evidence forward —
   directly addressing the "don't pollute context" motivation for subagents
   from §1.4.

### 2.3 Why each worker is read-only or not, concretely

- **`snapshot-planner`** is read-only by design: a plan is a *contract*
  (`Verdict`, `Scope`, `Placement`, `Invariants`, `Implementation direction`,
  `Acceptance criteria`), not code. Keeping it read-only guarantees the
  architecture decision is made before a single line changes.
- **`snapshot-developer`** is the only agent with `edit`/`execute` because
  implementation is the only step in this workflow that is supposed to touch
  the repository. It cannot mark its own work approved — self-approval is
  explicitly disallowed in its response contract, forcing every change through
  `snapshot-reviewer`.
- **`snapshot-reviewer`** and **`snapshot-tester`** both keep `execute` (to run
  builds/tests/diff checks) but never `edit` — they can *observe* the
  repository's build/test state but cannot change it, which is what makes their
  verdicts trustworthy as an independent check on the developer's own claims.

### 2.4 Response contracts as the "protocol" between agents

Because each subagent invocation is stateless (§1.4), the Snapshot Writer
agents don't rely on free-flowing conversation to hand off work — every worker
returns a fixed-shape structured response (e.g. reviewer returns exactly
`Verdict` + `Findings` + `Suggestions`, tester returns exactly `Verdict` +
evidence sections). This is a deliberate discipline layered on top of the
platform's subagent mechanism: the platform only guarantees that a summary
comes back, so the project defines *what that summary must contain* to make
routing (§2.2, step 3) deterministic.

### 2.5 Not part of this pattern: handoffs

This project does **not** use the `handoffs` frontmatter property (§1.7). Every
transition between planner → developer → reviewer → tester is fully
agent-driven by the coordinator, with no user button-click gate in between.
That is a deliberate choice appropriate for a CI-like, "describe the outcome,
get the outcome" workflow — a different project that wants a human to review
and approve each stage explicitly would reach for `handoffs` instead of (or
alongside) subagents.

---

## Part 3 — How to design your own subagent orchestration

A general recipe, generalized from Parts 1 and 2:

1. **Identify the distinct roles** your workflow needs (e.g. plan, implement,
   review, test) and, for each one, decide the minimum tools it truly needs.
   Read-only roles (planning, review, verification) should never receive
   `edit`.
2. **Create one `.agent.md` file per role** under `.github/agents/` (or
   `.claude/agents/` if you need Claude-format compatibility). Give each a
   clear `name` and `description`.
3. **Set `user-invocable: false`** on every worker agent that should only ever
   be reached as a subagent, so users can't accidentally select it as their
   main chat agent.
4. **Pick a model per role**, not just one model for everything — cheaper/faster
   models for narrow mechanical tasks, stronger models for design or
   ambiguous judgment calls. Use a prioritized array where you want automatic
   fallback.
5. **Write one coordinator agent** whose `tools` includes `agent`, whose
   `agents` property explicitly lists only the intended workers, and whose body
   defines: how to classify incoming requests, the ordered pipeline for the
   main case, and explicit routing rules for what happens when a worker reports
   a failure (which worker gets the fix, and how far back in the pipeline to
   re-run).
6. **Define a response contract per worker** (fixed fields such as
   `Verdict`/`Findings`/`Evidence`) so the coordinator can parse and route
   results reliably instead of interpreting free text.
7. **Bound the loop.** Always give the coordinator an explicit maximum number
   of retry/iteration cycles and an escalation path back to the user — subagent
   pipelines that can route findings backward need a hard stop condition.
8. **Decide handoffs vs. subagents deliberately** (§1.7): use `handoffs` where
   a human must approve each stage; use subagents where the coordinator should
   drive the whole pipeline autonomously.
9. **Test the routing, not just one happy path** — verify plan-only requests,
   review-only requests, and the full pipeline all classify and terminate
   correctly, plus at least one finding-routing (failure → correct worker)
   scenario.

---

## References

- [Custom agents in VS Code](https://code.visualstudio.com/docs/copilot/customization/custom-agents) — frontmatter reference, file locations, handoffs, Claude agent format.
- [Subagents in Visual Studio Code](https://code.visualstudio.com/docs/agents/run/subagents) — invocation model, `agents`/`user-invocable`/`disable-model-invocation`, model-selection priority, nested subagents, orchestration patterns.
- [Create and manage agent customizations](https://code.visualstudio.com/docs/copilot/customization/overview) — Agent Customizations editor, customization scopes, monorepo discovery.
- This repository's own agent files: [workflow.agent.md](../.github/agents/workflow.agent.md), [planner.agent.md](../.github/agents/planner.agent.md), [developer.agent.md](../.github/agents/developer.agent.md), [reviewer.agent.md](../.github/agents/reviewer.agent.md), [tester.agent.md](../.github/agents/tester.agent.md).
