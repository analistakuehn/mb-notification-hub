---
name: dotnet-code-review
description: "Runs an evidence-backed, read-only review of a bounded .NET diff or file set through dotnet-architect, dotnet-engineer, and dotnet-specialist in parallel. Use for direct code review, the Araia review command, or the full-EQI .NET source sensor. Every reviewer applies all six lenses: Performance, Software Engineering, .NET Quality, Test, Architecture, and Security. Not for applying fixes, targeted EQI remediation rechecks, lifecycle verdicts, or PR provider effects."
---

# .NET Code Review

## Purpose

Produce independent, evidence-backed .NET findings through the adapter's three
personas. The skill owns reviewer composition and finding consolidation;
`$araia` owns triage, approval, provider effects, EQI aggregation, correction
work, and lifecycle state.

## Input Contract

Require an immutable diff or bounded file set, source revision, Stack Profile
when present, accepted decisions, severity threshold, and available validation
evidence. An empty or mutable scope is `F-ARTIFACT`.

## Six-Lens Contract

Read `references/review-lenses.md`, which specializes
`./.codex/araia/shared/source-review-lenses.md` for .NET. Dispatch exactly
one fresh, read-only context for each of `dotnet-architect`, `dotnet-engineer`,
and `dotnet-specialist` in parallel. Give all three the same immutable evidence
envelope. Every reviewer must evaluate all six lenses; primary emphases
partition accountability, not visibility.

Do not degrade to one or two reviewers. If any required persona cannot run,
return `F-CAPABILITY` and identify the missing review receipt. Do not let one
reviewer see another's findings before all three independent receipts return.

## Procedure

1. Resolve the exact diff/fileset and source revision. Read the Stack Profile,
   accepted ADRs, `code-style.md`, and representative affected code.
2. Dispatch the three reviewers concurrently with read-only authority.
3. Validate that every receipt reports all six lenses, including an explicit
   `NO-FINDING` for a lens with no supported finding.
4. Consolidate only after all receipts complete. Deduplicate by root cause and
   location, retain the highest supported severity, and preserve dissent.
5. Return typed findings with `id` (`{LENS-CODE}-{NNN}` per the shared scheme),
   `severity`, `confidence`, `lens`, `file`, `line`, `evidence`,
   `evidence-kind`, `introduced-by-diff`, `impact`, `recommendation`,
   `verification`, `source-revision`, and `reviewers`.

## Output Contract

Return the three persona receipts, a consolidated finding set, lens coverage,
and one of `CLEAN`, `FINDINGS`, or `INCOMPLETE`. Findings are advisory evidence;
the skill does not calculate EQI, approve a gate, edit source, post comments,
resolve threads, or invoke a fix.

An exact-criterion remediation recheck is not a code-review run. The global
workflow dispatches only the recorded criterion owner and carries the remaining
baseline forward.

## Termination

Stop after all three independent receipts and the consolidation validate, or
return a typed failure without a partial review verdict.

## Auto-Clarity

Standing obligation: surface low-confidence claims, missing review lenses, and
evidence gaps inline; never convert an opinion into a finding.

1. **Safety warnings and irreversible actions**: remain read-only; refuse source
   edits, provider comments, thread resolution, or approval effects.
2. **Material ambiguity**: ask when diff scope, source revision, accepted
   decision, or security boundary has consequential variants.
3. **User visibly confused or mistaken**: explain when the scope is empty, not
   .NET, stale, or does not contain the claimed change.
4. **Multi-step sequences with cross-dependencies**: validate scope, three
   parallel receipts, lens completeness, deduplication, and consolidated output
   in that order; never return success with a missing persona.
5. **Conflict with global or project rules**: pause when a requested severity,
   omission, or recommendation conflicts with current evidence or accepted
   policy.

When a trigger blocks or asks, record it in `.araia/refusal-log.jsonl` per
`./.codex/araia/shared/refusal-log-protocol.md`.
