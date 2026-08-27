# Failure Taxonomy (Shared)

Canonical vocabulary for failure modes across the framework. Skills, orchestrators, and `flows/error-recovery.md` files reference these codes instead of duplicating prose.

## Principle

Every failure that surfaces from a skill, agent dispatch, or stage execution maps to one of five canonical codes. Each code carries a default recovery path. Skills MAY override the default in their own `flows/error-recovery.md` when domain context demands it; the override is additive, never contradictory.

## Canonical Codes

| Code | Name | Trigger | Default Recovery |
|------|------|---------|------------------|
| `F-ARTIFACT` | Missing artifact | Expected output path absent after the skill returns. Glob of declared `output-patterns` yields zero matches for a required artifact. | Re-run the skill once with the explicit expected path injected into the prompt. On a second miss, surface the gap to the user with the manifest entry highlighted; do not auto-retry a third time. |
| `F-PATH` | Wrong path | Glob finds the artifact at an unexpected location (different directory, different name suffix, etc.). | Update the manifest `artifacts[]` entry to the discovered path, log a `path-correction` history event, and continue. No retry needed. |
| `F-GATE` | Verifier failure | Quality gate automatic check fails (G2-G6 acceptance criteria, structural validation, content validation, or an inline verifier hook). | Render the gate output to the user. Offer (a) fix-and-rerun-gate, (b) `/araia reset {stage}` then `/araia next`, (c) override via `/araia gate {stage}`. Do not auto-retry. |
| `F-TOOL` | Tool error | Bash, Read, Write, Edit, or Skill tool returns a non-zero exit, raises an exception, or is denied by the permission system. | Hand to `./.claude/araia/shared/retry-protocol.md` Level 1 (error-enriched retry). On Level 1 failure, escalate to Level 2 (scope-reduced). On Level 3, mark the stage `failed`, surface the three error outputs, and pause the manifest. |
| `F-BUDGET` | Timeout / context overflow | Skill runs past its declared budget, parent context is truncated mid-run, the dispatch token estimate exceeds the orchestrator's remaining envelope, or an in-flight dispatch passes the stall deadline (no return past ~10x its declared budget or ~10x a comparable sibling chunk's actual duration; see `retry-protocol.md` "Stalled Dispatch"). | After the dispatch terminates or is killed, apply `durable-candidate-recovery.md` before changing stage status. A `complete-valid` candidate continues at review, consolidation, approval, or gate preparation without regeneration. A `partial-valid` candidate preserves valid units and pauses only the missing or invalid remainder. `invalid` or `absent` candidates use checkpoint-and-pause recovery. |

## Alignment Failure (sub-classification of `F-GATE`)

When the inline verifier accepts the artifact but the **user rejects it at the quality gate approval step**, the divergence is recorded as an alignment-failure entry in `docs/{SPEC_ID}/history.jsonl`:

```json
{"ts": "2026-04-29T14:00:00Z", "stage": "IMPLEMENT", "type": "alignment-failure", "verifier": "PASS", "user": "REJECT", "reason": "{user-supplied}"}
```

**Escalation**: three consecutive `alignment-failure` entries on the same stage trigger an Auto-Clarity pause. The orchestrator emits:

> "Verifier and user have disagreed three times on stage {STAGE}. The acceptance condition appears under-specified. Clarify: {prompt}"

The pause is honored before the next dispatch. Reset the counter when the user approves a stage without override.

## Cross-References

- `./.claude/araia/shared/retry-protocol.md`: handles `F-TOOL` Level 1/2/3 escalation and the "Stalled Dispatch" recovery. Skills cite both files when the recovery depends on retry semantics.
- `./.claude/araia/shared/validation-protocol.md`: produces `F-GATE` triggers for structural failures (missing `REPORT-START`/`REPORT-END` markers, minimum content length, code-evidence presence).
- `./.claude/araia/shared/staging-protocol.md`: `F-BUDGET` checkpoints align with the staging directory convention for parallel-agent reports.
- `./.claude/araia/shared/durable-candidate-recovery.md`: mandatory salvage-first classification for every `F-BUDGET` event with durable output.
- `./.claude/araia/shared/agent-uncertainty-protocol.md`: defines the structured envelope agents return for **epistemic** failures (insufficient evidence to conclude). Distinct from the codes in this taxonomy: the agent did produce an output, the output declares the dimension inconclusive. Orchestrators must not auto-retry these envelopes.
- Adapter `flows/error-recovery.md` files cross-link to this taxonomy and override only the rows where the default is wrong for the adapter's domain.

## Manifest Recording

Araia's `pipeline-runner.md` Phase 6 records the failure code in the manifest history so downstream automation can react:

```json
{"ts": "2026-04-29T14:00:00Z", "stage": "IMPLEMENT", "slice": "SLICE-001", "type": "failure", "code": "F-GATE", "detail": "{short message}", "recovery": "{action-taken}"}
```

Codes also appear in the `<!-- GATE-SUMMARY -->` block when applicable.

## Anti-Patterns

1. **Do not invent ad-hoc codes.** If a failure does not fit, propose a new canonical code via PR; do not silently use a custom string in a skill's `flows/error-recovery.md`.
2. **Do not collapse `F-ARTIFACT` and `F-PATH` into one code.** A missing artifact and a misplaced artifact require different recovery paths.
3. **Do not treat `F-GATE` as a `F-TOOL` retry candidate.** A gate failure is a semantic disagreement; auto-retry will not fix it.
4. **Do not pause or regenerate before auditing durable candidates on `F-BUDGET`.** A complete valid candidate continues; a partial valid candidate preserves its valid units. Every classification is checkpointed so `resume` never falls back to a blind restart.
