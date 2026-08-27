# Greenfield Foundation Slice Contract

## Purpose

Defer greenfield source generation pending approval of product and architecture
decisions. `init` records intent only; SPECIFY defines the foundation; PLAN
creates the first Delivery Slice or Delivery Slices; IMPLEMENT materializes and
verifies them.

A foundation Delivery Slice does not decide the architecture. It implements an
accepted architecture and produces an observable, runnable baseline.

## Lifecycle

1. **INIT records intent**. In greenfield mode, `--scaffold` and the default
   behavior create a deferred foundation request. `init` validates the adapter
   scaffold capability and requested flags but MUST NOT dispatch a scaffold or
   write source, Stack Profile, scaffold metadata, or architecture-baseline
   files.
2. **SPECIFY decides and seeds**. The Development Specification and accepted
   decision artifacts define the architecture. The Implementation Map contains
   one wave-zero foundation seed per scaffold-capable implementation adapter.
   A seed names an observable baseline outcome, owning adapter, requirements,
   dependencies, and risks; it is not yet a `SLICE-NNN` and contains no final
   tasks, estimates, file inventory, or acceptance wording.
3. **PLAN materializes first**. The global PLAN workflow converts foundation seeds
   before product seeds. Foundation Delivery Slices receive the lowest
   contiguous IDs beginning at `{PREFIX}-001` (default `SLICE-001`). In a
   multi-adapter SPEC, create one independently committable foundation slice
   per owning adapter, with no cross-adapter foundation megaslice.
4. **IMPLEMENT executes by route**. A foundation slice with
   `Executor: scaffold` dispatches the owning adapter's declared scaffold skill.
   Ordinary slices and explicitly non-generated foundations use
   `Executor: pair`.
5. **Commit closes the foundation**. The canonical Delivery Slice commit owns
   the source commit and manifest completion. When the canonical commit flow
   closes every foundation slice, set `pipeline.foundation.status: materialized`.

Brownfield SPECs use `strategy: existing` and do not receive an automatic
foundation seed. `--no-scaffold` uses `strategy: external`: Araia generates no
scaffold-owned foundation slice and G4 blocks implementation until the
externally created baseline and required Stack Profiles exist.

## Manifest Contract

`init` writes the following additive block under `pipeline`:

```yaml
foundation:
  strategy: "deferred-scaffold"       # deferred-scaffold | external | existing | inherited
  source: "default"                   # default | user-override
  status: "pending"                   # pending | planned | materialized | inherited | not-applicable
  owner-spec: "SPEC-001"
  adapters:
    dotnet:
      scaffold-skill: "dotnet-scaffold"
      profile-path: ".araia/stack-profile.yaml"
      requested-architecture: "modular-monolith"
      requested-args: "--solution Example --namespace Example --framework net10.0"
```

Rules:

- `deferred-scaffold` is the greenfield default and the explicit `--scaffold`
  result.
- `external` is the explicit `--no-scaffold` result.
- `existing` is the brownfield result and starts as `materialized`.
- `inherited` applies only to non-owning SPECs from `init --split`.
- `requested-*` values are preferences or explicit user constraints. SPECIFY
  must reconcile them with evidence and accepted decisions; it must not treat a
  default as an accepted architecture decision.
- For `dotnet-scaffold`, `requested-architecture` carries the selected starter
  entry: `modular-monolith`, `vertical-slice`, `clean`, or `hexagonal`. The
  architect handoff records the accepted canonical architecture separately, as
  `modular-monolith` for the first two entries and as itself for `clean` and
  `hexagonal`. A layered entry requires an accepted architecture decision, so
  SPECIFY must not promote it from a request alone.
- `traceability.stack-profiles` stays empty until a verified foundation
  implementation or brownfield analyzer writes a profile.
- PLAN mirrors `kind` and `executor` into each IMPLEMENT roster entry:
  `{status, adapter, kind, executor, dependencies, effort}`.

For `init --split`, select exactly one dependency-ordered SPEC as
`owner-spec` during the batch approval. Only that SPEC receives local
foundation seeds. Other greenfield SPECs use `strategy: inherited` and record
the owner's materialized foundation as an external prerequisite. Run the
scaffold only for the selected owner.

## Implementation Map Contract

Each deferred adapter receives one wave-zero seed with:

- outcome: a runnable and verified architecture baseline;
- owning adapter;
- requirement and accepted decision provenance;
- no local dependency unless a cross-cutting foundation needs an application
  foundation first;
- risk and rollout constraints;
- marker `foundation: true`;
- requested scaffold preferences as non-authoritative input.

Primary/backend seeds sort first, secondary application seeds next, and
cross-cutting infrastructure seeds last. Product seeds depend only on the
foundation seed or seeds whose generated surface they require.

For multiple generated foundations, SPECIFY accepts distinct output roots and
profile locations. PLAN must prove that Managed Paths do not overlap, unless
the owning scaffold contracts explicitly support safe composition on the same
root. By default, the primary can own the repository root, and each secondary
owns a bounded subroot with its Stack Profile below that subroot.

