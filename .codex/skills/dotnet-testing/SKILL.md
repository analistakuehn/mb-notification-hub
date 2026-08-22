---
name: dotnet-testing
description: "Supplies reusable .NET test design, implementation, repair, dialect detection, and evidence mechanics. Use from dotnet-test-driven-development, dotnet-implementation, IMPLEMENT/VERIFY, or standalone characterization, regression, integration, contract, architecture, and coverage work. Not for owning a complete RED-GREEN-REFACTOR workflow, lifecycle scheduling, global quality scoring, production architecture decisions, or lowering test gates."
---

# .NET Testing

## Purpose

Own test mechanics and test code for a bounded behavior. Strict
RED-GREEN-REFACTOR orchestration belongs to `dotnet-test-driven-development`.
Do not own IMPLEMENT or VERIFY lifecycle, EQI aggregation, acceptance approval,
or commits.

## Input Contract

Require a bounded behavior, target project or test set, allowed production
seams, project test dialect, risk level, and validation commands. Accept
test-first, characterization, regression, integration, contract, architecture,
or repair modes.

## Procedure

1. Detect xUnit/NUnit/MSTest, assertions, mocks, integration host, containers,
   and naming conventions from project files and representative tests.
2. Select strict test-first, characterization, regression, integration,
   contract, architecture, or performance-adjacent tests from the risk and
   evidence contract. Do not force TDD on legacy behavior without a safe seam.
3. Use `dotnet-engineer` with a test-driver addendum when an isolated context is
   useful. Keep production edits limited to the smallest testability seam the
   user or parent workflow authorized.
4. Prove RED when the request requires test-first, then GREEN, then rerun the
   affected suite. Record commands and exit results; never claim coverage
   without a measured report.
5. Load `references/existing-code-strategy.md`,
   `references/greenfield-strategy.md`, `references/test-patterns.md`,
   `references/special-scenarios.md`, or
   `references/test-project-template.md` only when the selected mode needs that
   guidance.

## Output Contract

Return tests changed, behavior covered, RED/GREEN evidence when applicable,
commands and exit results, remaining gaps, and authorized production seams
touched.

## Termination

Stop after the bounded test objective validates or returns a typed failure.
Never suppress or delete a failing test merely to pass.

## Auto-Clarity

Standing obligation: surface low-confidence claims, evidence gaps, and
contract uncertainty inline; never claim coverage or GREEN without evidence.

1. **Safety warnings and irreversible actions**: confirm before changing
   production seams, deleting tests/data, using external services, or running
   destructive integration fixtures.
2. **Material ambiguity**: ask when target behavior, test framework, oracle,
   risk level, or authorized production seam has consequential variants.
3. **User visibly confused or mistaken**: explain when no matching tests or
   project exist, the workflow cannot reproduce RED, or reported coverage is
   absent.
4. **Multi-step sequences with cross-dependencies**: if RED exits successfully,
   fails for the wrong reason, or the implementation handoff changes the test
   oracle, stop; prove the intended RED before GREEN and rerun the affected
   suite before returning.
5. **Conflict with global or project rules**: pause when fulfilling the
   request weakens gates, suppresses failures, replaces the project test
   dialect, or exceeds the allowed write set.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`~/.araia/framework/shared/refusal-log-protocol.md`.
