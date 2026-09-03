# Implementation Strategy Selection

Resolves the effective IMPLEMENT strategy before any source edit. This protocol
uses the evidence contract in `shared/implementation-evidence-contract.md` and
fails closed when a Delivery Slice does not satisfy the current contract.

## Inputs

| Input | Required | Source |
|---|---|---|
| Delivery Slice file | Yes | Resolved backlog path |
| Evidence contract | Conditional | Delivery Slice `## Evidence Contract` |
| CLI override | No | `--implementation-strategy adaptive\|strict-tdd` |
| Project constraints | Yes | Accepted ADRs, project instructions, adapter safety rules |
| Declared scope | Yes | Delivery Slice Tasks and files to create or modify |

## Effective Modes

| Mode | Meaning |
|---|---|
| `adaptive` | Select a registered evidence strategy per criterion and keep one strategy decision for each owned Task. |
| `strict-tdd` | Run the adapter's strict RED -> GREEN -> REFACTOR -> DOCUMENT loop while preserving the declared evidence contract. |

The CLI and internal state accept only `adaptive` and `strict-tdd`.
`adaptive` is the framework default. `strict-tdd` is opt-in: it applies only
when an accepted, citable constraint mandates it or the caller passes the flag.
Prescribing a ritual is a decision that needs a reason on the record, because
the framework measures implementation by outcome, not by process shape
(`PHILOSOPHY.md` #12).

## Resolution Algorithm

Apply the first matching rule:

1. If the Delivery Slice has no valid `## Evidence Contract`, halt with
   `F-ARTIFACT` before mutation. Require the canonical PLAN workflow to
   regenerate the Delivery Slice; do not infer oracles from prose.
2. If an accepted project constraint mandates strict test-first execution,
   select `strict-tdd`. The constraint must be citable: an accepted ADR, a
   project constitution, a regulatory obligation, or an adapter safety rule.
   A general appeal to best practice, a habit, or an unrecorded preference is
   not a constraint and selects nothing. An explicit adaptive override is a
   mandatory stop, naming the constraint and the smallest compliant
   alternative.
3. If the CLI explicitly selects `strict-tdd`, select it.
4. If the CLI explicitly selects `adaptive`, validate the evidence table and
   select `adaptive`.
5. If every evidence row explicitly selects `strict-tdd`, select
   `strict-tdd`.
6. Otherwise select `adaptive`.

Record the result before the first source edit:

```json
{"type":"implementation-strategy-selected","requested":"auto","effective":"adaptive","reason":"evidence-contract","slice":"SLICE-004"}
```

`requested` is `auto` when no CLI flag appears.

## Adaptive Row Selection

For an evidence row whose `Strategy` is `adaptive`, apply the first matching
signal:

| Signal | Strategy |
|---|---|
| Explicitly reported defect with a reproducible symptom | `reproduce-first` |
| Existing production target has no regression coverage | `characterization-first` |
| API, event, message, schema, or provider boundary changes | `contract-first` |
| User or operator journey changes | `scenario-first` |
| The outcome is a rendered artifact, document, report, or complex payload whose acceptance is human judgment applied once | `approved-scenarios` |
| Infrastructure, policy, migration, configuration, or architecture invariant changes | `validation-first` |
| No behavior change is intended | `behavior-preserving` |
| New deterministic domain behavior | `test-first` |
| Explicit bounded disposable spike | `prototype-first` |

Signals come from Delivery Slice fields, accepted artifacts, and repository evidence.
Keywords alone do not select a strategy. When two signals have equal authority
and imply materially different oracles, stop for a technical decision.

## Mandatory Stops

Stop before mutation when:

- an evidence row omits its observable outcome, oracle, fast check, boundary
  check, risk, or strategy;
- the evidence contract does not register the strategy;
- an evidence row selects `prototype-first` for high- or critical-risk work;
- the proposed work expands beyond the accepted Delivery Slice, Task, or declared file
  scope;
- an adaptive override conflicts with an accepted strict-TDD constraint;
- two equal-precedence strategies imply incompatible acceptance semantics;
- a required oracle cannot run in the assigned workspace;
- the selected strategy weakens a security, data, schema, production, or
  external-effect control.

Approval policy never auto-accepts these stops.

## Resume Rules

Persist the effective mode and per-row decisions in the sequential progress
snapshot or parallel worker brief. On resume:

1. reuse the recorded decision when the Delivery Slice, constraints, and evidence table
   hashes match;
2. re-run resolution when any hash changed;
3. append `implementation-strategy-changed` before continuing when the result
   differs;
4. never switch strategy in the middle of an unverified source mutation.

## Adapter Contract

Every implementation skill:

- accepts `--implementation-strategy <adaptive|strict-tdd>`;
- loads this protocol and the evidence contract before implementation;
- retains its existing strict cycle for `strict-tdd`;
- routes `adaptive` to its adapter-specific continuous Task loop;
- returns the effective strategy and evidence state in progress feedback.
