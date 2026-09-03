# IMPLEMENT Approval Policy

Normative checkpoint policy for `/araia implement`, the IMPLEMENT loop entered
through `/araia next`, and the Delivery Slice/SPEC commit scopes that finalize IMPLEMENT
work.

## Modes

| Policy | Behavior |
|---|---|
| `auto-recommended` | Automatically selects the one option explicitly marked `Recommended` when the safe-auto criteria below are all proven. This is the default for IMPLEMENT. |
| `manual` | Presents every checkpoint and waits for the user. |

`/araia implement ... --approval-policy <mode>` selects the effective policy for
the invocation. A new parallel run stores it in `scheduler.json`; a resume with
no flag reuses the stored value. An explicit flag on resume replaces the stored
value and appends an `approval-policy-changed` event. Delivery Slice and SPEC commit scopes
accept the same flag and otherwise default to `auto-recommended`; project-wide
`/araia commit` is always manual.

The effective policy is passed unchanged to the selected Delivery Slice
executor (implementation skill or scaffold skill), worker brief, candidate-mode
checkpoint proxy, integration queue, and canonical commit flow. Nested
specialists do not independently broaden it. Within an Araia IMPLEMENT
invocation this protocol overrides lower-level wording that says a routine
checkpoint always needs explicit approval; the mandatory-stop rules below
never yield.

## Safe Auto-Accept Criteria

Auto-accept a checkpoint only when every condition holds:

1. Exactly one option is explicitly labeled `Recommended`; a bare first option
   or an inferred preference is not enough.
2. The action is wholly inside the active SPEC, Delivery Slice, Task, accepted backlog, and
   declared file-lock or sealed-file boundary.
3. Required validation for the current transition passed, and the evidence is
   available in the current run state.
4. The action has no external side effect and does not destroy or irreversibly
   transform user or production data.
5. No unresolved ambiguity, conflict, scope drift, security veto, failed hook,
   or material warning changes the recommendation.

Routine safe-auto checkpoints include:

- creating or switching to the recommended local feature branch;
- accepting the validated DAG, initial ready set, and deterministic lock plan;
- advancing RED -> GREEN -> REFACTOR -> DOCUMENT after the preceding phase's
  invariant passes;
- accepting a discussion or review result with one safe recommendation;
- packaging and staging exactly the scheduler-owned or user-reviewed Delivery Slice file
  set;
- integrating a clean candidate whose base, hashes, locks, and canonical
  validation all match;
- creating the proposed local Conventional Commit for a sealed Delivery Slice/SPEC scope;
- performing the canonical amend that only adds the just-created Delivery Slice metadata;
- releasing proven scheduler-owned locks and removing a successful detached
  worktree;
- advancing from IMPLEMENT after G5 passes and the transition is the single
  recommended lifecycle action.

## Mandatory Stops

Always stop and ask the user, regardless of policy, for:

- work outside the active SPEC/Delivery Slice/Task or any unplanned file/scope expansion;
- push, pull request creation, deployment, provider mutation, notification, or
  any other external side effect;
- destructive data/schema/resource operations, production changes, secret or
  permission changes with material blast radius, or bypassing a safety control;
- integration conflicts, lock drift, dependency drift, failed validation,
  failed commit hooks, or an attempt to replace an already completed commit;
- an inconclusive recommendation, no option marked `Recommended`, more than one
  recommended option, or a choice whose trade-off cannot be resolved safely;
- history rewrites other than the canonical amend of the commit created moments
  earlier by the same bounded Delivery Slice/SPEC flow;
- reset, discard, force, skip, override, or cleanup that could remove user work
  or non-ephemeral evidence.

When a mandatory stop fires, render the evidence and the available choices. Do
not silently fall back to another option and do not downgrade the stop to a
warning.

## Audit Event

Every checkpoint, including an auto-accepted one, is durable. Append a compact
event to the active IMPLEMENT `state.jsonl` (and the parallel scheduler stream
when applicable):

```json
{"ts":"2026-07-15T12:00:00Z","type":"approval-decision","policy":"auto-recommended","mode":"auto","spec":"SPEC-002","slice":"SLICE-004","task":"Task 2","checkpoint":"green-to-refactor","decision":"continue","reason":"unique-safe-recommendation"}
```

Use `mode: "manual"` when the user answered. Persist the event before the gated
action, and include the decision in the next implementation feedback envelope.
