---
name: dotnet-test-driven-development
description: "Executes a bounded .NET RED-GREEN-REFACTOR cycle through dotnet-engineer using the project's observed test dialect and explicit behavior contract. Use for strict test-first implementation, a standalone TDD increment, or repairing a regression with a proven RED. Consult dotnet-specialist only for evidence-backed runtime, concurrency, framework, provider, performance, or toolchain depth. Not for general test-suite audit, lifecycle scheduling, acceptance approval, or architecture selection."
allowed-tools: Read, Write, Edit, Glob, Grep, Bash, Agent
---

# .NET Test-Driven Development

## Purpose

Own one strict test-first increment. `dotnet-engineer` is always the driver;
`dotnet-testing` supplies reusable test mechanics. `$araia` owns strategy
selection, scheduling, gates, approvals, commits, and lifecycle state.

## Input Contract

Require one observable behavior, its oracle, allowed production/test paths,
accepted decisions, Stack Profile when present, and RED/GREEN validation
commands. Missing behavior or write authority is `F-ARTIFACT`.

## Procedure

1. Dispatch `dotnet-engineer` with the TDD-driver addendum and bounded write set.
2. Invoke `dotnet-testing` as the support capability to detect the project test
   dialect and select the smallest valid test shape.
3. Write the test first and prove it fails for the intended behavioral reason.
   A compile failure, fixture failure, or unrelated failure is not RED.
4. Make the smallest production change that produces GREEN, then refactor while
   preserving behavior and rerun the affected suite.
5. Consult `dotnet-specialist` only when hard evidence points to runtime,
   concurrency, framework/provider internals, performance, SDK, compiler, or
   test-tooling depth. The specialist remains read-only and returns a brief to
   the engineer.
6. Return RED, GREEN, refactor, commands, changed paths, and residual-risk
   evidence. Never suppress a failing test or weaken a gate.

## Output Contract

Return `TDD: PASS | PARTIAL | FAIL`, the engineer receipt, optional specialist
receipt with activation evidence, RED/GREEN command evidence, changed paths,
and remaining gaps. Do not commit or update a Delivery Slice or manifest.

## Termination

Stop only after command evidence proves intended RED and the affected suite
reaches GREEN, or return a typed failure. If no authority covers a safe test
seam, stop before production edits.

## Auto-Clarity

Standing obligation: never claim RED, GREEN, coverage, or behavior exercise
without command evidence.

1. **Safety warnings and irreversible actions**: require authority before
   touching production seams, destructive fixtures, external services, or
   sensitive test data.
2. **Material ambiguity**: ask when behavior, oracle, boundary, or allowed seam
   has consequential variants.
3. **User visibly confused or mistaken**: explain when the test passes before
   implementation, fails for the wrong reason, or targets no observed project.
4. **Multi-step sequences with cross-dependencies**: enforce RED, GREEN,
   REFACTOR, and affected-suite validation in order.
5. **Conflict with global or project rules**: pause when the request weakens
   tests, analyzers, warnings, architecture, or the project's test
   dialect.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`./.agents/araia/shared/refusal-log-protocol.md`.
