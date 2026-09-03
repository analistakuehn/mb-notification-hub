# Quality Gates

Quality gates sit between pipeline stages and enforce minimum criteria before the pipeline advances. Each gate combines **automatic checks** (the orchestrator evaluates them by reading artifacts) with a **mandatory user approval checkpoint**.

**Core principle**: Gates are advisory plus approval. Automatic checks guide the decision, but the user always has final say (Philosophy Principle 5). Gates never auto-block without user awareness.

---

## Gate Protocol

For every gate:

1. **Run automatic checks**: Read artifacts from the completed stage, evaluate criteria
2. **Present results**: Show pass/fail per criterion with evidence
3. **User approval**: Ask the user explicitly: "Gate checks [passed/failed]. Approve advancing to [next stage]?"
4. **Record result**: Update the manifest with gate status, date, and notes

If automatic checks fail:
- Present which checks failed and why
- Suggest remediation (re-run stage, fix specific issues, and manual override)
- The user can still approve advancement (acknowledged risk)

---

## G2: SPECIFY -> Profile Resolution

**Purpose**: Confirm the lean development baseline is complete, traceable,
verifiable, and internally coherent. Apply
`shared/specify-artifact-model.md`; file count is not a quality signal.

After G2, `standard` continues to REFINE. An eligible `minimal` profile skips
standalone REFINE and folds PLAN into the SPECIFY closeout. G2 does not itself
authorize implementation; minimal must still produce canonical Delivery
Slices and pass G4.

> **Adapter-driven schema, shared artifact model**: every new SPEC uses the
> same three core artifacts: Development Specification, Implementation Map,
> and Verification Plan under `requirements/core/`. The
> active adapter's `### G2 Artifact Layout Overrides` defines stack-specific
> schema sections and conditional triggers, but MUST NOT add another fixed
> artifact. G2 accepts only this exact layout.
>
> **The three core artifacts are the only documents required to reach
> IMPLEMENT.** Every conditional family (ADR, RFC, ATA, Technical Design,
> Contracts, domain, performance, and adapter families) is included punctually,
> by request or accepted policy. A fired trigger obliges the *content*, which
> defaults to a section or register row inside the Development Specification.
> No G2 check below may demand a separate file for a family the register
> resolved as `inline`. Apply `shared/specify-artifact-model.md`, "Conditional
> Families".
>
> **Multi-adapter composition** (`manifest-version: 5.2.0`): the primary owns
> the shared core. Contributors write only applicable stack supplements under
> `requirements/adapters/{name}/`; they do not duplicate the three core
> artifacts. The cross-adapter pass verifies every declared seam against the
> shared capability, contract, implementation seed, and verification coverage.

