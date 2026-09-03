---
name: dotnet-scaffold
description: "Generates and verifies a greenfield .NET foundation in the macro architecture the domain decision selected: modular-monolith (default), vertical-slice, clean, or hexagonal. Every entry ships bounded-context modules with vertical slices, a capped SharedKernel, topology-specific dependency fitness functions, separate architecture/security/unit/integration/performance validation, and transport, persistence, messaging, and cache overlays. Use after dotnet-architect approves the boundary and the entry. Not for brownfield discovery, feature implementation, or architecture approval."
allowed-tools: Read, Bash, AskUserQuestion, TodoWrite, Agent
---

# dotnet-scaffold

## Purpose

Bootstrap a compilable .NET solution from an explicit foundation brief, in the
macro architecture the domain decision selected. The catalog carries four
entries: `modular-monolith` (the recommended default), `vertical-slice` (the
same canonical topology reached through its micro-level name), `clean`, and
`hexagonal`.

The entry decides where each surface lands. It never relaxes the invariants:
one Bounded Context per module, vertical slices as the unit of change inside a
context, technology-free domain code, versioned Integration Events across
contexts, a small SharedKernel under an enforced cap, a fail-closed host, and
executable architecture and security fitness functions generated from that
topology's own declared dependency rules.

`dotnet-architect` remains accountable for domain boundaries, subdomain
classification, and architecture fit, including which entry the project adopts.
The deterministic generator owns template rendering, overlay composition,
project topology, Stack Profile construction, and verification. Pipeline mode
obeys `./.agents/araia/shared/foundation-slice-contract.md`.

## Input Contract

| Argument | Default | Contract |
|---|---|---|
| `--output <path>` | current directory | Resolve the target root; no dry run creates it |
| `--solution <Name>` | current directory name | Write `{Name}.sln`; prefer dotted PascalCase |
| `--namespace <Prefix>` | first two solution segments joined | Reject `System`, `Microsoft`, and invalid C# identifiers |
| `--framework <value>` | `net10.0` | Accept the bundled `net10.0` package set and require its SDK |
| `--architecture <value>` | `modular-monolith` | Select `modular-monolith`, `vertical-slice`, `clean`, or `hexagonal`; accept the catalog aliases `pragmatic-modular`, `pragmatic-modular-architecture`, `vsa`, `vertical-slice-architecture`, `clean-architecture`, `onion`, `hexagonal-architecture`, and `ports-and-adapters`; `vertical-slice` resolves to the canonical `modular-monolith` topology, while `clean` and `hexagonal` are canonical architectures of their own |
| `--module <Name>` | empty | Add one evidenced bounded context; reject reserved or invalid names |
| `--transports <csv>` | `minimal-api` | Accept `minimal-api,graphql`; require a non-empty value when the caller explicitly supplies it |
| `--persistence <csv>` | empty | Accept `mongo,ef,dapper`; require `--module` |
| `--messaging <csv>` | empty | Accept `rabbitmq,kafka`; require `--module` |
| `--cache <csv>` | empty | Accept `redis,hybrid-cache`; require `--module` |
| `--profile-path <path>` | `.araia/stack-profile.yaml` | Keep the path inside the output root |
| `--non-interactive` | false | Do not prompt; require explicit `--transports` |
| `--dry-run` | false | Render and lint in temporary staging, return `status: planned`, and publish no scaffold output; a refusal-log append remains a separate control-plane audit when a gate fires |
| `--force` | false | Allow an approved generated-file overwrite or changed-configuration rerun; the safe-overlap rule continues to protect control files |
| `--allow-private-feed` | false | Allow feeds approved for the current session |

Do not invent a bounded context to satisfy `--module`. Its name must come from
Event Storming, an accepted Context Map, an ADR, or an equivalent approved
domain source. Omitting `--module` creates the foundation with only
`SharedKernel` under `Modules/`; it does not invent a bounded context.
A module owns persistence, messaging, and cache overlays, so selecting them
requires `--module`.

The CLI validates the resolved module identifier and overlay scoping, not the
provenance of a domain decision. In pipeline mode, the foundation brief and
receipt carry the architect and evidence reference. In standalone mode, the
caller must give that evidence to the agent before the dry run; the agent stops
before invoking the generator when it is absent.

The in-memory foundation handoff is the mechanical receipt for this boundary:

