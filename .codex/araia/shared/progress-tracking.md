# Progress Tracking Protocol (Shared)

How skills use **TodoWrite** for real-time progress visibility during multi-agent orchestration.

## Principle

When dispatching 5+ agents in parallel, the user has no visibility without explicit tracking. TodoWrite serves as a **live dashboard** showing completed, running, and overall status.

For Araia IMPLEMENT, visibility is also a traceability contract: every
user-facing feedback names its SPEC, Delivery Slice, and Task context and reports exact
SPEC/Delivery Slice progress. This applies even when no agent fan-out occurs.

For portfolio-wide NEXT, use one bounded dispatch step plus a compact per-SPEC
table with SPEC, assigned stage, worker state, gate/checkpoint, and next action.
Do not create one Todo item per SPEC when the batch exceeds 15; group the
dashboard by `queued`, `running`, `awaiting-approval`, `completed`, and
`blocked/failed`, while keeping every decision-bearing row individually
identifiable. The durable source of truth remains
`.araia/runs/portfolio-next/{RUN-ID}/scheduler.json` and `events.jsonl`.

## Araia IMPLEMENT Feedback Envelope

Every user-facing IMPLEMENT feedback, including initial plan, routine checkpoint,
auto-approval, evidence transition, worker event, integration result, failure,
resume, and completion, starts with this compact envelope:

```text
## IMPLEMENT Feedback

| Context | Value |
|---|---|
| SPEC | SPEC-002; IMPLEMENT; stages 3/6; Delivery Slices 4/12 |
| Delivery Slice | SLICE-005: Validate beneficiary; tasks 1/3 |
| Task | Task 2: Enforce beneficiary policy; correcting |
| Checkpoint | task-correction; auto-approved (auto-recommended) |
```

Rules:

1. `SPEC` is always present. Report exact completed/total stage and Delivery Slice counts;
   never invent a weighted percentage.
2. `Delivery Slice` is always present. For a SPEC-level scheduler event use
   `-- (SPEC-level scheduler)`; for a Delivery Slice event include ID, title, and exact
   completed/total Task counts.
3. `Task` is always present. Parse each `### Task N` heading and its title from the Delivery Slice. For a
   SLICE-wide action use `-- (SLICE-level orchestration)`. When an acceptance
   criterion cannot be mapped deterministically to one declared Task, use
   `-- (unmapped or cross-task)` and name the criterion separately; never guess.
4. Adaptive feedback adds the Task state (`planned`, `implementing`,
   `verifying`, `correcting`, `candidate`, or `blocked`). Strict-TDD feedback
   adds the current phase (`RED`, `GREEN`, `REFACTOR`, or `DOCUMENT`).
   Orchestration actions name their operation (`locks`, `integration`, `stage`,
   `commit`, `gate`, or `recovery`).
5. `Checkpoint` records `pending`, `manual`, `auto-approved`, or `blocked`, plus
   the effective approval policy. An auto-approval is shown after its durable
   audit event is written, never hidden.
6. A batched parallel dashboard may summarize several Delivery Slices, but every row that
   needs a decision still carries its own Delivery Slice and Task context.

## Durable IMPLEMENT Progress

Write the latest Task/evidence snapshot atomically after every material
transition:

- sequential Delivery Slice: `.araia/runs/{SPEC-ID}/IMPLEMENT/{SLICE-ID}/progress.json`;
- parallel Delivery Slice: `.araia/runs/{SPEC-ID}/IMPLEMENT-parallel/workers/{SLICE-ID}/progress.json`.

Minimum snapshot:

```json
{
  "schema_version": 2,
  "updated_at": "2026-07-15T12:00:00Z",
  "spec": {"id": "SPEC-002", "completed_stages": 3, "total_stages": 6, "completed_slices": 4, "total_slices": 12},
  "slice": {"id": "SLICE-005", "title": "Validate beneficiary", "status": "in_progress", "completed_tasks": 1, "total_tasks": 3},
  "implementation_strategy": "adaptive",
  "task": {"id": "Task 2", "title": "Enforce beneficiary policy", "status": "in_progress", "task_state": "correcting", "attempt": 2},
  "tasks": [{"id": "Task 1", "title": "Define policy contract", "status": "completed"}],
  "evidence": {"pending": 0, "producing": 1, "observed": 1, "boundary_verified": 0, "blocked": 0, "total": 2},
  "loop_health": {"files_touched": 4, "output_tokens": 18240, "attempts": 2},
  "evaluation": {"required": true, "triggers": ["public-contract"], "status": "pending", "confirmed": 0, "refuted": 0, "uncertain": 0},
  "approval": {"policy": "auto-recommended", "last_checkpoint": "task-correction", "decision": "auto-approved"}
}
```

For `strict-tdd`, replace `task_state` and `attempt` with `tdd_phase`. Readers
and writers require schema version 2 and always persist
`implementation_strategy`.

When the risk protocol requires independent evaluation, persist its trigger
set, status, and verdict counters. `pending`, `refuted`, or high/critical
`uncertain` evaluation prevents candidate completion.

`loop_health` carries the counters that `shared/refactoring-triggers.md`
consumes: distinct files written during the attempt (excluding generated output
and lockfiles), model output tokens attributed to the attempt, and the attempt
count. A harness that cannot report output tokens writes `null`, which disables
that trigger instead of fabricating a trend.

The active Delivery Slice executor owns Task/evidence updates; adapter
implementation skills additionally own Task/evidence transitions. The parent scheduler owns
SPEC/Delivery Slice, integration, and commit updates. `status` reads these
snapshots plus the manifest and scheduler state. A missing snapshot is reported
as unavailable, not inferred from git diffs, transcript text, file mtimes, or
unchecked Delivery Slice boxes.

Task statuses are `pending`, `in_progress`, `completed`, and `blocked`.
Adaptive Task states are `planned`, `implementing`, `verifying`, `correcting`,
`candidate`, and `blocked`. A Task is completed only after all mapped evidence
is `boundary-verified`. Cross-task work may advance a strict-TDD phase without
incrementing a Task count until the mapping is proven.

## When to Use

- Workflows or capabilities dispatching **3+ agents** (VERIFY, Code Analyzer, REFINE)
- Implementation skills with **multi-step evidence cycles**
- Global workflows with **sequential artifact generation** (SPECIFY)

## Setup Phase

Before dispatch, create a TodoWrite list with one entry per major work unit.

### Analysis Workflows and Capabilities (VERIFY, Code Analyzer, REFINE)

```
TodoWrite([
  { content: "Dispatch Agent 1: {agent_name} -- {dimension}", status: "pending", activeForm: "Dispatching {agent_name}" },
  { content: "Dispatch Agent 2: {agent_name} -- {dimension}", status: "pending", activeForm: "Dispatching {agent_name}" },
  ...
  { content: "Consolidate agent reports", status: "pending", activeForm: "Consolidating reports" },
  { content: "Present results for user approval", status: "pending", activeForm: "Presenting results" },
  { content: "Publish final files", status: "pending", activeForm: "Publishing files" }
])
```

### Implementation Skill (Evidence Cycles)

```
TodoWrite([
  { content: "Load Delivery Slice context and verify dependencies", status: "pending", activeForm: "Loading Delivery Slice context" },
  { content: "Mob Discussion: gather agent perspectives", status: "pending", activeForm: "Running mob discussion" },
  { content: "Criterion 1: {criterion_text_abbreviated}", status: "pending", activeForm: "Implementing criterion 1" },
  ...
  { content: "Post-implementation verification", status: "pending", activeForm: "Running final verification" }
])
```

### Global SPECIFY Workflow (Lean Core + Conditionals)

```
TodoWrite([
  { content: "Core: Generate Development Specification", status: "pending", activeForm: "Generating Development Specification" },
  { content: "Conditionals: Generate triggered artifacts ({N})", status: "pending", activeForm: "Generating triggered artifacts" },
  { content: "Core: Generate Implementation Map", status: "pending", activeForm: "Generating Implementation Map" },
  { content: "Core: Generate Verification Plan", status: "pending", activeForm: "Generating Verification Plan" },
  ...
  { content: "Review pass: consistency check", status: "pending", activeForm: "Running consistency review" },
  { content: "Present results for user approval", status: "pending", activeForm: "Presenting results" }
])
```