| Check | Criteria | How to Evaluate |
|-------|----------|-----------------|
| Development Specification exists | One merged product-and-engineering baseline conforms to the adapter schema | Require exactly `{requirements-dir}/core/01-development-specification.md` |
| Development coverage complete | Supported problems, goals, journeys, capabilities, acceptance, engineering rules, NFRs, constraints, risks, applicability, and decision registers are present and coherent | Apply `shared/specify-artifact-model.md`, `shared/product-traceability-protocol.md`, and the adapter's `engineering-schema-check` |
| Unknown information omitted | No durable requirement artifact contains `UNKNOWN`, unresolved sentinels, placeholder-only rows, or Open Questions sections | Run `node ./.agents/araia/scripts/check-document-unknowns.mjs {requirements-dir}` and require exit zero |
| Unknown-information policy applied | Durable artifacts omit unsupported non-blocking values; any mandatory gap paused publication instead of becoming a placeholder; resolved values carry provenance | Apply `shared/unknown-information-policy.md` and inspect the resolution receipt when `--resolve-unknowns` was used |
| Decision coverage resolved | Every material choice is recorded in the decision register, covered by an accepted decision, or explicitly deferred with owner and revisit condition | Apply `shared/specify-decision-package.md`. Zero ADR files is always valid: a durable decision recorded inline satisfies this check with the same required fields |
| Decision records honest and complete | Every accepted decision records evidence, alternatives, objections, consequences, enforcement, and revisit condition, whether it sits in the register or in an ADR file | Apply the Gate Invariants in `shared/specify-decision-package.md` to both dispositions; resolve reciprocal links; reject fabricated meetings or consensus; never require an ADR, RFC, or ATA file by default |
| Design coverage resolved | When the adapter's design trigger fires, the implementation shape is covered in the Development Specification's target state and affected boundaries, or in an `included` Technical Design; otherwise the register records evidence-backed `not-applicable` | Read the adapter's `design-trigger-check`, then check the declared disposition: `inline` validates the section, `included` validates artifacts under `{requirements-dir}/conditional/design/` |
| Contract coverage resolved | Every public or cross-boundary surface is covered, in the Development Specification's contract-surface summary or in `included` Contracts; otherwise the register records evidence-backed `not-applicable` | Compare the surfaces the adapter's `contracts-coherence-source` reports against the declared disposition. A surface that appears in neither place fails, whatever the disposition claims |
| Implementation Map exists | Lean seed map exists and contains outcomes, requirement links, owners, dependencies, waves, and risks | Require `{requirements-dir}/core/02-implementation-map.md`; reject story points, task hours, TDD task decomposition, final file lists, or per-Delivery Slice DoR/DoD owned by PLAN |
| Greenfield foundation seeded | Every `pipeline.foundation.strategy: deferred-scaffold` adapter has exactly one `foundation: true` wave-zero seed; no canonical `SLICE-NNN`, final tasks, estimates, final files, or acceptance wording leaked into SPECIFY | Apply `shared/foundation-slice-contract.md`; skip for `existing`, verify the owner prerequisite for `inherited`, and record missing external evidence for `external` |
| Verification Plan exists | Compact requirement-to-evidence matrix and global gates are present | Require `{requirements-dir}/core/03-verification-plan.md`; do not accept a Quality Strategy substitute |
| Included artifacts well-formed | Each `conditional/*/` subdirectory that the register marks `included` contains at least one file; a directory exists only for an `included` family | Glob for `{requirements-dir}/conditional/*/`, verify each has >= 1 `.md`, and reject a directory whose family is `inline` or `not-applicable` |
| Applicability register complete | Every canonical conditional family carries exactly one disposition: `not-applicable` with evidence, `inline`, or `included` with links | Apply `shared/specify-artifact-model.md` "Conditional Families", `shared/specify-applicability-defaults.md`, and adapter-specific trigger protocols. No family is required to be `included`, and no separate applicability artifact is created |
| Product traceability complete | The Development Specification declares `PROB`, `GOAL`, `JRN`, `CAP`, and `PAC` IDs with no orphan coverage or unexplained dependency cycles | Apply `shared/product-traceability-protocol.md` |
| SPECIFY visuals are selective and valid | Normally 0-2 new SVGs; every SVG is justified, referenced, valid, faithful, and non-duplicative; any excess has a recorded complexity rationale | Apply `shared/stage-visual-enrichment.md` |
| Development -> conditionals coherence | Every triggered design, contract, decision, performance, domain, privacy/security, or migration record traces to a capability, rule, NFR, risk, or decision candidate | Cross-reference the Development Specification against its own inline records and every `included` artifact |
| Development -> Verification coherence | Every domain rule, NFR, acceptance criterion, and applicable contract criterion maps to an observable, oracle, verification level, gate, and owner | Parse the Verification Plan coverage matrix; require zero uncovered mandatory rows |
| Development -> Implementation coherence | Every capability that requires implementation maps to at least one seed; every seed maps back to requirements; dependencies are acyclic | Parse the Implementation Map and product capability graph |
| Portfolio declarations coherent | When `manifest.portfolio` is present, every hard external dependency names an owner and evidence; every provided contract has SemVer + readiness; feature/capability IDs use the canonical prefixes | Run `validate-manifest.py` on the manifest. This checks local shape only; provider resolution remains a portfolio graph concern at G4. |
| Consistency review passed | When the adapter's SPECIFY skill dispatches a consistency reviewer at end-of-flow, the reviewer reports no critical issues (mandatory in adapters that declare `consistency-review: required`; informational otherwise) | Parse the SPECIFY skill's consistency-review report (path declared by the adapter) for critical findings; skip silently when the adapter does not declare a reviewer |
| Platform Architecture package ready | When `initiative-kind: platform-architecture`, PA1 to PA5 are approved, the seven-file dossier is complete, sources are classified, options are viable, blocking validations are dispositioned, and the primary adapter's design/contracts are linked | Apply `shared/platform-architecture-flow.md`; skip for `product` |
| Minimal-profile eligibility recorded | When `delivery-profile: minimal`, all eligibility checks have evidence and every escalation trigger is resolved; otherwise the effective profile is `standard` | Apply `shared/delivery-profile-contract.md` |
| **User approval** | User explicitly approves | AskUserQuestion |

