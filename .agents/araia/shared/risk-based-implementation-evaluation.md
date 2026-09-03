# Risk-Based Implementation Evaluation

Defines when IMPLEMENT must challenge a candidate with an independent evaluator.
The evaluator tests whether evidence proves the declared outcome; it is not a
mandatory ceremonial review after every Task.

## Dispatch Decision

Dispatch one isolated evaluator when any trigger is present:

| Trigger | Reason |
|---|---|
| Evidence risk is `high` or `critical` | Failure has material impact even when ordinary sensors pass |
| Public API, event, message, schema, migration, or provider contract changes | Compatibility and consumer effects cross the local Task boundary |
| Authentication, authorization, privacy, secrets, money, fiscal, KYC, destructive data, or irreversible operations change | Safety requires an independent challenge |
| Persistence, concurrency, ordering, idempotency, retry, cache consistency, or external side effects change | State and timing defects can escape focused checks |
| The Task-loop circuit breaker opens or the builder changes its oracle after a failure | The implementation path or proof mechanism has become unstable |
| Actual paths exceed the declared lock/scope set | Scope drift can invalidate the accepted plan and sensor selection |
| Required behavior depends on a runtime or human-visible scenario that deterministic sensors only partially observe | Evidence has an acknowledged observation gap |
| A prototype becomes a production implementation | Disposable assumptions require an independent challenge before integration |

Do not dispatch an evaluator for a low-risk change when all required sensors
are deterministic and green, evidence is `boundary-verified`, scope is
unchanged, and no trigger above is present. Medium risk alone does not force a
dispatch, but any listed trigger does.

Record the decision:

```json
{
  "required": true,
  "triggers": ["public-contract", "risk:high"],
  "status": "pending"
}
```

## Independence Boundary

The evaluator runs in a fresh isolated context and receives only:

- accepted Delivery Slice text, evidence contract, and higher-precedence ADR or policy;
- base and candidate source needed to reproduce the behavior;
- changed-path inventory and lock-drift report;
- concise `L1-fast`, `L2-task`, and `L3-slice` sensor results;
- exact commands or runtime entry points needed to inspect the oracle.

Do not provide the builder's reasoning, transcript, identity, confidence,
hypotheses, discarded attempts, or preferred verdict. The evaluator
reconstructs the case from source and observations. It has read/execute access
but cannot edit the candidate, its tests, its evidence contract, or its sensor
records.

An adapter selects the evaluator specialty from the trigger. It can use the
same agent type as the builder only in a new isolated context that excludes the
builder's reasoning. Prefer a security, data, performance, architecture, UX, or
contract specialist when that domain activated the trigger.

## Evaluation Task

For each triggered evidence row:

1. state the observable outcome and oracle without paraphrasing their meaning;
2. attempt to refute that the recorded evidence proves the outcome;
3. reproduce or independently inspect the strongest applicable boundary;
4. verify that the candidate did not weaken the oracle, skip required scope,
   or encode the expected answer into its grader;
5. return one verdict with direct evidence.

This is bounded adversarial evaluation, not a general code review. Record
incidental defects separately without silently folding them into the requested
verdict.

## Verdict Contract

```json
{
  "evidence_row": "AC-2",
  "verdict": "confirm",
  "trigger": "public-contract",
  "oracle_observed": "HTTP 422 with code COUPON_EXPIRED",
  "commands": ["dotnet test --filter CouponContractTests"],
  "evidence_refs": [".../L3-slice-contract.json"],
  "finding": null,
  "residual_uncertainty": null
}
```

Valid verdicts:

- `confirm`: the independent attempt did not refute the outcome and cites
  reproducible evidence;
- `refute`: evidence or implementation contradicts the accepted outcome,
  boundary, or risk control;
- `uncertain`: the evaluator cannot reach a safe result because the oracle,
  environment, scope, or evidence is incomplete or contradictory.

An evaluator's opinion cannot override a deterministic failing sensor,
higher-precedence artifact, or human safety gate. A `confirm` strengthens
evidence but does not replace required sensor levels.

## Actions

| Verdict | Action |
|---|---|
| All `confirm` | Record `status: confirmed` and continue to final review |
| Any `refute` | Reject candidate promotion, attach the smallest reproducible finding, and return the affected Task to `correcting` |
| `uncertain` on high/critical risk or an irreversible effect | Mandatory stop for oracle repair, environment repair, or accountable human decision |
| `uncertain` on medium/low risk | Run one bounded alternative deterministic sensor; if uncertainty remains, stop rather than auto-confirm |

After a correction, rerun invalidated sensors and the evaluator for every
affected trigger. Do not ask the evaluator to repair its own finding, and do
not reuse a verdict when the candidate, evidence contract, risk, or input
fingerprint changes.

## Cost and Audit Rules

- One evaluator can assess several triggered rows that share the same domain and
  boundary; unrelated domains use separate bounded evaluations.
- Persist trigger, evaluator role, commands, verdict, evidence references,
  duration, and candidate fingerprint.
- Track evaluator dispatches and verdict distribution as IMPLEMENT metrics.
- No multiple-judge dispatch merely to vote. Add a specialist only when a
  distinct risk domain requires distinct evidence.
