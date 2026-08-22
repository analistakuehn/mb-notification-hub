---
name: dotnet-system-design
description: "Designs a bounded .NET backend system, subsystem, integration, or migration with explicit constraints, alternatives, NFRs, boundaries, and validation criteria. Use for architecture decisions, technical designs, ADR inputs, integration topology, or material structural change. dotnet-architect is always accountable; consult dotnet-specialist only when evidence requires deep runtime, SDK, framework, provider, performance, or security expertise. Not for product scope, lifecycle orchestration, backlog publication, or production implementation."
---

# .NET System Design

## Purpose

Produce an evidence-backed .NET design candidate. `dotnet-architect` owns the
design reasoning and trade-offs. `$araia` or the calling workflow owns approval,
artifact placement, stage state, and any subsequent implementation.

## Input Contract

Require the design question, system boundary, functional constraints, supported
NFRs, existing Stack Profile/code evidence when brownfield, accepted decisions,
and requested artifact shape. Mark absent but material inputs as decision needs;
do not invent them.

## Procedure

1. Dispatch `dotnet-architect` as the accountable designer with the complete
   evidence envelope.
2. Inspect boundaries, contracts, consistency, data and messaging ownership,
   failure modes, security, operability, migration, rollback, and measurable
   NFRs.
3. Consult `dotnet-specialist` only when a cited package, runtime symptom, SDK
   constraint, provider mechanism, performance target, security mechanism, or
   framework-internal concern requires depth beyond ordinary architecture.
4. Have `dotnet-architect` reconcile specialist evidence without transferring
   design authority. Unsupported disagreement remains explicit.
5. Return the smallest useful design: decision, alternatives, consequences,
   interfaces, validation criteria, migration/rollback, risks, and unknowns.

## Output Contract

Return `DESIGN: READY | PARTIAL | BLOCKED`, architect receipt, optional
specialist receipt with activation evidence, assumptions, decisions required,
and validation plan. Do not write production code, approve an ADR, publish a
canonical stage artifact, or change lifecycle state.

## Termination

Stop when the bounded design is actionable and evidence-backed or when a
material decision is unavailable. A specialist consultation never substitutes
for the architect receipt.

## Auto-Clarity

Standing obligation: distinguish facts, inferences, assumptions, and
recommendations; never manufacture NFRs or architecture evidence.

1. **Safety warnings and irreversible actions**: require explicit authority
   before recommending destructive migration, public-contract break, data-store
   replacement, or distributed topology with material operational cost.
2. **Material ambiguity**: ask when competing designs depend on an unstated
   constraint that changes the recommendation.
3. **User visibly confused or mistaken**: explain when requested technology or
   topology conflicts with observed code, Stack Profile, or accepted ADRs.
4. **Multi-step sequences with cross-dependencies**: do not finalize a design
   before required evidence, specialist consultation, and validation criteria
   are complete.
5. **Conflict with global or project rules**: surface the exact conflict and
   leave the design blocked until the owning authority resolves it.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`~/.araia/framework/shared/refusal-log-protocol.md`.
