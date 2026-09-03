---
name: dotnet-solution-inspection
description: "Mechanically inspects an existing .NET solution and emits an evidence-backed Stack Profile, project/dependency map, risk signals, and optional bounded reports. Use from dotnet-discovery, brownfield intake, adoption, project inventory, or Stack Profile refresh. Not for architectural interpretation, product discovery, lifecycle orchestration, requirements, implementation, architecture decisions, or EQI verdicts."
allowed-tools: Read, Write, Edit, Glob, Grep, Bash, Agent
---

# .NET Solution Inspection

## Purpose

Inspect an existing .NET project and return reusable evidence. Do not own a
pipeline stage, acceptance semantics, canonical requirements, planning, code
changes, approval, or a quality gate.

## Input Contract

Accept a solution/project path, optional output directory, language, scope, and
`--refresh-profile`. Require at least one `.sln`, `.slnx`, or `.csproj` for a
brownfield analysis.

## Procedure

1. Discover solutions, projects, target frameworks, SDK pin, build props,
   packages, project references, source roots, tests, transports, persistence,
   messaging, caching, authentication, telemetry, and CI/build commands.
2. Read
   `./.agents/araia/adapters/dotnet/references/stack-profile-protocol.md`
   and `references/stack-profile-detection.md`. Do not infer a library from a
   keyword when package or source evidence is absent.
3. Use `dotnet-architect` for backend structure and `dotnet-engineer` for
   implementation/test dialect only when their independent context is useful.
   Activate `dotnet-specialist` only for an evidenced runtime/toolchain issue.
4. If caller requests it, write `.araia/stack-profile.yaml` atomically. Preserve
   unknown axes as `unknown` in the transient receipt; omit unsupported durable
   claims according to `shared/unknown-information-policy.md`.
5. When caller provides an output directory, write a compact evidence report with
   `file:line` or command references. Otherwise return the receipt inline.

## Output Contract

Return detected profile axes, project/dependency map, evidence locations,
risks, contradictions, and unresolved facts. End with `PASS`, `PARTIAL`, or
`FAIL`; a missing codebase is `F-ARTIFACT`.

## Termination

Stop after validating the requested profile/report and leaving every project
file unchanged. Do not call a stage facade from this capability.

## Auto-Clarity

Standing obligation: surface low-confidence claims, evidence gaps, and
contract uncertainty inline; durable reports follow
`./.agents/araia/shared/unknown-information-policy.md`.

1. **Safety warnings and irreversible actions**: confirm before overwriting a
   Stack Profile/report or running a command that can restore, build, or expose
   sensitive configuration.
2. **Material ambiguity**: ask when project root, solution selection, output
   scope, or refresh intent has multiple consequential interpretations.
3. **User visibly confused or mistaken**: explain observed state when no .NET
   project exists, the project is greenfield, or evidence does not support the
   requested technology.
4. **Multi-step sequences with cross-dependencies**: if discovery selects a
   solution but profile detection cannot cite its `.csproj`/package evidence,
   stop before writing `.araia/stack-profile.yaml`; validate every cited path
   before returning the report.
5. **Conflict with global or project rules**: pause when analysis commands,
   durable unknowns, output paths, or evidence handling conflict with project
   policy.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`./.agents/araia/shared/refusal-log-protocol.md`.