**On failure**: list missing outcomes or coherence gaps, not merely missing
directories (for example, "NFR 'LCP < 2.5s' has no oracle or gate in the
Verification Plan"), and suggest re-running SPECIFY or editing the affected
artifact.

**On pass: Artifact Status Update**
When G2 passes, the orchestrator MUST update the `**Status**:` field in every artifact under `{requirements-dir}/core/*.md` and `{requirements-dir}/conditional/**/*.md`:
- Development Specification, Implementation Map, Verification Plan, and
  triggered non-decision artifacts: `PROPOSED` / `DRAFT` -> `APPROVED`
- ADRs: `PROPOSED` -> `ACCEPTED`; RFCs, when present: `IN REVIEW` ->
  `ACCEPTED`; and ATAs, when present, remain `RECORDED`.
- Other conditional artifacts (for example, Glossary, Performance Strategy, Domain Discovery, Test Case Catalog, and Breaking Changes), when present: `PROPOSED` / `DRAFT` → `APPROVED`

This is a batch find-and-replace of `**Status**: PROPOSED` and `**Status**: DRAFT` to `**Status**: APPROVED` across all files in `{requirements-dir}/core/` and `{requirements-dir}/conditional/`.

---

## G3: REFINE -> PLAN

**Purpose**: Confirm the refinement validates requirements against the codebase and addresses critical gaps.

| Check | Criteria | How to Evaluate |
|-------|----------|-----------------|
| Refinement report exists | The adapter's declared refinement artifact exists (a consolidated report or one self-contained Tech Refinement Artifact) | Check the adapter stage mapping's REFINE output patterns |
| No unresolved critical findings | All CRITICAL findings addressed or acknowledged | Parse for CRITICAL items without resolution |
| Risk mitigations defined | All HIGH risks have mitigation plans | Parse risks section for unmitigated HIGHs |
| Requirements impact dispositioned | Every refinement finding states either `no requirements change` or names the requirement update that must occur before G3; a read-only refinement skill never silently edits approved requirements | Cross-check the refinement artifact, requirements diff, and user attestation |
| REFINE visuals are selective and valid | 0-2 new SVGs; no redraw of an equivalent SPECIFY view; every SVG is referenced, XML-valid, link-valid, and faithful | Apply `shared/stage-visual-enrichment.md` to the refinement tree |
| Outstanding reconciliations resolved | Latest entry per `from`-family in `pipeline.reconciliations` is either `verdict: ALIGNED` OR carries a `resolved-at` timestamp | Parse `pipeline.reconciliations` (see `commands/gate.md` for the exact evaluation rule). Skip silently when the list is absent (no reconcile has run). |
| Platform design conforms | For Platform Architecture initiatives, the primary adapter's design and contracts conform to the approved standard and cover security, operations, migration, and brownfield impact | Apply the G3 extension in `shared/platform-architecture-flow.md` |
| **User approval** | User confirms ready for planning | AskUserQuestion |

**On failure**: List unresolved critical findings, suggest updating requirements before proceeding. When the failure is "Outstanding reconciliations resolved", point the user at the unresolved reconciliation file(s) and suggest re-running `/araia reconcile --from <family>` after fixing the affected artifacts (a fresh `ALIGNED` run auto-resolves the prior entries). Alternatively, accept OVERRIDE with explicit acknowledgment that the spec advances with known drift.

**On pass: Artifact Status Update**
If requirements change during REFINE (per the refinement checklist), the updated artifacts retain `APPROVED` status. No additional status transitions occur at G3 because the artifacts already hold `APPROVED` status from G2.

---

## G4: PLAN -> IMPLEMENT

**Purpose**: Confirm the backlog is well-formed and ready for implementation.

> **Adapter-driven backlog shape**: the task-breakdown structure and the effort unit vary by adapter (the dotnet Delivery Slice is not the React Delivery Slice). The orchestrator MUST read the active adapter's `### G4 Backlog Shape Overrides` block in `./.agents/araia/adapters/{adapter}/adapter.md` before evaluating G4. The block declares: (a) what the adapter recognizes as a task breakdown (markers, section headers, or task-table shape); (b) the effort unit and its accepted range. When the override block is missing, fall back to the generic checks below.
>
> **Multi-adapter composition** (`manifest-version >= 5.0.0`): the backlog is a single merged set, but the orchestrator validates each Delivery Slice against **its owning adapter's** `### G4 Backlog Shape Overrides` (resolved from the Delivery Slice's `## Metadata` `Adapter` row). The global checks (no circular dependencies, provenance) stay global and additionally assert every Delivery Slice's `Adapter` names a participating adapter from the manifest's `adapters[]`. A Delivery Slice whose `Adapter` is absent defaults to the primary.

