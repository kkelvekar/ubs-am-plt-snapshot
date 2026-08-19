# Subagent Orchestration, Explained Live

> Speaker script — no camera. Read the plain paragraphs aloud; the indented
> **On screen** blocks are stage directions (what to open, point at, or type).
> Time codes are a pace guide, not a stopwatch — if a segment runs long, the
> live demo (Section 6) is the one safe place to trim. Total runtime: ~19 min.

## Table of contents

1. [Open](#1-open) — 0:00–1:00
2. [The problem with how we use Copilot today](#2-the-problem-with-how-we-use-copilot-today) — 1:00–4:30
3. [The technique](#3-the-technique) — 4:30–9:00
4. [This repo, concretely](#4-this-repo-concretely) — 9:00–13:30
5. [How to set this up yourselves](#5-how-to-set-this-up-yourselves) — 13:30–16:30
6. [Live demo](#6-live-demo) — 16:30–20:00
7. [Close](#7-close) — 20:00
8. [Q&A backup](#qa-backup)

---

## 1. Open

**0:00–1:00**

Today I want to show you a technique, not a tool. Nothing here needs a new
subscription or a new platform — it's a way of structuring how we let AI
touch our codebase, and every one of you can copy it into your own repo this
week. It's called subagent orchestration, and I'm going to show you why it
produces better outcomes than how we use Copilot today, then show you the
actual files, then trigger it live and watch it work.

---

## 2. The problem with how we use Copilot today

**1:00–4:30**

Here's what all of us already do. You open Copilot Chat, you type "implement
X," and one agent — one continuous stream of reasoning — plans it, writes
it, writes tests for it, and tells you it's done. That's the entire
interaction. And it has four problems that get worse, not better, the bigger
the change is.

**It grades its own homework.** The same context that just wrote the code is
the one telling you it's correct. There's no independent check in between —
it's the same as a developer merging their own pull request with nobody else
looking at it.

**Planning and editing happen in the same breath.** There's no gate where a
design decision gets locked in before fifty file edits land on top of it. If
it picks a bad direction three sentences into its own reasoning, you don't
find out until you're staring at a full diff trying to reverse-engineer why
it did what it did.

**"Done" is a claim, not evidence.** It says the tests pass. Did it actually
run them? The right ones? You genuinely cannot tell from a paragraph of
prose.

**Every permission is on for the whole session.** The exploring-and-thinking
part of the task has full edit and execute access the entire time, even
though it has no reason to touch a file yet.

One agent, doing four different jobs, with nothing checking any of them.
That's the gap.

---

## 3. The technique

**4:30–9:00**

The fix is simple to say: stop using one agent for the whole job. Split it
into roles — plan, build, review, test — the same way a real engineering
team is split, and give each role only the permissions and context it
actually needs. Two ideas make this possible inside GitHub Copilot, and
they're both native features, not add-ons.

First, a **custom agent**. A role is just a markdown file — a persona
written in plain instructions, a list of tools it's allowed to use, a model.
Nothing exotic. It's checked into git like any other file, so the whole team
gets it the moment they clone the repo.

Second, a **subagent**. One agent — a coordinator — can call another agent
to do one bounded piece of work in a completely separate context, and only
the final structured result comes back. The coordinator's own conversation
never gets cluttered with the subagent's intermediate thinking.

Now, why does this actually produce better outcomes, and not just more
process for the sake of process?

1. **Independent review is real, not theater.** The reviewer is a different
   agent invocation with zero memory of writing the code. It cannot
   rubber-stamp its own work, because it never wrote anything.
2. **Least privilege is built in, not requested.** The planner and reviewer
   simply do not have an edit tool in their configuration. It isn't "please
   don't touch files" in the instructions — it is physically incapable of
   it, the same way you would not hand a code reviewer production write
   access.
3. **A plan becomes a contract.** The planner has to produce a fixed
   structure — scope, invariants, acceptance criteria — before a single file
   changes. A bad architectural call gets caught at the cheapest possible
   point: before code exists.
4. **Verification means evidence, not a claim.** The tester's entire job is
   to run real commands and report real output — build results, test
   results, actual API responses — not summarize what it thinks happened.
5. **Failures route to whoever owns the mistake, automatically,** and it's
   bounded — three iterations, then it stops and hands the whole verdict
   history to a human, so it can never loop forever.

> **On screen:** Nothing to open yet — keep talking. If you want a visual
> anchor, have the `.github/agents/` folder visible (collapsed) in the VS
> Code sidebar in the background.

---

## 4. This repo, concretely

**9:00–13:30**

Let me stop talking in the abstract and show you the actual files, because
there's nothing hidden here — five markdown files, in `.github/agents/`, and
that's the whole system.

| File | Role | Can edit? | Model |
|---|---|---|---|
| `workflow.agent.md` | Coordinator — classifies the request, drives the pipeline, never writes code itself | Yes* | inherited |
| `planner.agent.md` | Produces the plan contract | No | GPT-5.6 Terra |
| `developer.agent.md` | The only role that implements | Yes | Claude Sonnet 5 |
| `reviewer.agent.md` | Verdict: APPROVED / CHANGES_REQUESTED | No | Sol → Sonnet fallback |
| `tester.agent.md` | Verdict: PASS / FAIL, backed by real evidence | No | Claude Sonnet 5 |

*The coordinator technically has edit access but its own instructions
forbid using it — it only ever delegates.

The pipeline runs in one direction, with two gates. Planner returns
`READY_FOR_IMPLEMENTATION`, `NEEDS_CLARIFICATION`, or `BLOCKED`. Only
`READY_FOR_IMPLEMENTATION` continues. Developer implements, reviewer returns
`APPROVED` or `CHANGES_REQUESTED`. Only `APPROVED` continues. Tester returns
`PASS` or `FAIL`. A rejection at either gate gets classified — a code bug
goes back to the developer, a design problem goes back to the planner — and
the pipeline re-runs downstream from there, capped at three iterations total
before it stops and asks a human.

> **On screen:**
> 1. Open `.github/agents/workflow.agent.md`. Point at line 5:
>    `agents: ['snapshot-planner', 'snapshot-developer', 'snapshot-reviewer', 'snapshot-tester']`
>    — say: "this is an explicit allow-list, the coordinator can never call
>    anything outside these four."
> 2. Open `planner.agent.md`. Point at `tools: ['read', 'search']` — say: "no
>    edit tool. Full stop."
> 3. Open `developer.agent.md`. Point at
>    `tools: ['read', 'search', 'edit', 'execute']` — say: "this is the only
>    one of the five files with edit in it."
> 4. Flash `reviewer.agent.md` and `tester.agent.md` just long enough to show
>    the same missing `edit` tool.

---

## 5. How to set this up yourselves

**13:30–16:30**

This is the part I actually want you to walk away with, because none of
this is specific to our repo — it's a checklist you can run against any
project you own.

1. List the distinct roles your workflow actually needs. It doesn't have to
   be plan, build, review, test — a different repo might need different
   roles entirely.
2. One `.agent.md` file per role, in `.github/agents/`, checked into git so
   the whole team shares it.
3. Set `user-invocable: false` on every worker, so nobody accidentally picks
   "reviewer" as their main chat agent.
4. Give read-only roles no edit tool. Not a rule you write into the prompt —
   remove the capability entirely.
5. Pick a model per role. Cheap and fast for narrow mechanical work, your
   strongest model for judgment calls like planning and review.
6. Write one coordinator file. Its `tools` includes `agent`, its `agents`
   list names exactly the workers it may call, and its body defines the
   pipeline order plus what happens when a worker fails.
7. Give every worker a fixed response shape — a verdict plus a small number
   of required fields. That structure is the only thing crossing between two
   agents that share no memory, so it has to be unambiguous.
8. Bound the loop. A hard iteration cap, and an explicit point where it
   stops and asks a human instead of retrying forever.
9. Test the routing itself, not just the happy path — does a plan-only
   request actually stop at the planner? Does a rejected review actually
   come back to the developer?

That's the whole recipe. No new infrastructure, no new subscription —
markdown files and discipline about which role gets which tool. If anyone
wants the long-form version with sourcing, it's already written up in
[`docs/Subagent Orchestration Guide.md`](Subagent%20Orchestration%20Guide.md)
in this repo.

---

## 6. Live demo

**16:30–20:00**

Let's actually watch it run. I'm going to ask for something small but real —
not a typo fix, because that's small enough to skip the whole pipeline by
design — a genuine feature slice that needs a plan, an implementation, a
review, and a test.

> **On screen — before you start talking:**
> 1. Open Copilot Chat in VS Code, repo open at its root, nothing else in
>    the chat history.
> 2. Have
>    `src/Clients/UBS.AM.PLT.Snapshot.Api/Controllers/PortfolioSnapshotController.cs`
>    open in a background tab — you won't narrate it, it's just there if
>    someone asks "where would this land."

I'm going to type this into the chat, in plain language — I'm not naming any
agent by hand, the coordinator picks that up on its own:

> **Type into Copilot Chat:**
> ```
> Add an optional limit query parameter to the Load-snapshots grid endpoint,
> so callers can cap the number of records returned in one response.
> ```

While it's thinking, narrate what's actually happening rather than sitting
in silence: "Watch the first thing it does — it's not writing code. It's
classifying this as a feature request, and it's calling the planner first."
When the plan comes back, read its `Verdict` and `Scope` fields out loud —
that structured shape landing in the chat *is* the contract I described a
few minutes ago, made visible.

If there's time and it's moving well, let it continue into the developer
step so the room sees a real handoff happen. If it's running long, that's
fine — stop right after the plan and say: "From here it's the same pattern —
developer implements, reviewer checks the diff against this exact plan,
tester proves it with a real test run. I've stopped it here on purpose so we
have time for questions."

> **If it stalls or errors:** Don't troubleshoot live. Say: "This is exactly
> why the pipeline has a human escalation path — three failed iterations and
> it stops and hands me the full history instead of guessing," and move
> straight to Close.

---

## 7. Close

**20:00**

Every file you just saw is a markdown file you already know how to write.
The only genuinely new discipline is refusing to let one role do another
role's job — and once you draw that line, the rest of this follows almost
by itself. Questions?

---

## Q&A backup

You won't need to read these aloud — they're here in case the room asks
first. Answer in your own words; these are just the shape of a good answer.

**What stops the developer from just badgering the reviewer into
approving?**
It can't. The reviewer is a separate agent invocation with its own
read-only tool set and no memory of writing the code — it only ever sees
the diff and the plan, so there's nothing to argue with.

**Why not just tell one agent to "be careful" and check its own work?**
Because self-review isn't independent — same context, same blind spots.
This isn't better instructions, it's removing the edit tool from the roles
that shouldn't have it in the first place.

**Doesn't this cost more time and tokens than one prompt?**
Yes — you're paying for a plan, a review, and a test pass instead of one
shot. The trade is catching problems before merge instead of after, which
is almost always cheaper.

**Does this work outside GitHub Copilot?**
Same pattern, different file format. This repo actually ships both:
`.github/agents/*.agent.md` for Copilot and `.claude/agents/*.md` for
Claude Code.

**What happens if it just keeps failing?**
It's bounded — three iterations, then it stops and hands the full verdict
history to a human instead of looping forever.

**Can I copy these exact files into my own repo?**
The pattern, yes immediately. The files themselves are tuned to this
repo's architecture and invariants — treat them as a worked example, not a
template to paste verbatim.

---

*Sourced from `.github/agents/*.agent.md` and
[`docs/Subagent Orchestration Guide.md`](Subagent%20Orchestration%20Guide.md)
in this repository.*
