---
name: dotnet-runtime-diagnostics
description: "Diagnoses evidence-backed .NET runtime, compiler, SDK, MSBuild, NuGet, GC, JIT, async/threading, allocation, dump, trace, counter, or framework-internals problems. Use when builds or runtime behavior require platform depth beyond ordinary source debugging. Not for generic code review, speculative optimization, feature implementation, or production profiling without explicit authority."
---

# .NET Runtime Diagnostics

## Purpose

Collect and interpret platform/runtime evidence, then return a bounded fix
brief. Source remediation is handed to `dotnet-engineer` unless the parent
explicitly invokes `dotnet-implementation` afterward.

## Input Contract

Require a concrete symptom, environment, affected project or process, allowed
commands, sensitivity boundary, and competing hypotheses when known. Do not
accept an unbounded production-profiling request.

## Procedure

1. Require a concrete symptom, environment, affected project/process, allowed
   commands, and sensitivity boundary.
2. Dispatch `dotnet-specialist` with the minimal context. State competing
   hypotheses and the evidence that distinguishes them.
   Load `references/quality-assessment-scope.md` for performance evidence and
   `references/benchmarkdotnet-standards.md` only when a benchmark is required.
3. Prefer non-invasive build logs, binlogs, counters, traces, dumps, and focused
   reproductions. Never attach to or stress production without explicit
   authority; treat diagnostic artifacts as sensitive.
4. Correlate runtime evidence with source/configuration and produce a fix brief
   plus a validation experiment. Use `references/output-templates.md` only for
   a durable diagnostic report. An inconclusive diagnosis remains inconclusive.

## Output Contract

Return `CONFIRMED`, `INCONCLUSIVE`, or `REFUTED`, evidence paths and commands,
confidence, a bounded fix brief, validation experiment, and residual
uncertainty.

## Termination

Stop after the hypothesis is confirmed, refuted, or bounded as inconclusive.
Do not change lifecycle state, edit source, or emit a quality verdict.

## Auto-Clarity

Standing obligation: surface low-confidence claims, evidence gaps, and
contract uncertainty inline; an unsupported diagnosis stays inconclusive.

1. **Safety warnings and irreversible actions**: require explicit authority
   before attaching, dumping, tracing, profiling, stressing, or exposing data
   from a production or sensitive process.
2. **Material ambiguity**: ask when symptom, environment, process, command
   grant, sensitivity boundary, or diagnostic hypothesis is consequentially
   unclear.
3. **User visibly confused or mistaken**: explain when the process/project is
   absent, a tool is unavailable, or the supplied evidence refutes the premise.
4. **Multi-step sequences with cross-dependencies**: validate command output,
   artifact integrity, source correlation, and the distinguishing experiment
   before confirming a mechanism.
5. **Conflict with global or project rules**: pause when diagnostic collection,
   artifact retention, process access, or proposed remediation violates
   security, privacy, operational, or project policy.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`./.codex/araia/shared/refusal-log-protocol.md`.