| Check | Criteria | How to Evaluate |
|-------|----------|-----------------|
| Delivery Slices generated | >= 1 Delivery Slice file exists | Glob for `{backlog-dir}/SLICE-*.md` or `{PREFIX}-*.md` |
| Delivery profile coherent | PLAN execution is `standalone` for standard or `folded` only for a resolved minimal profile; both shapes produce the same canonical backlog contract | Compare manifest profile resolution, PLAN `execution-mode`, and `shared/delivery-profile-contract.md` |
| Implementation Map covered | At least one Delivery Slice represents each approved map seed, or an explicit fold/split/supersede mapping covers the seed; no seed silently disappears | Parse the approved map roster and each Delivery Slice's provenance; verify complete many-to-many coverage rather than requiring equal file counts |
| Foundation slices ordered | Deferred foundation seeds become the first contiguous Delivery Slice IDs beginning at `{PREFIX}-001` (default `SLICE-001`), one independently committable slice per owning adapter | Apply `shared/foundation-slice-contract.md`; verify primary/secondary/cross-cutting order and reject a mixed-adapter foundation megaslice |
| Foundation executors valid | Every scaffold-routed foundation has `Kind: foundation`, `Executor: scaffold`, the owning adapter's declared scaffold skill, decision-aligned arguments, a Stack Profile path, and bounded Managed Paths; multi-adapter output/profile paths do not overlap unless safe composition is evidenced | Parse `## Scaffold Execution`, validate flags against the scaffold Input Contract, compare managed roots and profile paths, and reject `Executor: scaffold` on a product slice |
| Foundation dependencies complete | Every product slice depends on each foundation surface it requires; external/inherited strategies carry resolvable prerequisites | Compare owning adapters, file surfaces, local DAG edges, and portfolio prerequisites; do not force unrelated slices to depend on every foundation |
| No circular dependencies | Dependency graph is acyclic (DAG) | Parse dependencies from all Delivery Slice files, detect cycles |
| Portfolio graph valid | Typed cross-SPEC dependencies have no hard cycle, orphan target, ambiguous provider, invalid readiness, or incompatible hard contract version | Run `validate-portfolio-graph.py --project-root {project-root} --allow-blocking`; render its exact JSON findings. A known readiness gap is not a malformed backlog: it remains a per-Delivery Slice scheduler blocker. |
| External blockers explicit | Every Delivery Slice seed affected by an unresolved hard portfolio relation records the external prerequisite and its evidence; unrelated Delivery Slices remain eligible | Compare the SPEC context slice with Delivery Slice provenance/dependencies. Do not synthesize a local Delivery Slice dependency edge solely to represent an external prerequisite. |
| Acceptance criteria present | Every Delivery Slice has >= 1 acceptance criterion | Parse Delivery Slice files for acceptance criteria section |
| Evidence contracts complete | Every Delivery Slice emits v2 with an `## Evidence Contract` row per acceptance criterion and an `## Quality Obligations` row per applicable G5/G6 obligation | Read PLAN `GATE-SUMMARY`; validate criterion/oracle and quality-obligation coverage against `shared/implementation-evidence-contract.md`. |
| Task breakdown present | Every Delivery Slice has a task breakdown the adapter recognizes | Parse each Delivery Slice for the structure declared by the adapter's `task-breakdown-check` (dotnet: an Azure DevOps task table; other adapters: their own TDD-phase markers or task list) |
| Effort documented | Effort documented per the adapter's unit | Sum effort per the owning adapter's `effort-unit`; for dotnet, every task uses exactly `2 SP = 8h`, `3 SP = 16h`, `5 SP = 24h`, or `8 SP = 40h`, and work above `8 SP / 40h` is split |
| Provenance present | Every Delivery Slice references at least one requirement AND at least one downstream artifact | Parse each Delivery Slice's `## Provenance` block for a source spec, an ERS requirement title, and a downstream artifact name |
| PLAN visuals are selective and valid | 0-2 new SVGs; dependency/critical-path and wave views only when their triggers match; no per-Delivery Slice decoration; every SVG is referenced and valid | Apply `shared/stage-visual-enrichment.md` to the backlog tree |
| Platform operating model covered | For Platform Architecture initiatives, Delivery Slices cover applicable service, contracts, automation, observability, documentation, onboarding, migration, adoption, and fitness functions | Apply the G4 extension in `shared/platform-architecture-flow.md` |
| **User approval** | User approves backlog | AskUserQuestion |

