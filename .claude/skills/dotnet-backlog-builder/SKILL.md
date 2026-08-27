---
name: dotnet-backlog-builder
description: "Builds an implementation-ready .NET technical backlog candidate from accepted requirements, design decisions, and verification evidence. Use to decompose backend work into bounded tasks, dependencies, ownership, validation, and scaffold versus implementation routes. dotnet-engineer is always accountable; consult dotnet-specialist only for evidence-backed specialty work. Not for owning araia:PLAN, prioritizing product scope, publishing Delivery Slices, approvals, estimates, or lifecycle state."
allowed-tools: Read, Write, Edit, Glob, Grep, Bash, Agent
---

# .NET Backlog Builder

## Purpose

Produce the adapter's technical decomposition candidate without becoming a
PLAN-stage facade. `dotnet-engineer` owns implementation feasibility and task
shape; the global workflow owns product sequencing, Delivery Slice IDs, DAG
publication, approval, and manifest state.

## Input Contract

Require accepted requirements, architecture/design decisions, Verification
Plan evidence, Stack Profile when realized, repository constraints, and the
target outcome. Missing architecture that changes task boundaries is a decision
need, not an invitation to invent it. Read
`./.claude/araia/shared/implementation-evidence-contract.md`; the candidate
must supply enough evidence mapping for global PLAN to author Evidence Contract
v2 rows.

## Procedure

1. Dispatch `dotnet-engineer` as the accountable backlog builder.
2. Decompose by coherent outcomes and executable validation, not by generic
   layers or persona phases. Identify affected project surfaces, dependency
   edges, foundation/scaffold need, migrations, tests, and rollout risks.
3. Consult `dotnet-specialist` only when a task requires evidenced runtime,
   framework/provider, toolchain, performance, security, or specialty-pack
   depth. Record the evidence that activated the consultation.
4. Map each task candidate to requirement/design inputs and proposed Evidence
   Contract v2 fields: observable outcome, oracle, strategy, fast check,
   boundary check, risk, and evidence. Keep RED/GREEN/REFACTOR as execution
   strategy, not backlog items.
5. Return candidates and constraints to the global PLAN workflow; do not assign
   final IDs, priority, wave, or approval status locally.

## Output Contract

Return `BACKLOG_CANDIDATE: READY | PARTIAL | BLOCKED`, engineer receipt,
optional specialist receipt, task candidates with outcome/dependencies/owner
capability/validation, foundation route, risks, and decisions required.

## Termination

Stop when the technical candidate is complete enough for global PLAN
composition or when missing accepted decisions make safe decomposition
impossible.

## Auto-Clarity

Standing obligation: surface unsupported file predictions, dependencies, and
validation assumptions; do not present a candidate as an approved backlog.

1. **Safety warnings and irreversible actions**: flag destructive migrations,
   external effects, and rollback-sensitive tasks before decomposition.
2. **Material ambiguity**: ask when accepted behavior or architecture yields
   materially different task boundaries.
3. **User visibly confused or mistaken**: explain when requested modules,
   projects, or dependencies are absent from evidence.
4. **Multi-step sequences with cross-dependencies**: validate inputs,
   decomposition, dependency edges, and verification mappings before READY.
5. **Conflict with global or project rules**: pause when the requested backlog
   bypasses PLAN ownership, accepted decisions, or repository boundaries.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`./.claude/araia/shared/refusal-log-protocol.md`.
