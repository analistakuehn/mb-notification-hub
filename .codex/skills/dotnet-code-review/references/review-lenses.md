# .NET Six-Lens Review Model

Specializes `./.codex/araia/shared/source-review-lenses.md` for .NET. The
lens set, the codes, and the finding id scheme come from that file and are not
redefined here. Every reviewer evaluates every lens; the primary assignment
identifies who must provide the deepest challenge and cannot return a
superficial pass.

| Code | Lens | Required evidence | Primary emphasis |
|---|---|---|---|
| `PRF` | Performance | hot paths, allocations, I/O, concurrency, latency, or measured runtime evidence | `dotnet-specialist` |
| `ENG` | Software Engineering | correctness, cohesion, coupling, maintainability, error handling, observability, and change scope | `dotnet-engineer` |
| `STK` | .NET Quality | C# idioms, nullable flow, async semantics, framework/API use, analyzers, SDK/package compatibility | `dotnet-specialist` |
| `TST` | Test | behavior coverage, oracle strength, isolation, failure paths, regression risk, and test dialect | `dotnet-engineer` |
| `ARC` | Architecture | boundaries, dependency direction, contracts, consistency, NFR allocation, and accepted decisions | `dotnet-architect` |
| `SEC` | Security | authentication, authorization, input/data protection, secrets, supply chain, abuse paths, and auditability | `dotnet-architect` |

`STK` is the stack lens. For this adapter it is .NET Quality; other adapters
name it after their own stack and use the same code.

## Reviewer Receipt

Each persona returns this shape without seeing the other receipts:

```text
REVIEWER: dotnet-architect | dotnet-engineer | dotnet-specialist
SOURCE_REVISION: <immutable revision>
PRF: <findings or NO-FINDING with evidence inspected>
ENG: <findings or NO-FINDING with evidence inspected>
STK: <findings or NO-FINDING with evidence inspected>
TST: <findings or NO-FINDING with evidence inspected>
ARC: <findings or NO-FINDING with evidence inspected>
SEC: <findings or NO-FINDING with evidence inspected>
BLIND_SPOTS: <missing evidence or none>
```

## Consolidation Rules

1. A finding needs current `file:line` or command/artifact evidence and a
   falsifiable verification step.
2. Deduplicate matching root cause and location after all receipts return.
3. Preserve the highest evidence-supported severity and list every reviewer
   that independently reported the root cause.
4. Preserve disagreements in `dissent`; do not decide by majority vote.
5. An unsupported lens is incomplete, not clean. Missing any persona makes the
   complete review invalid.
6. Assign each consolidated finding its id per the shared scheme
   (`{LENS-CODE}-{NNN}`, numbered per lens from `001`), plus `evidence-kind`
   (`executed` or `derived`) and `introduced-by-diff`.

Severity is `CRITICAL`, `HIGH`, `MEDIUM`, or `LOW`, defined once in the shared
file. `CRITICAL` means an immediate security, data-integrity, availability,
destructive-change, or public contract hazard. `HIGH` means likely incorrect
behavior or a material architecture, performance, test, or .NET-quality
regression. Lower severities must still name concrete impact; style-only
preference without project evidence is not a finding.