| Field | Contract |
|---|---|
| `architect` | exactly `dotnet-architect` |
| `starter` | selected catalog entry: `modular-monolith`, `vertical-slice`, `clean`, or `hexagonal` |
| `architecture` | canonical architecture the entry resolves to: `modular-monolith`, `clean`, or `hexagonal` |
| `architecture-evidence` | accepted decision selecting the entry; required when `architecture` is not `modular-monolith` |
| `module` | accepted bounded-context name, or absent when generation includes no module |
| `module-evidence` | exact accepted Event Storming, Context Map, ADR, or equivalent source; required with `module` |
| `subdomain-class` | accepted `Core`, `Supporting`, or `Generic` classification; required with `module` |
| `selections` | resolved transport, persistence, messaging, and cache axes |
| `deviations` | accepted deviations with their decision source; omit when none exist |
| `specialist-consultation` | consulted specialty and evidence source; omit when no consultation occurred |

The target must contain no existing `.sln`, `.csproj`, or `*.cs` files unless
the user explicitly authorizes overwrite and the rerun passes `--force`. A
same-configuration rerun with unchanged generated files is idempotent and does
not require `--force`. `modular-monolith` and `vertical-slice` share one
canonical descriptor and template set, so switching between those two entries is
also an idempotent rerun. Switching to or from `clean` or `hexagonal` changes the
canonical architecture and is a different generation configuration.

The foundation contract preserves existing pipeline control files byte-for-byte,
even with `--force`: root `README.md`, `.gitignore`,
`LICENSE`, `AGENTS.md`, `CLAUDE.md`, `.github/PULL_REQUEST_TEMPLATE.md`, and
unrelated existing content under `.araia/`, `.codex/`, `.claude/`, or `docs/`.
Explicit idempotency and profile guards continue to govern the scaffold-owned
metadata and selected Stack Profile path. The JSON result reports
`preserved` for each differing staged file that the safe-overlap rule skips.

## Output Contract

Create the production projects the selected entry declares, plus the same
validation and documentation surfaces in every entry:

```text
src/...                                   projects declared by the selected entry
  <composition surface>                   IModule, IEndpointModule, discovery, assembly list
  <shared kernel surface>
  <per context>                           Domain, Features, Integration/V1, Infrastructure,
                                          registration files, AGENTS.md, hotspots.md
tests/Platform.ArchTests/
tests/Platform.SecurityArchTests/
tests/Platform.UnitTests/
tests/Platform.IntegrationTests/
tests/Platform.PerformanceTests/
docs/architecture/{adrs,standards}/
docs/security/
```

`modular-monolith` places every surface inside one `src/Platform.Api` project.
`clean` and `hexagonal` distribute them across layer or core-and-adapter
projects. Concrete trees per entry live in
[`references/architecture-layouts.md`](references/architecture-layouts.md).

Also create a deterministic `.sln`, Central Package Management, locked-package
configuration, analyzer/editor settings, selected overlays,
`.araia/scaffold-metadata.json`, and `.araia/stack-profile.yaml`. Emit exactly
one compact JSON object on stdout. The object and metadata distinguish the
selected `starter` from the resolved canonical `architecture`.

Every entry enforces authenticated fallback authorization, explicit anonymous
exceptions, rate limiting, problem details, a small SharedKernel under an
architecture-tested cap, no mediator, separate architecture and security test
projects, and the dependency fitness functions declared by that entry. It
creates infrastructure capabilities but does not pretend that an outbox, inbox,
threat model, SLO, or production authentication scheme exists before
implementation and verification.

Run `dotnet restore --use-lock-file`, `dotnet restore --locked-mode`,
`dotnet build --no-restore --warnaserror`, and
`dotnet test --no-build --no-restore`. Keep command output out of the
conversation; on failure, write it to `.araia/scaffold-run.log` and return its
path in JSON. Write the Stack Profile last and only after every verification
passes.

## Termination

Terminate successfully only on JSON `status: completed` with a verified Stack
Profile. `status: planned` is a scaffold-output-free dry run, not completion.
`status: generated-unverified` is maintenance-only and cannot satisfy a
foundation Delivery Slice.

When the caller explicitly requests only a plan or dry run, `status: planned`
is the terminal result for that request. `--non-interactive` suppresses prompts;
it does not authorize publication beyond the requested endpoint.

Stop on invalid arguments, an unevidenced/missing module that overlays require,
existing or conflicting files, overlay-graph errors, missing markers, private
feed authorization, template/language lint failures, or restore/build/test
failure. Resume a confirmation-gated stop once after approval and refusal-log
entry. Surface the script's `code`, `message`, and recovery action; do not
substitute a conversational scaffold or blind retry.