**On failure**: List malformed Delivery Slices, surface the specific gap (missing task breakdown, undocumented effort, or a Delivery Slice whose Provenance block omits a requirement or downstream artifact), and suggest regenerating with the planning skill.

---

## G5s: Delivery Slice Quality Checkpoint (inside IMPLEMENT)

**Purpose**: Confirm that one Delivery Slice is adherent to its specification
and meets the quality obligations it owns, at the moment it finishes, and seal
its surface so a later Delivery Slice cannot silently regress it.

`G5s` is a gate inside IMPLEMENT, not a stage boundary. It runs once per
Delivery Slice, after its required `L3-slice` sensors pass and before the
canonical Delivery Slice commit finalizes it as `completed`. Gate numbering is
unaffected: `G5` and `G6` keep their meaning.

Apply `shared/slice-quality-checkpoint.md` in full. It owns the checkpoint
composition, the slice assessment boundary, the sealed surface, the freshness
sweep, and the targeted recheck.

| Check | Criteria | How to Evaluate |
|-------|----------|-----------------|
| Slice sensors green | Every required `L3-slice` sensor passed on the state the checkpoint will assess | Apply `shared/implementation-validation-pyramid.md`; a worktree result never survives integration |
| Adherence confirmed | Every acceptance criterion, referenced ADR decision, applicable design contract, and declared API surface is met, with `file:line` evidence | Run the `check` Delivery Slice flow (`commands/check.md`); require verdict `ALIGNED` |
| Narrow EQI meets threshold | Every `ASSESSED` criterion and the narrow aggregate reach the effective `MIN_SCORE` within the slice assessment boundary | Invoke the owning adapter's quality skill with `--slice-id`; apply `shared/eqi-scoring-contract.md` for the threshold. An adapter without slice scope records `ADAPTER-SLICE-SCOPE-UNSUPPORTED` and the checkpoint degrades to adherence only |
| No attributable blocker | No blocker or hard-gate failure is attributable to this Delivery Slice | Parse the narrow ledger; pre-existing debt outside the boundary is context, a regression the slice introduced is `ASSESSED` |
| No tautological oracle | Every acceptance-criterion oracle inside the boundary names a concrete production change that would make it fail | Apply `shared/test-tautology-rules.md` to the tests the Delivery Slice added or changed. A rejected shape fails the checkpoint; the repair is a real oracle, not an added assertion on the same value |
| Test effectiveness meets declared threshold | When the Verification Plan declares a mutation target that this Delivery Slice touched, the recorded score meets the declared threshold | Apply `shared/test-effectiveness-sensor.md`; require a current run for the touched target. `not-applicable` needs Verification Plan evidence; `blocked` is a mandatory stop with the score, the threshold, and the surviving mutants |
| Refactoring triggers dispositioned | Every trigger that fired is repaired, recorded as a backlog candidate, or escalated | Apply `shared/refactoring-triggers.md` against the Delivery Slice `loop_health` counters and static-analysis findings. A fired trigger with no disposition fails the checkpoint; a trigger below its minimum sample is recorded, not fired |
| Surface sealed | The seal records every evidence-anchored input with its digest and its anchoring criterion IDs, and its fingerprint recomputes | Run `slice-quality-seal.py seal`; a path inside the boundary that is absent from the surface is `SURFACE-INCOMPLETE` |
| **User approval on failure** | A `failed` checkpoint is a mandatory stop offering `Fix now` / `Override and continue` / `Cancel` | AskUserQuestion. `auto-recommended` must not auto-select past it. An override requires a non-empty reason and records the exact failing criterion IDs |

