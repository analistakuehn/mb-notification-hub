# Durable Candidate Recovery

Recovery protocol for `F-BUDGET` events when a specialist may have persisted usable work before its dispatch ended. This protocol applies to every Araia stage and every producing skill that writes to `.staging/` or a declared run workspace.

## Principle

Budget exhaustion describes how a dispatch ended; it does not establish whether its durable output is complete. After the harness reports dispatch completion or a kill notification, validate persisted candidates before deciding to regenerate, retry, pause, or fail.

This protocol never treats file growth as a liveness signal. Do not inspect staging while a dispatch is still running. Dispatch liveness comes only from the harness completion notification and declared per-unit returns.

## Recovery Classification

Inventory the specialist's declared staging root, output patterns, run `state.jsonl`, and artifact checkpoint. Compare the durable candidate with the expected roster from the approved artifact tree, implementation sequence, Delivery Slice list, criterion list, dispatch brief, or checkpoint.

Classify the candidate as exactly one of:

| Classification | Required evidence | Action |
|---|---|---|
| `complete-valid` | Every expected unit exists; IDs and counts match; structural and content validators pass; dependencies resolve; no unresolved placeholders remain; checkpoint hashes and section coverage match when declared | Preserve the candidate, append a recovery checkpoint, keep the stage `in_progress`, and continue at the next specialist sub-step such as review, consolidation, approval, or gate preparation. Do not regenerate. |
| `partial-valid` | At least one expected unit validates, but the roster is incomplete | Preserve and checkpoint every valid unit, identify only the missing or invalid units, mark the stage `paused`, and resume or split only those units. |
| `invalid` | Files exist but fail required invariants | Preserve them for audit, record the exact validation failures, and retry only the invalid units with enriched or reduced scope. Never promote them. |
| `absent` | No durable candidate exists | Apply normal `F-BUDGET` checkpoint and pause behavior. |

## Validation Invariants

Use the same validators that govern the normal path. Recovery is not a weaker gate.

1. Expected roster: exact unit count and IDs, or every unit declared by the approved tree or checkpoint.
2. Structural contract: required sentinels, headings, metadata, minimum content, and file naming.
3. Content contract: adapter-specific evidence, acceptance, scoring, test, or artifact rules.
4. Referential integrity: dependencies point to known units and graphs are acyclic when applicable.
5. Completion hygiene: no unresolved `{{placeholder}}`, producer instruction, truncation marker, or mixed-language candidate.
6. Durable identity: recompute SHA-256 and validate declared section coverage when the specialist uses unit checkpoints.
7. Next-step proof: record whether the candidate still requires specialist review, consolidation, human approval, or only the stage gate.

## Checkpoint Event

Append one compact JSON line to the specialist checkpoint and mirror it to the stage run's `state.jsonl` when available:

```json
{"ts":"2026-07-11T12:00:00Z","stage":"PLAN","type":"budget-recovery","code":"F-BUDGET","classification":"complete-valid","units":13,"next_step":"staged-slice-review","recovery":"validated-staging-candidate"}
```

For `partial-valid`, also record `completed_units`, `missing_units`, `invalid_units`, and the planned split. Never overwrite prior checkpoint events.

## Stage Continuations

| Stage | Complete candidate continues at |
|---|---|
| SPECIFY | Next ungenerated artifact, cross-artifact review, or approval checkpoint, according to the artifact checkpoint |
| REFINE | Specialist review or consolidation, then the refinement approval checkpoint |
| PLAN | Staged Delivery Slice review in the resolved collaboration mode, then backlog approval; do not assume reviewers have reviewed a generated candidate |
| IMPLEMENT | Next incomplete TDD phase or acceptance criterion; passing tests and implementation invariants still apply |
| VERIFY | Remaining dimension review or consolidation/scoring, then the verification gate |
| DELIVER | Remaining deterministic validation or human approval; never repeat an already completed external side effect |

## Safety Boundaries

- Do not promote, publish, commit, open a PR, or advance a gate merely because the candidate is complete.
- Do not infer that a specialist reviewed generated content. Require a durable `next_step` or review-completed checkpoint; otherwise resume at review.
- Do not delete staging on `F-BUDGET`.
- Do not regenerate a `complete-valid` candidate.
- Do not regenerate validated siblings of a `partial-valid` candidate.
- If validation itself cannot complete within the remaining budget, checkpoint the inventory and pause; do not guess.