## Auto-Clarity

Standing obligation: surface every unverified generator claim, unresolved
option, and destructive effect on existing files before running the script,
never after.

Maintain the Standing Honesty Obligation from
`./.agents/araia/shared/auto-clarity-protocol.md`, the uncertainty rules from
`./.agents/araia/shared/agent-uncertainty-protocol.md`, and the retry rules
from `./.agents/araia/shared/retry-protocol.md`. Append every trigger-driven
block, question, pause, or abort to `.araia/refusal-log.jsonl`, including the
user's resolution when available.

Resolve the refusal log against the active project root, never `--output`.
Therefore, a gate must not create or mutate the scaffold target during a dry
run. If the caller also prohibits every project control-plane write, surface
that conflict and stop before invoking the generator; do not misreport the
blocked operation as a completed dry run.

1. **Safety warnings and irreversible actions**: for `existing-dotnet-files`,
   `existing-generated-files`, or `manually-edited-profile`, name affected
   files and require explicit approval before `--force`. For
   `private-feed-confirmation-required`, name every feed and require per-session
   authorization before `--allow-private-feed`.
2. **Material ambiguity**: no `--module` inference from a solution name, database,
   controller, or technical layer. Ask once when no accepted domain source
   establishes the boundary. Treat the architecture entry the same way: default
   to `modular-monolith`, and select `clean` or `hexagonal` only against an
   accepted decision, never because the caller named the pattern in passing.
   Inside any entry, introduce explicit dependency seams only when evidence
   justifies them.
3. **User visibly confused or mistaken**: relay canonical `valid` starter ids
   for an unknown overlay or architecture entry. If the requested SDK is absent, show
   detected SDKs and stop instead of changing the target framework.
4. **Multi-step sequences with cross-dependencies**: dry run, publication,
   first restore, locked restore, build, test, metadata, and Stack Profile form
   ordered gates. No partial result counts as success.
5. **Conflict with global or project rules**: accepted project ADRs and observed
   brownfield conventions override this greenfield baseline. Stop rather than
   using the scaffold to erase a project-specific architecture decision.

## Workflow

1. Resolve arguments without loading templates. Prompt only for a materially
   missing domain boundary or an authorization the script requires.
2. Dispatch `dotnet-architect` with the goal, constraints, accepted domain
   evidence, requested module, selected axes, validation expectations, and
   material risks. Require a schema-complete foundation brief containing every
   applicable field from the in-memory handoff table, including the resolved
   `starter`, canonical `architecture`, bounded-context evidence, overlay
   ownership, and any deliberate deviations. Reject a response that leaves a
   required field only in surrounding prose. For a standalone invocation,
   record the architect response and evidence source in the task handoff before
   running the CLI.
3. Consult `dotnet-specialist` only when cited SDK, package, provider,
   performance, runtime-AI, or security evidence requires depth. Return its
   evidence to the architect before generation.
4. Resolve `GENERATOR` as `scripts/scaffold.py` inside the active skill. If only
   the framework install is available, use
   `./.agents/araia/adapters/dotnet/skills/dotnet-scaffold/scripts/scaffold.py`.
   Try `python3` only when `python` is unavailable; otherwise return `F-TOOL`.
5. Invoke the generator with every resolved argument plus `--dry-run`. It loads
   and validates the starter catalog internally. Parse its one-line JSON and do
   not print the file inventory.
6. Preview solution, namespace, selected `starter`, canonical `architecture`,
   module or "no module", selected features, and file count from the JSON plan. Stop here when the
   requested endpoint is plan-only. Otherwise, ask for approval unless the
   caller supplied `--non-interactive` for a generation request.
7. Invoke the same command without `--dry-run` only after step 6 authorizes
   publication. After a confirmation-gated failure, record the decision and
   rerun once with the approved `--force` or `--allow-private-feed` flag.
8. Accept success only when JSON returns `status: completed` and the profile
   exists. In pipeline mode, require the pipeline receipt to identify the
   accountable architect and any specialist consultation; in standalone mode,
   include that attribution and the domain-evidence source in the task result.

Do not load `scripts/scaffold.py`, templates, or manifests during the normal
path. Execute the deterministic generator directly. Read a reference when the
brief raises the corresponding architectural question or the user requests its
rationale.

## Architecture Rules

