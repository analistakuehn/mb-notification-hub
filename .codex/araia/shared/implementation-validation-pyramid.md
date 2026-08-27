# Implementation Validation Pyramid

This pyramid defines cost-aware validation sensors for evidence-driven
IMPLEMENT. The pyramid separates rapid correction feedback from Task, Delivery Slice, and
SPEC completion proof.

## Principle

Run the cheapest deterministic sensor that can answer the current question.
Broader green checks never repair a missing focused oracle, and focused checks
never prove that an integrated Delivery Slice or SPEC is healthy.

## Sensor Levels

| Level | Trigger | Scope | Purpose | Promotion rule |
|---|---|---|---|---|
| `L1-fast` | After each implementation attempt | One behavior, file cluster, contract, dry run, or scenario | Give the builder rapid, actionable feedback | The owned evidence row can become `observed` |
| `L2-task` | After all rows owned by a Task are observed | Changed module plus direct dependencies | Detect local regressions and prove the Task boundary | Owned rows can become `boundary-verified`; the Task can become `candidate` |
| `L3-slice` | After every Task is a candidate and after candidate integration | Complete adapter slice affected by the Delivery Slice | Prove acceptance criteria and slice health together | The Delivery Slice can enter the `G5s` quality checkpoint (`shared/slice-quality-checkpoint.md`); only a passing checkpoint authorizes review or sealed-candidate packaging |
| `L4-spec` | At G5 after all source-contributing Delivery Slices are integrated | Every source-contributing adapter plus declared cross-adapter checks | Prove the canonical SPEC branch is releasable | IMPLEMENT can pass G5 |

An adapter declares concrete commands for every applicable level. For a level
with no meaningful executable command, use a deterministic observation or
artifact grader, not an invented test command.

## Adapter Sensor Profiles

Each adapter declares its own profile in its adaptive Task loop, next to the
loop that runs it, so the commands stay with their execution contract instead of
accumulating here:

| Adapter | Profile |
|---|---|
| dotnet | `adapters/dotnet/skills/dotnet-implementation/flows/adaptive-task-loop.md` |
| react | `adapters/react/skills/react-implementation/flows/adaptive-task-loop.md` |
| flutter | `adapters/flutter/skills/flutter-implementation/flows/adaptive-task-loop.md` |
| devops | `adapters/devops/skills/devops-implementation/flows/adaptive-task-loop.md` |

Two rules bind every profile: the default `L1-fast` sensor is never the full
suite, and a filtered check is insufficient at `L2-task` when the Task changes a
public contract, a persistence or provider boundary, generated code, or a shared
module.

## Sensor Result Contract

Persist a concise result for every executed sensor:

```json
{
  "sensor_id": "dotnet.task.module-tests",
  "level": "L2-task",
  "scope": ["src/Payments", "tests/Payments.Tests"],
  "command": "dotnet test tests/Payments.Tests",
  "cwd": ".",
  "input_fingerprint": "sha256:...",
  "started_at": "2026-07-23T12:00:00Z",
  "duration_ms": 1834,
  "exit_code": 0,
  "outcome": "pass",
  "attempt": 1,
  "cache_hit": false,
  "diagnostics_digest": "sha256:...",
  "evidence_ref": ".araia/runs/SPEC-002/IMPLEMENT/SLICE-005/evidence/L2-task-payments.json"
}
```

Valid outcomes are `pass`, `fail`, `blocked`, and `skipped`. `skipped` requires
a declared non-applicability reason and never satisfies a required level.
Store bounded diagnostics in the evidence artifact; the progress snapshot keeps
only counters and references.

## Selection and Promotion

1. Select sensors from the evidence row, adapter profile, changed paths, direct
   dependency graph, and declared risk.
2. Run all required sensors at the current level. Run independent sensors
   concurrently; keep dependency-ordered sensors sequential.
3. Promote only when every required sensor at that level passes.
4. On failure, return the smallest diagnostic delta to the active Task loop.
5. After a correction, rerun invalidated lower-level sensors before the failed
   boundary level.
6. Never mark a row `boundary-verified` from an `L1-fast` result alone.
7. Never reuse worktree-only `L3-slice` evidence after integration; rerun it on
   the canonical SPEC branch.

## Cache and Invalidation

Reuse a passing sensor only when all of these fields match:

- command and working directory;
- relevant source, test, configuration, schema, and lockfile content hashes;
- toolchain/runtime identity and material environment inputs;
- dependency scope and adapter version;
- required upstream sensor results.

Record the resulting `input_fingerprint` and `cache_hit`. Invalidate on a
matching path change, dependency expansion, configuration or lockfile change,
toolchain change, integration onto a different base SHA, or any uncertain
provenance. Prefer a safe rerun to an unverifiable cache hit.

## Failure, Flake, and Timeout Handling

- A deterministic failure is evidence: preserve its fingerprint and return its
  actionable diagnostic to the loop.
- Use one rerun to confirm suspected infrastructure noise. A pass after a
  contradictory result marks the sensor `blocked` as flaky until the oracle is
  stabilized or an approved deterministic replacement passes.
- A timeout is `fail` when the command exceeded a declared performance budget;
  otherwise it is `blocked`, never an implicit pass.
- Repeating the same failure fingerprint without new evidence activates the
  Task-loop circuit breaker.
- Do not weaken assertions, exclude failing tests, increase timeouts, or reduce
  scope solely to make a required sensor green.

## Ownership

The implementation skill owns `L1-fast` and `L2-task` execution and evidence. The
Delivery Slice worker or canonical integrator owns `L3-slice` on the integrated state. The
Araia gate runner owns `L4-spec`. Builders can propose sensors but cannot
declare required levels satisfied without recorded results.

Sensors prove that the code builds and behaves; they do not prove that it does
what the Delivery Slice asked for. That question belongs to the `G5s`
checkpoint, which runs immediately above `L3-slice` and seals the Delivery
Slice's evidence surface so a later slice cannot regress it unnoticed. A green
`L3-slice` result never substitutes for a checkpoint verdict, and a passing
checkpoint never substitutes for a required sensor.
