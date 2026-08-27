# Context Preservation Protocol (Shared)

This protocol is referenced by all skills that dispatch multiple agents and produce report/artifact files. It prevents context window compaction from destroying agent outputs before consolidation.

## Scope

`.staging/` is a **transient buffer for in-flight agent reports only**. It must never contain:

- The spec file (`specification.md`) -- lives at `docs/SPEC-{NNN}/` directly.
- The manifest (`manifest.md`) -- lives at `docs/SPEC-{NNN}/` directly.
- Any artifact that has already been promoted (post-consolidation files belong in their stage directory: `analyses/`, `requirements/`, `refinements/`, `backlogs/`, `eqi/`).

If you are creating a spec or initializing pipeline state, write directly to `docs/SPEC-{NNN}/`. Staging is only for the multi-agent report-collection pattern below.

## Problem

Dispatching 5+ agents that each produce large reports can cause context window compaction, which **irreversibly loses** earlier report content before the orchestrator can consolidate results.

## Solution: Write-As-You-Go to Staging

All agent outputs are persisted to disk **immediately** after each agent returns. Consolidation always reads from disk, never from conversation memory.

## Rules (Inviolable)

### Rule 1: Create Staging Directory First

Before dispatching any agents, create the staging directory:
```bash
mkdir -p {OUTPUT_DIR}/.staging/
```

### Rule 2: Immediate Write After Every Agent

After EVERY agent returns its output, **IMMEDIATELY** write it to staging using the Write tool:
```
Write tool -> {OUTPUT_DIR}/.staging/{NN}-{dimension}-report.md
```

Do NOT process, analyze, or dispatch the next agent before writing. The write is the **first action** after receiving output.

### Rule 3: Read From Disk for Consolidation

For the consolidation step, **ALWAYS** read agent reports from staging files on disk using the Read tool. **NEVER** rely on conversation memory for report content.

```
Read tool -> {OUTPUT_DIR}/.staging/{NN}-{dimension}-report.md
```

### Rule 4: Background Dispatch

Use `run_in_background: true` for all parallel agent dispatches. As each background agent returns its output:
1. Write that output to staging **immediately** with the Write tool (Rule 2)
2. Continue collecting the remaining agents
3. Once all agents have returned, read the staging files from disk (Rule 3) for consolidation

### Rule 5: Cleanup on Rejection

If the user rejects all reports at the approval checkpoint, delete the staging directory:
```bash
rm -rf {OUTPUT_DIR}/.staging/
```

### Rule 6: Staging to Final Pipeline

After user approval, move files from staging to their final locations:
1. Create the final directory structure (e.g., `docs/SPEC-{NNN}/analyses/`)
2. Move/rename each staging file to its final path (no date suffix -- git is the versioning system per `pipeline/stages.md` File Naming rules)
3. Remove the `.staging/` directory

**Promotion target rule**: agent reports in `{output-root}/.staging/` are promoted to `{output-root}/{stage-dir}/` (e.g., `docs/SPEC-001/analyses/`). They never stay nested in `.staging/`, and the spec file / manifest are never inside `.staging/` to begin with.

### Rule 7: Context Compaction is Not a Problem

If context compaction occurs during consolidation, it is **not a problem** -- all reports are on disk in staging. Read from staging files to continue. This is the whole point of this protocol.

### Rule 8: Recover Complete Candidates Before Regeneration

When a dispatch ends with `F-BUDGET`, apply `./.claude/araia/shared/durable-candidate-recovery.md` after the completion or kill notification and before retrying, pausing, or marking the stage failed. Validate the staging roster with the normal structural and content invariants. Continue from a `complete-valid` candidate's recorded next step; preserve valid units from `partial-valid`; never delete staging or regenerate validated units because of budget exhaustion alone.

## Staging File Naming Convention

| Pattern | Example | Used When |
|---------|---------|-----------|
| `{NN}-{dimension}-report.md` | `01-architecture-report.md` | Per-agent analysis reports |
| `{NN}-{dimension}-{supplement}.md` | `01-architecture-graphql-supplement.md` | Supplementary agent reports |
| `_impact-map.md` | `_impact-map.md` | Pre-computed shared context |
| `_signal-report.md` | `_signal-report.md` | Codebase analysis output |

Files prefixed with `_` are shared context artifacts, not publishable reports.

## Anti-Patterns (Never Do These)

1. **Never keep report content only in conversation context** -- always write to disk first.
2. **Never proceed to consolidation without verifying all staging files exist** -- use Glob to check.
3. **Never write to final directory before user approval** -- staging is the buffer.
4. **Never batch multiple agent writes** -- write each one immediately as it arrives.
5. **Never treat `F-BUDGET` as proof that staging is incomplete** -- classify the durable candidate first.