## During Execution

### Mark In-Progress

```
{ content: "...", status: "in_progress", activeForm: "Running {agent_name} analysis" }
```

**Rule**: only ONE task `in_progress` at a time. For parallel dispatches, mark the **dispatch step** as in_progress, not individual agents.

### Mark Completed with Summary

```
{ content: "Agent 1: dotnet-architect -- Architecture (10 criteria, 1 blocker)", status: "completed", activeForm: "..." }
```

### Handle Failures

Retrying:
```
{ content: "Agent 3: dotnet-specialist -- Performance role (RETRYING: empty output)", status: "in_progress", activeForm: "Retrying performance analysis" }
```

Skipped after exhausted retries:
```
{ content: "Agent 3: dotnet-specialist -- Performance role (SKIPPED: 3 failures)", status: "completed", activeForm: "..." }
```

## Parallel Dispatch Pattern

For parallel agents with `run_in_background: true`:

1. **Before dispatch**: create all pending entries
2. **At dispatch**: mark single "Dispatching N agents in parallel" as in_progress
3. **As each returns**: update that entry to completed with summary
4. **After all return**: mark dispatch step completed, move to consolidation

Example flow:
```
Step 1: [completed] Codebase Discovery (12 source projects, .NET 9)
Step 2: [in_progress] Dispatching 5 agents in parallel
  - Agent 1: dotnet-architect [completed] (10 criteria, 1 blocker)
  - Agent 2: dotnet-specialist [completed] (9 criteria, 0 blockers)
  - Agent 3: dotnet-specialist / performance-evidence [in_progress]
  - Agent 4: dotnet-engineer / quality-reviewer [pending]
  - Agent 5: dotnet-engineer / test-strategist [pending]
Step 3: [pending] Consolidate reports
Step 4: [pending] User approval
Step 5: [pending] Publish files
```

## Dispatch Liveness (no transcript spying)

The ONLY liveness signals for a dispatched agent are the harness completion notification and the per-unit returns feeding the TodoWrite dashboard. Never poll the filesystem for a subagent's transcript or output file (no `ls`, `tail`, `Get-Item`, or size checks on it) to infer that the agent is alive or progressing: it wastes turns, tempts a transcript read that floods the caller's context, and measures nothing the contract guarantees (a growing transcript proves tokens, not progress).

When a dispatch runs long enough that anyone feels the need for a liveness check, the dispatch is too big. Bound it (below) instead of watching it.

The legitimate stall check is a deadline, not surveillance: when a chunk has no return past ~10x the actual duration of a comparable completed sibling (or ~10x its declared budget), treat it as stalled and recover per `retry-protocol.md` "Stalled Dispatch" (kill, split into parallel chunks, re-dispatch with an evidence budget).

## Bounded Batch Dispatches

A batch over N units (files, ADRs, articles, findings) is dispatched as one agent per unit, or per small chunk (at most 5 units), in parallel; the caller aggregates the per-unit reports. Progress then IS the stream of returns in the dashboard: no unit-level opacity, failure isolation per chunk, and a retry costs one chunk instead of the whole batch.

A single agent that processes the entire batch and only reports at the end is an anti-pattern, worst in read-only agents (Tier 3): they cannot write partial results, so the run produces zero observable progress by design and invites exactly the transcript-spying this protocol forbids.

## Anti-Patterns

1. **Never create a TodoWrite list with more than 15 items**: group sub-steps.
2. **Never forget to mark completion**: update status immediately after each step.
3. **Never leave stale in_progress items**: complete or update before starting new.
4. **Never use TodoWrite for trivial skills** (e.g., single-agent global PLAN workflow): adds noise without value.
5. **Never infer liveness from a subagent's transcript on disk**: completion notifications and per-unit returns are the only signals.
6. **Never hand an unbounded batch to one agent**: fan out per unit or small chunk so progress is observable through returns.
