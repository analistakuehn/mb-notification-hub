# Agent Uncertainty Protocol (Shared)

Standard envelope for agents to return **structured epistemic failure** instead of forcing a finding when the evidence does not support one. Complements `retry-protocol.md` (which handles tool failures) and `validation-protocol.md` (which enforces evidence on findings that *are* produced).

## Principle

A skilled engineer who has read the relevant code and still cannot answer with confidence says so, they do not invent a finding to fill the slot. Agents must do the same. When an agent reaches a question it cannot answer with adequate evidence, the honest output is a structured uncertainty envelope, not a fabricated or hedged finding.

This protocol gives agents a sanctioned "I cannot conclude" exit so they do not feel pressured by the dispatch contract to produce a finding-shaped artifact when the underlying epistemic state is "insufficient evidence".

## When to Emit Uncertainty Instead of a Finding

Emit the envelope when **any** of the following holds:

- The dispatched dimension cannot be evaluated because the codebase lacks the relevant feature, file, or layer.
- The reachable evidence is contradictory and the agent cannot resolve the contradiction without user input.
- The dispatched scope is too narrow to produce a finding the agent would stand behind (e.g., asked to assess "data layer quality" in a codebase with no persistence code).
- The agent would otherwise produce a finding annotated `[LOW-CONFIDENCE: ...]` for every substantive claim.

Do **not** emit the envelope when:

- The agent simply needs more time, more files, or a retry, those are `F-TOOL` cases handled by the retry protocol.
- The agent has *some* evidence and *some* uncertainty, produce findings with inline `[LOW-CONFIDENCE: ...]` annotations per the standing honesty obligation in `auto-clarity-protocol.md`.
- The agent disagrees with the dispatch premise, surface the disagreement as a finding, not as inability to conclude.

The envelope is for **epistemic failure**, not effort or scope disputes.

## Envelope Format

Agents return the envelope inside the standard `REPORT-START`/`REPORT-END` markers so structural validation still passes:

```markdown
<!-- REPORT-START: DIMENSION={name}, AGENT={subagent_type} -->
<!-- UNCERTAINTY: STATUS=INSUFFICIENT-EVIDENCE -->

## Cannot Conclude

**What I was asked**: {one-line restatement of the dispatched question}

**What I read**:
- {file or area examined}: {one-line observation, with `file:line` if applicable}
- ...

**Why I cannot conclude**:
{2-4 sentences naming the specific evidence gap. State what is missing, not what is present.}

**What would unblock a conclusion** (in priority order):
1. {Concrete artifact or input — e.g., "access to integration test logs from the last release"}
2. {...}

**What I would NOT do**:
- Produce a finding without the evidence above. Doing so would violate `validation-protocol.md` C1.

<!-- REPORT-END: DIMENSION_SCORE=N/A -->
```

The `DIMENSION_SCORE=N/A` literal is reserved for uncertainty envelopes. Consolidation skills must recognize `N/A` and exclude the dimension from any aggregate score, not coerce it to zero.

## Orchestrator Handling

Skills that dispatch agents and consolidate results must:

1. **Detect** the envelope by scanning for `<!-- UNCERTAINTY: STATUS=INSUFFICIENT-EVIDENCE -->` after `REPORT-START`.
2. **Preserve** the envelope verbatim in the consolidated report under a dedicated `## Inconclusive Dimensions` section. Do not paraphrase.
3. **Exclude** the dimension from aggregate scoring math. Do not substitute zero, do not interpolate from neighbors.
4. **Surface** the unblock list to the user as actionable next steps.
5. **Do not auto-retry** an uncertainty envelope. Unlike `F-TOOL`, this is not a retryable failure, the agent has already concluded that no retry will succeed without new input.

Consolidation template addition:

```markdown
## Inconclusive Dimensions

The following dimensions could not be evaluated. Each is reproduced verbatim from the dispatched agent. Aggregate scores below exclude these dimensions.

### {DIMENSION}

{verbatim envelope content}

---

## Aggregate (excluding inconclusive dimensions)

Scored: {N} of {TOTAL} dimensions.
Inconclusive: {LIST}.
```

## Anti-Patterns

1. **Do not silently coerce `N/A` to zero in aggregates.** Coercion turns "we did not assess" into "we assessed and it is bad", a fabricated finding.
2. **Do not retry an uncertainty envelope as if it were `F-TOOL`.** The agent has already declared the failure non-retryable; retrying without new evidence wastes cycles and pressures the agent toward fabrication.
3. **Do not let agents emit the envelope as a shortcut for hard work.** If an agent emits uncertainty for a dimension that *is* evaluable from the reachable evidence, the orchestrator should surface this as a quality issue (potential agent misbehavior), not silently accept it. The "What I read" list is the audit trail.
4. **Do not edit the envelope during consolidation.** Preserve verbatim. The unblock list is the user's path forward and must reach them unmodified.

## Cross-References

- `shared/auto-clarity-protocol.md`: standing honesty obligation; the envelope is the artifact-level expression of that obligation when no finding is producible.
- `shared/validation-protocol.md`: C1 evidence threshold; the envelope is the sanctioned alternative to fabricating evidence to clear C1.
- `shared/retry-protocol.md`: for tool/format failures; uncertainty envelopes are NOT retryable under that protocol.
- `shared/failure-taxonomy.md`: uncertainty envelopes are a distinct concept from `F-ARTIFACT` (no output produced) and `F-GATE` (output rejected). The envelope IS the output; it just declares insufficient evidence.