**On pass**: write the seal, mirror it into
`pipeline.stages.IMPLEMENT.slices.{SLICE-ID}.quality`, append the run to
`pipeline.adherence-checks`, and continue to the canonical Delivery Slice
commit. After the commit lands, run the sweep so every other seal in the SPEC
is re-evaluated against the new HEAD.

**On failure**: render the failing criteria with their evidence and the three
options above. `Fix now` returns the Delivery Slice to its executor with the
failing criteria as the correction brief and leaves it `in_progress`.

**On stale**: a previously sealed Delivery Slice that the sweep marked `stale`
is repaired by a targeted recheck of its `stale-criteria` only, never by a
SPEC-wide re-analysis.

---

## G5: IMPLEMENT -> VERIFY

**Purpose**: Confirm complete implementation of all Delivery Slices and a healthy codebase.

| Check | Criteria | How to Evaluate |
|-------|----------|-----------------|
| All Delivery Slices completed | Every Delivery Slice in backlog marked as done | Check Delivery Slice completion status in manifest |
| Slice checkpoints sealed and fresh | Every source-contributing Delivery Slice has a seal whose status is `passed` or `passed-with-override` and whose freshness is `fresh` | Run `python ./.agents/araia/scripts/slice-quality-seal.py summary --project-root {project-root} --spec-id {SPEC-ID} --expect-slice ...` and require exit zero. Apply `shared/slice-quality-checkpoint.md`. A missing seal fails G5. Surface every override with its reason so the user decides with full information |
| Foundation materialized | Every planned foundation slice is committed, every declared Stack Profile exists and passed adapter validation, and `pipeline.foundation.status` is `materialized` | Compare foundation slice metadata/SHAs, `traceability.stack-profiles`, generator validation evidence, and `shared/foundation-slice-contract.md` |
| Build succeeds | Project builds without errors | Run the build command of **every adapter that contributes source** (skip cross-cutting adapters with no build). PASS iff all pass; the failure report names the breaking adapter. |
| Tests pass | Full test suite passes | Run the test command of **every source-contributing adapter**; aggregate. PASS iff all pass. |
| Acceptance evidence complete | Every Delivery Slice has a valid Evidence Contract and all criteria are `boundary-verified` | Parse the Delivery Slice evidence ledger and durable IMPLEMENT progress. Require `criteria-boundary-verified == criteria-total`; a missing or invalid Evidence Contract fails the gate. |
| Quality obligations complete | IMPLEMENT verifies every applicable G5/G6 quality obligation it owns; every `not-applicable` or `rollout-only` disposition has approved Verification Plan evidence | Aggregate each Delivery Slice's `quality-obligations.json`; reject open, blocked, silently deferred, or reclassified G6 targets per `shared/implementation-evidence-contract.md`. |
| Risk evaluations closed | No required independent implementation evaluation remains pending, refuted, or uncertain | Aggregate `shared/risk-based-implementation-evaluation.md` verdicts; require `required-evaluations-open: 0`. |
| SPEC boundary passes | Required `L4-spec` sensors pass on the canonical SPEC branch | Execute `shared/implementation-validation-pyramid.md` for every source-contributing adapter and declared cross-adapter boundary; cached Delivery Slice/worktree results do not satisfy this check. |
| Assurance receipt classified | A current G5 receipt records provenance, source fingerprint, evidence, risks, sensors, quality obligations, overrides, drift, and eligibility reasons | Write `.araia/runs/{SPEC-ID}/IMPLEMENT/assurance.json` after canonical-branch validation and apply `shared/implementation-assurance-contract.md`. Ineligibility does not fail G5; it forces full EQI in `auto`. |
| **User approval** | User confirms ready for verification | AskUserQuestion |

