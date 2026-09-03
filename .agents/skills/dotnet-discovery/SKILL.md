---
name: dotnet-discovery
description: "Discovers an existing .NET system's architecture, boundaries, dependencies, contracts, runtime constraints, risks, and Stack Profile evidence. Use for brownfield intake, adoption, modernization discovery, or design preparation. dotnet-architect is always accountable; use dotnet-solution-inspection for mechanical inventory and consult dotnet-specialist only when evidence requires specialty depth. Not for product discovery, lifecycle orchestration, requirements approval, implementation, or quality verdicts."
allowed-tools: Read, Write, Edit, Glob, Grep, Bash, Agent
---

# .NET Discovery

## Purpose

Turn brownfield .NET evidence into an architectural discovery receipt.
`dotnet-architect` owns interpretation; `dotnet-solution-inspection` owns the
mechanical project and Stack Profile inventory.

## Input Contract

Require a project root containing `.sln`, `.slnx`, or `.csproj`, a bounded
discovery question or scope, language, and optional output location. Route a
product problem statement alone to the global product workflow.

## Procedure

1. Invoke `dotnet-solution-inspection` to collect the project graph, packages,
   build/test dialect, Stack Profile axes, and cited contradictions.
2. Dispatch `dotnet-architect` with that immutable evidence to identify system
   boundaries, contracts, dependency direction, data/messaging ownership,
   architecture risks, NFR evidence, and decision needs.
3. Consult `dotnet-specialist` only for an evidenced specialty: runtime,
   compiler/SDK, framework/provider internals, concurrency, performance,
   security mechanism, or a load-on-demand specialty pack.
4. Reconcile facts, inferences, and unknowns. Do not turn absence of evidence
   into an architectural conclusion.
5. Return the Stack Profile reference, system map, risks, modernization seams,
   contradictions, and recommended next capability.

## Output Contract

Return `DISCOVERY: PASS | PARTIAL | FAIL`, solution-inspection receipt,
architect receipt, optional specialist receipt with activation evidence, and
decisions required. Do not author product requirements, select lifecycle work,
approve architecture, edit source, or advance a stage.

## Termination

Stop when cited evidence answers the bounded discovery questions, or return
`F-ARTIFACT`/`INSUFFICIENT-EVIDENCE` without inventing a system model.

## Auto-Clarity

Standing obligation: keep observed facts separate from architectural
inference; durable unknowns follow the framework unknown-information policy.

1. **Safety warnings and irreversible actions**: confirm before commands that
   restore/build, contact private feeds, or expose sensitive configuration.
2. **Material ambiguity**: ask when project root, solution, scope, or output
   target has consequential variants.
3. **User visibly confused or mistaken**: explain when no .NET system or the
   claimed component exists in the bounded scope.
4. **Multi-step sequences with cross-dependencies**: validate mechanical
   inventory before architectural interpretation and specialist activation.
5. **Conflict with global or project rules**: pause when discovery commands,
   evidence retention, or output paths violate policy.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`./.agents/araia/shared/refusal-log-protocol.md`.
