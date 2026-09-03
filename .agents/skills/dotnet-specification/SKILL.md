---
name: dotnet-specification
description: "Produces an evidence-backed .NET technical specification contribution through dotnet-architect, dotnet-engineer, and dotnet-specialist in parallel. Use when SPECIFY or standalone technical authoring needs backend boundaries, implementation feasibility, test strategy, platform/runtime constraints, security, and NFR evidence. Not for product intent, complete lifecycle-stage authorship, approval, artifact promotion, or manifest state."
allowed-tools: Read, Write, Edit, Glob, Grep, Bash, Agent
---

# .NET Specification

## Purpose

Compose the .NET technical contribution to a specification from three
independent persona perspectives. The global product/SPECIFY workflow owns the
canonical specification, product semantics, approval, publication, and gate.

## Input Contract

Require an approved Initiative Brief or bounded technical objective, available
product requirements, brownfield evidence or greenfield assumptions, Stack
Profile when realized, language, and requested output contract.

## Required Parallel Panel

Dispatch all three personas in parallel with the same immutable evidence:

- `dotnet-architect`: boundaries, contracts, domain/integration architecture,
  security posture, NFR allocation, decisions, migration and rollback;
- `dotnet-engineer`: implementation feasibility, project dialect, test
  strategy, delivery risks, and executable validation; and
- `dotnet-specialist`: .NET/runtime/toolchain/framework compatibility,
  performance and specialty constraints, including explicit
  `NO-SPECIALTY-EVIDENCE` when evidence supports none.

Do not replace a missing persona with another. Consolidate only after all three
receipts return; disagreement stays in the decision register.

## Procedure

1. Validate the product input and classify greenfield versus brownfield.
2. For brownfield evidence, invoke `dotnet-discovery` before the panel when no
   current discovery receipt exists.
3. Run the required parallel panel and validate each bounded receipt.
4. Consolidate supported constraints, applicability, NFRs, contract surfaces,
   feasibility, validation, risks, and decision needs. Never invent product
   acceptance criteria.
5. Return a technical addendum to the owning workflow or authoring capability.

## Output Contract

Return `SPECIFICATION_CONTRIBUTION: READY | PARTIAL | BLOCKED`, the three
persona receipts, evidence index, technical requirements, applicability
register, decision register, validation obligations, and unresolved facts. Do
not approve or publish the canonical specification.

## Termination

Stop after all three receipts and the consolidated addendum validate, or return
a typed failure. A missing panel member cannot yield READY.

## Auto-Clarity

Standing obligation: keep product facts, technical facts, inferences,
recommendations, and unknowns distinct.

1. **Safety warnings and irreversible actions**: surface migrations,
   public-contract breaks, compliance impact, and costly topology decisions.
2. **Material ambiguity**: ask when missing product semantics or constraints
   change the technical contract.
3. **User visibly confused or mistaken**: explain when requested technology or
   system behavior conflicts with observed evidence.
4. **Multi-step sequences with cross-dependencies**: validate discovery when
   required, three parallel receipts, and consolidation before READY.
5. **Conflict with global or project rules**: pause when local authorship
   overrides product authority, accepted decisions, or lifecycle ownership.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`./.agents/araia/shared/refusal-log-protocol.md`.