**On failure**: List incomplete Delivery Slices, show per-adapter build/test failures (naming which adapter broke), and suggest continuing implementation. When the failing check is "Slice checkpoints sealed and fresh", name each affected Delivery Slice, its `stale-cause`, and its `stale-criteria`, then suggest the targeted recheck for exactly those Delivery Slices. Do not propose a SPEC-wide re-analysis: bounded repair is the reason the seal exists.

> **Multi-adapter composition** (`manifest-version >= 4.0.0`): build/test run once per source-contributing adapter, each over its own slice; the gate is the conjunction. Cross-cutting adapters with no build command (e.g. `devops` infra-only) skip build/test but run their infra-validation command, when declared, in the same fan-out.

---

## G6: VERIFY -> DELIVER

**Purpose**: Confirm code quality meets defined standards before delivery,
either by revalidating complete IMPLEMENT evidence or by running numeric EQI.

Resolve `verification-mode` and route per
`shared/implementation-assurance-contract.md`. `auto` uses implementation
assurance only when the G5 receipt remains eligible; otherwise it records the
fallback reasons and runs full EQI in `delivery` assessment mode.

Exception: after a completed EQI-remediation IMPLEMENT generation, resolve the
state-derived `eqi-remediation-recheck` route first, per
`shared/eqi-remediation-recheck.md`. It reassesses only the immutable
remediation criterion roster, carries every other baseline row forward, and
recalculates raw dimensions/EQI. An unsafe baseline or scope is `F-GATE`.
Post-remediation VERIFY must never silently replace this route with full EQI.

For every numeric route, blockers and vetoes affect the verdict without capping
or rewriting the raw score.

A remediation recheck also reports `remediation-status` from
`shared/eqi-remediation-state.md`. It is loop control, not a gate result:
`CONVERGED` states that no open `code`-track blocker remains, so another
remediation backlog cannot move the score. G6 still fails while any blocker or
hard gate stands. Never read `CONVERGED` as approval, and never hold the
remediation loop open for a blocker that only an external measurement or a
human sign-off can close; those belong to the obligation register.

