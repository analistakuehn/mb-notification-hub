# Refactoring Triggers

Schedules refactoring from observed signals instead of from a position in a
cycle.

## Why This Exists

A REFACTOR phase that runs because the loop reached it produces cleanup when
nothing needs cleaning and skips it when the pressure is real. An agent also
lacks the friction that tells a human "this is getting hard to change": it reads
the whole requirement at once and pays no cognitive cost for a sprawling change.
The framework therefore removes refactoring from the inner ritual and attaches
it to signals that are visible from outside the loop.

## Principle

A trigger proposes work. It never rewrites code by itself and never blocks a
gate. When a trigger fires, the framework either opens a `behavior-preserving`
Task inside the current Delivery Slice boundary or records a candidate for the
backlog. Refactoring that changes behavior is not refactoring; it needs its own
acceptance criteria.

## Trigger Table

| Trigger | Source | Fires when | Action |
|---|---|---|---|
| Static analysis on changed paths | Adapter analyzer, linter, complexity rule | A new finding appears inside the Delivery Slice boundary | Repair inside the same Delivery Slice as a `behavior-preserving` Task |
| Modular structure review | `G5s` narrow EQI, architecture dimension | The checkpoint reports a structural finding attributable to this Delivery Slice | Repair now, or record with an owner and a revisit condition |
| Change surface spread | `files_touched` counter | The Delivery Slice median rises above the SPEC rolling median by half again, over at least five completed slices | Propose a seam: the change is crossing a boundary that does not exist yet |
| Loop cost drift | `output_tokens` counter | Tokens per completed Task rise while `files_touched` stays flat, over at least five completed Tasks | Inspect context and structure: the loop is paying more to achieve the same increment |
| Regression value decay | `shared/test-effectiveness-sensor.md` trend | The mutation score falls on a target whose declared scope did not change | Repair the suite before adding behavior to that target |
| Repeated circuit breaker | Adaptive loop fingerprint | The breaker opens twice on the same module across different Tasks | Escalate as a structural decision, not another coding attempt |

## Counters

The IMPLEMENT loop records two counters per attempt and aggregates them per Task
and per Delivery Slice, per `shared/progress-tracking.md`:

| Counter | Definition |
|---|---|
| `files_touched` | Distinct files written during the attempt, excluding generated output and lockfiles |
| `output_tokens` | Model output tokens attributed to the attempt, when the harness exposes them |

A harness that cannot report `output_tokens` records `null`. A null counter
disables its trigger and never fabricates a trend.

## Comparison Rules

1. Compare against the SPEC's own rolling median, never against an absolute
   number carried in from another project.
2. Require the declared minimum sample before firing. Below it, record the
   value and stay silent.
3. Exclude foundation and scaffold slices from the baseline: generated output
   distorts both counters.
4. Report the samples that produced the verdict alongside the verdict. A
   trigger that cannot show its inputs is noise.

## Action Ladder

1. **Record**: always. The counter and the finding land in the Delivery Slice
   evidence, whether or not the trigger fires.
2. **Repair in place**: when the finding sits inside the current Delivery Slice
   boundary and a `behavior-preserving` Task closes it, with existing focused
   and boundary checks green before and after the edit.
3. **Propose**: when the finding is real but outside the boundary, record a
   backlog candidate with the evidence. Do not widen the accepted scope.
4. **Escalate**: when the signal indicates a structural decision (a missing
   seam, a wrong ownership boundary), route it to the adapter's decision
   capability. Implementation does not own architecture decisions.

## Non-Goals

Do not refactor to move a counter. The counters detect pressure; they are not
targets, and optimizing them directly produces churn that the seals will flag on
the next sweep.
