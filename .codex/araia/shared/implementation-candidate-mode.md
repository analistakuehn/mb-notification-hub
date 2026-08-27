# Adapter Implementation Candidate Mode

Internal execution contract for an adapter implementation skill invoked by
`araia-slice-worker` during a validated SPEC-wide or EQI-remediation parallel
IMPLEMENT scheduler generation.

## Activation

The worker invokes the owning implementation skill with the Delivery Slice ID,
`--candidate-only`, the absolute `--slice-file`, `--mode`, and
`--approval-policy`. Accept this flag only when the invocation includes a valid
scheduler brief under
`.araia/runs/{SPEC-ID}/IMPLEMENT-parallel/workers/{SLICE-ID}/brief.md` and the
current repository is the detached worktree recorded there. Otherwise halt
with `F-CANDIDATE-CONTEXT`.

This mode overrides any lower implementation rule that would create a branch
or worktree, merge, stage for delivery, commit, mutate the canonical manifest,
update central scheduler state, or skip a user checkpoint merely because
execution occurs in a worktree.

## Required Behavior

1. Run the adapter's normal discovery, collaboration routing, RED, GREEN,
   REFACTOR, DOCUMENT, review, and validation phases for exactly the assigned
   Delivery Slice.
2. Reuse the detached worktree supplied by the parent. Never create another
   worktree or branch and never invoke the adapter's own multi-Delivery Slice
   parallel flow.
3. Evaluate every checkpoint through
   `./.codex/araia/shared/implementation-approval-policy.md`. For a safe
   `auto-recommended` checkpoint, persist the decision and continue. For
   `manual` or a mandatory stop, proxy through the worker: return control
   before the gated action and resume only with the exact checkpoint token and
   parent-provided decision.
4. Do not stage, commit, amend, merge, cherry-pick, rebase, or push. A normal
   commit-decision step becomes a candidate-readiness checkpoint: after
   approval, return the validated working-tree diff to the worker for
   packaging.
5. Do not mark the Delivery Slice completed or mutate the canonical manifest,
   `.araia/index.md`, central `scheduler.json`, or central `state.jsonl`. The
   canonical `commit SLICE-NNN` handler owns completion state after integration.
6. Keep implementation documents within the assigned Delivery Slice scope.
   The worker rejects the canonical manifest, another Delivery Slice,
   scheduler state, and paths outside the repository from the candidate.
7. Maintain `progress.json` per `shared/progress-tracking.md` after every Task,
   Task state, and checkpoint transition. Every worker event names SPEC,
   Delivery Slice, Task, exact progress, and approval decision.
8. End with a machine-readable candidate result containing validation evidence
   and the actual changed paths. Do not report a commit SHA.

## Termination

Success means the implementation cycle and validation passed, every checkpoint
has a durable manual or safe-auto decision, the worktree contains a non-empty
uncommitted candidate, and control returned to `araia-slice-worker`. Failure
preserves the worktree and reports a resumable checkpoint or exact failure code.