| Check | Criteria | How to Evaluate |
|-------|----------|-----------------|
| Verification result valid | Publish exactly one result type: current eligible `implementation-assurance`, `full-eqi`, or state-authorized `eqi-remediation-recheck` | Revalidate the assurance receipt/fingerprint, parse the full ledger/report, or validate the recheck receipt, exact target/replacement equality, baseline fingerprint, merged ledger, and selected-slice HEAD. Assurance never carries an EQI score. |
| EQI meets threshold | For `full-eqi` and `eqi-remediation-recheck`, raw aggregate EQI and every merged `ASSESSED` criterion >= effective `MIN_SCORE` (default: 9.0) | Apply `shared/eqi-scoring-contract.md`; for recheck, only target rows are new analysis and all other rows must be marked `carried-forward`. Blockers/vetoes never cap raw score. **Multi-adapter**: use strictest-wins. Skip numeric check for assurance. |
| No blockers or hard-gate failures | Full EQI/recheck has zero blockers and current hard gates PASS; assurance has no failed receipt condition or hard gate | Parse the typed result. For recheck, distinguish rechecked from carried-forward blockers and require current evidence for unknown/affected hard gates. **Multi-adapter**: conjunction across every source contributor and cross-adapter seam. |
| Quality targets met | Verification evidence demonstrates satisfaction of every applicable G6 criterion from the SPECIFY Verification Plan | For assurance, cross-reference receipt evidence; for full EQI, use assessed criteria; for remediation recheck, use current target evidence plus carried-forward baseline rows and refreshed G5/hard-gate evidence. |
| Test effectiveness current at SPEC scope | Every Verification Plan mutation target assigned to `L4-spec` has a current run at or above its declared threshold on the canonical SPEC branch | Apply `shared/test-effectiveness-sensor.md`. A stale run against a different source fingerprint does not satisfy the check; `not-applicable` requires approved Verification Plan evidence, and IMPLEMENT cannot downgrade the target here. |
| Platform standard verified | For Platform Architecture initiatives, conformance, contracts, quality scenarios, operations, rollback, and golden-path adoption have evidence, and no validation patch entered product history | Apply the G6 extension in `shared/platform-architecture-flow.md` |
| **User approval** | User approves for delivery | AskUserQuestion |

**On failure**: for assurance, show the ineligibility reason codes and the
evidence or drift that must be repaired; for full EQI, show the raw per-adapter
dashboard, blockers, hard-gate failures, and remediation. For a remediation
recheck, show the exact baseline/scope reason or remaining rechecked and
carried-forward blockers; do not initiate a total analysis.

> **Multi-adapter composition** (`manifest-version >= 4.0.0`): assurance is
> available only when every source-contributing adapter supports the same
> receipt version and coverage includes all cross-adapter seams. Otherwise `auto`
> fans out full EQI in `delivery` mode. Numeric thresholds compose
> strictest-wins; non-numeric hard-gate verdicts compose by conjunction.

---

## Gate Result Schema

Record gate results in the spec manifest:

```yaml
gates:
  G2-specify-to-refine:
    status: "PASS"          # PASS | FAIL | OVERRIDE
    date: "2026-02-28"
    auto-checks:
      passed: 7
      failed: 0
      total: 7
    notes: "All requirements artifacts present, consistency review passed"
  G3-refine-to-plan:
    status: "OVERRIDE"      # User approved despite automatic check failures
    date: "2026-02-28"
    auto-checks:
      passed: 4
      failed: 1
      total: 5
    notes: "One unresolved CRITICAL finding -- user acknowledged, address in PLAN scope"
```

- **PASS**: All automatic checks passed AND user approved
- **FAIL**: Gate not yet approved (blocking)
- **OVERRIDE**: Automatic checks failed but user approved advancement (acknowledged risk)
