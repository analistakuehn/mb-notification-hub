---
name: dotnet-implementation
description: "Implements one bounded .NET backend change or Delivery Slice through dotnet-engineer using the project's Stack Profile, accepted decisions, allowed write set, and declared validation commands. Use as the araia:IMPLEMENT execute contribution or for standalone feature/refactor work. Consult dotnet-specialist only for evidence-backed specialty depth. Not for scheduling, collaboration policy, approvals, commits, lifecycle state, requirements, planning, architecture selection, or EQI scoring."
allowed-tools: Read, Write, Edit, Glob, Grep, Bash, Agent
---

# .NET Implementation

## Purpose

Own technology-specific source changes and local validation for one bounded
unit. `dotnet-engineer` is the accountable implementer. `$araia` owns scheduling, collaboration mode, checkpoints, gates,
integration, commit, and manifest state.

## Input Contract

Require an implementation goal or `--slice-file`, `--mode <solo|pair|mob>`, allowed write paths, accepted
decisions, Stack Profile when present, validation commands, implementation
strategy, and approval policy. A missing or contradictory scope is
`F-ARTIFACT`; no scope expansion from conversation history.

The parent can pass `--implementation-strategy <adaptive\|strict-tdd>`,
`--approval-policy <auto-recommended|manual>`, and `--candidate-only`. Load
`./.claude/araia/shared/implementation-evidence-contract.md` and
`./.claude/araia/shared/implementation-strategy-selection.md`. For
`--candidate-only`, also load
`./.claude/araia/shared/implementation-candidate-mode.md`,
`./.claude/araia/shared/implementation-approval-policy.md`, and
`./.claude/araia/shared/progress-tracking.md`. The parent
owns strategy and approval selection; a missing Evidence Contract is
`F-ARTIFACT`.

## Adaptive Task Loop

For `adaptive`, the framework default, reuse the transition flow documented at
`./.claude/araia/adapters/dotnet/skills/dotnet-implementation/flows/adaptive-task-loop.md`: preserve the
builder context through implementation, run `L1-fast` and required `L2-task`
sensors, apply `shared/test-tautology-rules.md` to every oracle the attempt
introduces, emit compact attempt deltas, and stop after two consecutive attempts
with the same fingerprint. For `strict-tdd`, selected only by a citable accepted
constraint or an explicit flag, invoke `dotnet-test-driven-development` and
require its RED/GREEN evidence before final validation. In either case, the
continuous builder produces no commits; return only boundary-verified evidence to
the parent.

## Procedure

1. Read `./.claude/araia/adapters/dotnet/code-style.md`,
   `./.claude/araia/shared/no-spec-refs-in-implementation.md`, the Stack
   Profile, ADRs governing changed paths, and a representative in-project slice.
2. Dispatch `dotnet-engineer` with the implementer addendum and complete bounded
   write set. Keep implementation ownership with `dotnet-engineer`; no inline
   implementation or transfer to another persona.
3. Resolve conditional capability needs before writes. Load
   `./.claude/araia/adapters/dotnet/references/specialties/data.md` plus
   `postgresql.md` or `mongo.md` for evidenced persistence/cache bindings;
   load `kafka.md`, `rabbitmq.md`, or `graphql.md` for their evidenced
   integrations. Use `dotnet-testing` for general test mechanics,
   `dotnet-test-driven-development` for strict test-first work, and
   `dotnet-runtime-diagnostics` for evidenced runtime/toolchain failures.
   Consult `dotnet-specialist` only when file, package, Stack Profile, command,
   trace, NFR, or failure evidence activates runtime, provider, framework,
   performance, security, or toolchain depth. The specialist remains read-only
   and returns a brief to the engineer.
4. Execute the smallest vertical increment. If an unresolved backend decision
   changes the code shape, stop and route it to `dotnet-system-design` or
   `dotnet-round-table`; keep decision ownership outside implementation.
5. Run narrow tests, then the declared build/test commands. Keep warnings,
   analyzers, tests, and project policies intact.
6. Return a receipt with the engineer identity, changed paths, commands and exit results, evidence,
   conditional capabilities used, residual risks, and decisions required.

## Output Contract

Return an uncommitted candidate with the required `dotnet-engineer` receipt,
changed paths, commands and exit results, evidence, conditional capabilities
and specialist consultations used, residual risks, and decisions required, or
return a typed failure without partial promotion.

## Termination

End with a validated uncommitted candidate or a typed failure. Leave Delivery
Slice and manifest state unchanged; emit no gate verdict.

## Auto-Clarity

Standing obligation: surface low-confidence claims, evidence gaps, contract
uncertainty, and unverified implementation claims inline.

1. **Safety warnings and irreversible actions**: require explicit authority
   for destructive migrations, external effects, secret handling, or writes
   outside the granted set.
2. **Material ambiguity**: ask when two candidate slice files, API behaviors,
   accepted ADR interpretations, or allowed write sets produce different
   code or migration effects.
3. **User visibly confused or mistaken**: explain observed state when the
   requested project/symbol is absent, the slice is already complete, or the
   proposed change contradicts the codebase.
4. **Multi-step sequences with cross-dependencies**: validate capability
   activation, edits, narrow tests, build, and final evidence in order; accept
   only a complete, well-formed receipt.
5. **Conflict with global or project rules**: pause when requested code,
   packages, analyzers, warnings, tests, architecture, or source references
   violate accepted project policy.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`./.claude/araia/shared/refusal-log-protocol.md`.