These hold in every catalog entry. The entry decides placement, never whether a
rule applies.

- A module is a bounded context, not a technical layer. Its name and its
  Core/Supporting/Generic classification come from domain discovery.
- Vertical slices live inside a module and keep one use case's transport,
  structural validation, orchestration, response, and logging close together.
  Layer or adapter projects relocate the endpoint and the persistence
  implementation; they do not dissolve the slice.
- Domain invariants stay in aggregates and value objects. Handlers orchestrate;
  validators check input shape.
- Commands accept transport primitives; handlers rebuild domain value objects.
- Domain Events remain internal. Integration Events are distinct, immutable,
  versioned contracts that cross context boundaries through transactional
  outbox/idempotent inbox delivery.
- Encapsulate technology, but introduce interfaces, repositories,
  specifications, policies, ports, CQRS projections, or separate services only
  for demonstrated complexity or operational pain.
- `SharedKernel` contains only genuine universals and stays under an
  architecture-tested size cap.
- Runtime performance, eventual consistency, and AI-runtime behavior require
  measured baselines and explicit SLOs; directory shape is not evidence.
- Each entry's architecture tests enforce that entry's declared dependency
  rules, plus cross-context isolation, one error axis, and the SharedKernel cap.

## Reference Routing

All files below are direct children so a maintainer can load only the needed
contract:

| Question | Reference |
|---|---|
| Starter catalog and generated topology per entry | [`references/architecture-layouts.md`](references/architecture-layouts.md) |
| Clean entry: projects, layers, and enforced rules | [`references/clean.md`](references/clean.md) |
| Hexagonal entry: core, ports, and adapters | [`references/hexagonal.md`](references/hexagonal.md) |
| Declaring or extending a topology | [`references/topology-manifest.md`](references/topology-manifest.md) |
| Strategic DDD: bounded contexts, Context Map, subdomain class, extraction | [`references/module-conventions.md`](references/module-conventions.md) |
| Vertical-slice content and naming | [`references/slice-conventions.md`](references/slice-conventions.md) |
| Tactical DDD: entity, value object, aggregate, policy, repository, specification | [`references/tactical-patterns.md`](references/tactical-patterns.md) |
| Domain Events, Integration Events, outbox, and inbox | [`references/event-contracts.md`](references/event-contracts.md) |
| Security, tests, supply chain, and NFR gates | [`references/quality-guardrails.md`](references/quality-guardrails.md) |
| Persistence choice and module data ownership | [`references/persistence-selection.md`](references/persistence-selection.md) |
| `AGENTS.md`, hotspots, and specification context | [`references/agent-context.md`](references/agent-context.md) |
| Overlay compatibility | [`references/conflict-matrix.md`](references/conflict-matrix.md) |
| Marker insertion and cleanup | [`references/marker-convention.md`](references/marker-convention.md) |
| Deterministic overlay ordering | [`references/overlay-application-order.md`](references/overlay-application-order.md) |
| Overlay manifest schema | [`references/overlay-manifest.md`](references/overlay-manifest.md) |
| Constructed Stack Profile | [`references/stack-profile-construction.md`](references/stack-profile-construction.md) |
| Template token catalog | [`references/template-placeholders.md`](references/template-placeholders.md) |

## Script Exit Contract

| Exit | JSON code | Recovery |
|---|---|---|
| `0` | completed/planned | Continue according to `status` |
| `2` | argument/overlay error | Correct arguments; generator writes no files |
| `3` | overwrite/profile guard | Require approval before one `--force` rerun |
| `4` | private-feed gate | Require per-session feed approval before one authorized rerun |
| `5` | starter/template/tool/generation error | Fix the framework asset or tool; do not retry blindly |
| `6` | verification failure | Inspect `.araia/scaffold-run.log`; profile remains absent |

## Rules of Engagement

1. Execute the Python generator; create no generated project files in the
   conversation.
2. Validate the complete overlay graph and render to staging before publishing.
3. Preserve deterministic ordering and idempotency; no overwrite of user work
   without explicit authorization.
4. Apply the NuGet feed gate before restore and record approved private or
   unknown feeds in `.araia/scaffold-run.log`.
5. Publish `.araia/stack-profile.yaml` last with the resolved canonical
   `architecture` and that entry's slice layout.
6. Treat deterministic templates that pass `check-writing-rules.py --strict` as
   the prevalidated-template exception in
   `shared/post-write-language-enforcement.md`; do not dispatch
   `artifact-writer` for generated project output.
