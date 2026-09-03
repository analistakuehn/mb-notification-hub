# Retry Protocol (Shared)

This protocol defines how skills should retry failed agent dispatches with **incremental context enrichment** instead of blind retries.

> **Companion**: `./.agents/araia/shared/failure-taxonomy.md` defines the canonical failure codes (`F-ARTIFACT`, `F-PATH`, `F-GATE`, `F-TOOL`, `F-BUDGET`). This protocol owns the recovery semantics for `F-TOOL` (Level 1/2/3). Other codes have their own recovery paths defined in the taxonomy.

## Principle

A retry with the **exact same prompt** has a high probability of producing the same failure. Each retry must add diagnostic context that helps the agent avoid the previous failure mode.

## Retry Levels

### Level 1: Error-Enriched Retry

Add the previous failure details to the prompt. This is the **default first retry** for all agent failures.

**Additions to the original prompt**:
```markdown
## Previous Attempt Failed

### Error Output
[INJECT: The exact error message, stack trace, or validation failure from the previous attempt]

### What Went Wrong
[INJECT: Brief classification of the failure -- e.g., "Report missing code evidence", "Build compilation error", "Template format not followed"]

### Correction Instructions
Fix the issue described above. All other instructions remain the same.
```

### Level 2: Scope-Reduced Retry

If Level 1 also fails, **reduce the scope** of the request to lower complexity.

**Modifications to the original prompt**:
```markdown
## Reduced Scope (Retry #2)

Previous attempts failed. Reduce your output scope:
- If criteria/findings: produce 5-6 instead of 8-12
- If artifacts: produce the core sections only, skip optional sections
- If code analysis: focus on the 3 most impacted files only
- If implementation: implement one acceptance criterion at a time

[INJECT: Same error output from Level 1]
```

### Level 3: Skip and Report

If Level 2 also fails, **skip the agent** and note the gap in the consolidated output.

**Actions**:
1. Do NOT retry again
2. Log the failure with all 3 error outputs
3. In the consolidated report, add a section:
   ```markdown
   ## Skipped: [Dimension/Agent Name]
   Agent failed after 3 attempts. Errors:
   - Attempt 1: [one-line summary]
   - Attempt 2: [one-line summary]
   - Attempt 3: [one-line summary]
   
   **Impact**: [What analysis is missing due to this skip]
   **Recommendation**: Re-run with `--focus [agent]` after investigating the failure
   ```

## Stalled Dispatch (in-flight, no return)

A dispatch that has not returned needs a deadline, not surveillance. Detection and recovery:

- **Deadline, sibling-relative**: expected wall-clock for a chunk is ~10x its declared budget or, when a sibling chunk of comparable size has already returned, ~10x that sibling's actual duration. A comparable chunk returning in 12 minutes makes a peer chunk still silent at 121 minutes an objective stall; no transcript inspection needed. Never infer liveness or stall from the subagent's transcript on disk (per `progress-tracking.md` "Dispatch Liveness").
- **Recovery**: kill the dispatch and re-dispatch with Level 2 semantics, applying BOTH:
  0. **Durable candidate audit**: after the kill notification, apply `./.agents/araia/shared/durable-candidate-recovery.md`. If staging is `complete-valid`, do not re-dispatch; continue from its recorded next step. If it is `partial-valid`, exclude validated units from the split below.
  1. **Scope split**: divide the stalled chunk into two or more parallel chunks (per `progress-tracking.md` "Bounded Batch Dispatches").
  2. **Evidence budget**: the dominant stall mode in generation agents is re-verifying evidence already verified upstream instead of writing. Name the upstream verification explicitly ("evidence already verified by {signal report / gate}"), cap re-reading (4-6 files), and instruct the agent to prioritize producing the artifact. When a sibling artifact is already staged, attach it as an alignment reference so the agent calibrates against it instead of re-deriving.
- Log the stall as `F-BUDGET` in the manifest history with the kill decision and the split applied.

## Retry Decision Matrix

| Failure Type | Level 1 Action | Level 2 Action |
|-------------|----------------|----------------|
| Agent returns empty output | Retry with "Your response was empty" | Retry with simplified prompt |
| Agent returns wrong template format | Retry with template + "Match this format exactly" | Retry with fewer required sections |
| Agent returns < minimum criteria | Retry with "Found only N, need at least M" | Accept the reduced count, note in report |
| Agent returns > maximum criteria | Accept, truncate to max during consolidation | N/A (not a failure) |
| Build compilation error (mob or implementation loop) | Retry with compiler errors injected | Retry with single-file scope |
| Test failure (mob or implementation loop) | Retry with test output injected | Retry with simplified test |
| Agent timeout | Retry with reduced scope immediately | Skip agent |
| Dispatch stalled (no return past the deadline above) | Kill; re-dispatch split into parallel chunks with an evidence budget | Skip the remaining units, note the gap |
| Agent produces hallucinated code | Retry with "Read actual files. Previous attempt contained code not found in codebase" | Retry with explicit file paths to read |

## Mob Orchestrator Specific Rules

On the opt-in `strict-tdd` route, the mob orchestrator has its own retry
limits per phase. On the default adaptive route, retries are governed by the
Task-loop circuit breaker in the adapter's `flows/adaptive-task-loop.md`, which
opens after two consecutive attempts with the same failure fingerprint and no
new evidence.

| Phase | Max Retries | Escalation |
|-------|-------------|------------|
| RED (failing test) | 3 | Stop, present all attempts to user |
| GREEN (make test pass) | 3 | Stop, present all attempts to user |
| REFACTOR | 1 | Revert refactoring, continue to DOCUMENT |
| DOCUMENT | 2 | Skip documentation, note in report |

## Anti-Patterns

1. **Never retry with the exact same prompt**: always add error context.
2. **Never retry more than 3 times total**: if 3 attempts fail, the problem is structural.
3. **Never hide retry failures from the user**: always surface them in the consolidated output.
4. **Never retry destructive operations** (file deletes, git resets): ask the user instead.
