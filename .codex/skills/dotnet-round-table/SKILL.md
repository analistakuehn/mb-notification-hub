---
name: dotnet-round-table
description: "Runs a structured .NET technical decision round table with dotnet-architect as mediator and dotnet-engineer plus dotnet-specialist as mandatory participants. Use when credible alternatives remain contested after evidence collection or when araia:DECISION requests the .NET roster. Produces an advisory decision receipt with dissent and validation needs. Not for product decisions, unilateral approval, lifecycle state, production edits, or replacing missing evidence."
---

# .NET Round Table

## Purpose

Resolve a bounded .NET technical disagreement without creating a facilitator
persona. `dotnet-architect` mediates; `dotnet-engineer` and
`dotnet-specialist` are mandatory participants. The global `araia:DECISION`
workflow owns escalation, human approval, durable decision artifacts, and
lifecycle effects.

## Input Contract

Require one decision question, supported alternatives, immutable evidence,
constraints, accepted decisions, decision owner, and requested return shape.
Do not convene a round table merely because information is missing.

## Procedure

1. Dispatch `dotnet-architect` as mediator to normalize the question,
   alternatives, decision criteria, and disqualifying constraints without
   choosing a winner.
2. Dispatch `dotnet-engineer` and `dotnet-specialist` as independent
   participants against the normalized brief. Each returns recommendation,
   evidence, trade-offs, failure modes, validation, and objections.
3. Return both positions to the architect for bounded challenge and synthesis.
   The mediator must preserve unresolved dissent and cannot manufacture
   consensus.
4. Produce `RECOMMEND`, `NO-CONSENSUS`, or `INSUFFICIENT-EVIDENCE` with the
   leading option, rejected alternatives, consequences, validation experiment,
   dissent, and human decision needs.

## Output Contract

Return mediator opening, engineer position, specialist position, mediator
synthesis, evidence index, dissent, and decision-owner handoff. Do not edit
source, approve/publish an ADR, ask the user directly when invoked by a parent,
or mutate stage state.

## Termination

Stop after one opening, one independent position per participant, and one
mediator synthesis. Additional rounds require new evidence or an explicit
request from the owning workflow.

## Auto-Clarity

Standing obligation: preserve dissent, confidence, and missing evidence; never
equate mediation with decision authority.

1. **Safety warnings and irreversible actions**: require the owning workflow to
   surface destructive, costly, security-sensitive, or public-contract effects
   before approval.
2. **Material ambiguity**: stop when the decision question, alternatives,
   criteria, or owner is not bounded.
3. **User visibly confused or mistaken**: explain when alternatives are not
   comparable or an accepted decision already controls the outcome.
4. **Multi-step sequences with cross-dependencies**: require the normalized
   brief, both independent positions, and mediator synthesis in order.
5. **Conflict with global or project rules**: surface the conflict and return
   NO-CONSENSUS or BLOCKED rather than overriding policy.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`~/.araia/framework/shared/refusal-log-protocol.md`.