## Delivery Slice Shape

PLAN adds these rows to the normal metadata table:

```markdown
| **Kind** | foundation |
| **Executor** | scaffold |
```

`Kind` defaults to `product`; `Executor` defaults to `pair` when absent.

A scaffold-routed slice also contains:

```markdown
## Scaffold Execution

| Field | Value |
|---|---|
| Skill | dotnet-scaffold |
| Arguments | --solution Example --namespace Example --framework net10.0 --architecture modular-monolith --module Billing |
| Stack Profile | .araia/stack-profile.yaml |
| Managed Paths | src/**; tests/**; Example.sln; Directory.Build.props; Directory.Packages.props; NuGet.Config; .araia/stack-profile.yaml; .araia/scaffold-metadata.json |
```

The skill must equal the owning adapter's declared scaffold skill. Arguments
must conform to that skill's Input Contract and accepted SPECIFY decisions.
Managed paths must be repository-relative, bounded, and complete enough for
candidate locking and commit scope.

The observable acceptance contract includes:

1. deterministic generator completion;
2. adapter-native restore/install, build/typecheck, lint/analyze, and tests;
3. the declared Stack Profile written last after verification;
4. architecture baseline and fitness functions when triggered;
5. no product behavior beyond the generator's bounded example or health check.

## IMPLEMENT Routing

For `Executor: scaffold`:

1. validate `Kind: foundation`, the owning adapter, declared scaffold skill,
   arguments, managed paths, the architect's foundation handoff, and
   empty/unrelated-source preconditions;
2. dispatch the scaffold skill directly through the active harness skill verb;
3. require its terminal verified-success status and declared Stack Profile;
4. verify actual changes stay within Managed Paths;
5. update `traceability.stack-profiles[adapter]` and the foundation history;
6. leave the Delivery Slice `in_progress` and route to the canonical
   `commit SLICE-NNN` flow.

The scaffold skill owns generation and its internal verification. The pair
orchestrator must not recreate scaffold templates or generator logic.

### Deferred target precondition

A pipeline foundation runs after `init`, SPECIFY, and PLAN, so the repository is
not byte-empty. Scaffold skills must treat the following existing control-plane
surface as safe overlap when, and only when, a matching active foundation slice
is `in_progress`:

- `.git/**`, `.gitignore`, `README.md`, and `LICENSE`;
- Araia/Codex/Claude harness files under `.araia/**`, `.codex/**`, and
  `.claude/**`;
- `AGENTS.md`, `CLAUDE.md`, `.github/PULL_REQUEST_TEMPLATE.md`, and the active
  SPEC documentation under `docs/**`.

Safe overlap permits existing files to remain present; it never permits
overwrites. The executor still refuses an existing source tree,
package/build manifest, Stack Profile, or other path that its generator owns,
except when the scaffold explicitly supports a verified same-configuration
idempotent rerun. A standalone scaffold without a matching active foundation
slice keeps its original narrow empty-directory precondition.

Parallel IMPLEMENT passes `kind`, `executor`, scaffold skill, arguments,
foundation-managed paths, and the architect handoff to the worker brief. The
handoff names the accountable architect, selected starter entry, accepted
architecture, module and its evidence when present, resolved axes, accepted
deviations, and any specialist consultation. The worker dispatches the named scaffold skill inside its detached
worktree and carries those fields into its contribution receipt before packaging
the result exactly as it packages a pair candidate.

## Commit and Path Safety

Foundation source commits contain only:

- source/config/test paths declared by the Delivery Slice;
- the exact declared Stack Profile;
- the exact declared scaffold metadata file;
- the exact declared architecture-baseline subtree.

This is a narrow exception to the normal `.araia/` exclusion. It excludes
`.araia/index.md`, manifests, run state, worktrees, staging, refusal logs, and
any undeclared `.araia/**` path. The sealed file inventory remains authoritative
in parallel mode.

## Gate Obligations

G2 verifies that wave-zero seeds and accepted decisions cover deferred
foundation intent without leaking PLAN detail into SPECIFY.

G4 verifies:

- foundation slices occupy the first contiguous IDs;
- every deferred adapter has exactly one owned foundation slice;
- scaffold skill, arguments, profile, and Managed Paths are valid;
- multi-adapter output roots and Stack Profiles are non-overlapping, or
  evidence proves safe composition;
- the local DAG orders foundation-dependent product slices after their foundations;
- external or inherited strategies carry satisfiable evidence/prerequisites.

G5 additionally verifies the Stack Profiles, generator validations, commits,
and `pipeline.foundation.status`.

## Termination

The contract holds when `init` performs no scaffold write, SPECIFY
defines rather than implements the architecture, PLAN creates bounded first
foundation slices, and IMPLEMENT plus commit materialize a verified baseline
before dependent product slices become ready.
