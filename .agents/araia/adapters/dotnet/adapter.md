# .NET Adapter

schema-version: 3.1.0
default-composition-role: primary-capable
file-signatures: ["**/*.cs", "**/*.csproj", "**/*.sln", "Directory.*.props"]

`default-composition-role` and `file-signatures` support multi-adapter composition (see [`adapter-contract.md`](../adapter-contract.md)): the role sets the default this adapter takes when combined with others in one spec, and the signatures attribute each Delivery Slice and each file in a mixed diff to its owning adapter.

The .NET adapter is the reference pilot for adapter contract 3.1. `$araia`
owns the six lifecycle workflows; `stage-mapping.md` supplies ordered .NET
capability, agent, command, profile, template, and validator contributions.

---

## Technology Stack

The adapter targets **.NET 8.0+** (LTS). It **detects every other stack choice per project** (brownfield) or **selects it at scaffold time** (greenfield); the adapter does not assert a single fixed stack.

### Stack Detection (Brownfield)

When a project already exists, `dotnet-architect` leads `dotnet-discovery`,
which uses `dotnet-solution-inspection` to derive the
mechanical **Stack Profile** and project inventory. Canonical .NET capabilities
consume the profile rather than assuming a fixed stack.

| Source | Fields read |
|---|---|
| `*.csproj` | `<TargetFramework>`, `<LangVersion>`, `<Nullable>`, `<ImplicitUsings>`, `<RootNamespace>`, `<AssemblyName>`, `<PackageReference>` set, `<ProjectReference>` graph |
| `Directory.Build.props` / `Directory.Build.targets` | Inherited compile/build settings, common analyzer config |
| `Directory.Packages.props` | Central Package Management versions (when the project enables CPM) |
| `global.json` | SDK pin |
| `nuget.config` | Private feeds (e.g., internal Genial / Azure Artifacts) |
| `appsettings*.json` | Connection-string keys to infer persistence dependencies |
| Repo layout | Module/slice convention (for example `src/Platform.Api/Modules/<Context>/Features/`), test layout |

For a greenfield foundation, `dotnet-scaffold` writes the **Stack Profile
artifact** to `.araia/stack-profile.yaml`; `dotnet-discovery` coordinates
brownfield SPECIFY/adoption and delegates the profile write to
`dotnet-solution-inspection`. Discovery and solution inspection are
capabilities, not stages.

```yaml
target-framework: net10.0
lang-version: latest
nullable: enable
implicit-usings: enable
namespace-strategy: prefixed       # prefixed | bare
root-namespace-prefix: XRCambio
central-package-management: true
architecture: modular-monolith     # modular-monolith | vertical-slice | clean | hexagonal | custom
mediator: none                     # none | mediator-source-gen | minidiator | mediatr | wolverine
validation: fluent-validation
test-framework: xunit
test-mocking: nsubstitute          # nsubstitute | moq | fakeiteasy | none
test-assertions: shouldly          # shouldly | awesome-assertions | xunit-builtin | fluent-assertions
test-data: bogus                   # bogus | autofixture | none
arch-tests: netarchtest            # archunitnet | netarchtest | none
http-mocking: mvc-testing          # mvc-testing | mockhttp | wiremock-net | none
transports: [minimal-api, graphql] # detected via HotChocolate.* refs
persistence: [mongo, dapper]       # detected via MongoDB.Driver, Dapper + embedded *.sql
messaging: [rabbitmq]              # detected via RabbitMQ.Client
cache: [redis, hybrid]             # detected via StackExchange.Redis, Microsoft.Extensions.Caching.Hybrid
resilience: http-resilience        # polly | http-resilience | none
serialization: memorypack
telemetry: opentelemetry           # opentelemetry | elk | serilog | none
auth: jwt-bearer
distributed-locks: redlock-net
slice-layout:
  features-root: src/Platform.Api/Modules
  slice-shape: module-with-vertical-slices
  file-naming: dot-suffix          # {SliceName}.{Role}.cs (recommended) | concatenated
```

### Reference Stacks (Greenfield)

When no project exists, `/araia init` records a deferred .NET foundation.
SPECIFY accepts the architecture decisions, PLAN creates the first
`Kind: foundation` Delivery Slice, and IMPLEMENT dispatches the script-backed
`dotnet-scaffold` skill. The generator remains the sole owner of starters and
feature overlays.

**Architecture starters**: the declarative catalog carries four entries. `modular-monolith` is the default and `vertical-slice` (alias `vsa`) is the same canonical topology reached through its micro-level name: one production host at `src/Platform.Api`, bounded-context folders under `Modules/<Context>/`, vertical slices inside each module. `clean` (aliases `clean-architecture`, `onion`) and `hexagonal` (aliases `hexagonal-architecture`, `ports-and-adapters`) are canonical architectures of their own, with layer or core-and-adapter projects and their own generated dependency fitness functions. Every entry ships bounded-context modules with vertical slices, a deliberately small `SharedKernel` under an enforced cap, and five validation projects separated by purpose. The generator reports the selected starter separately from the resolved canonical `architecture`. Select a layered entry against an accepted architecture decision, not by preference.

**Feature axes** (composable; the scaffold requires at least one transport, and module-owned axes require `--module`):

| Axis | Available features |
|---|---|
| Transport | `minimal-api`, `graphql` |
| Persistence | `mongo`, `ef`, `dapper` |
| Messaging | `rabbitmq`, `kafka` |
| Cache | `redis`, `hybrid-cache` |

**Default project conventions** (greenfield only; brownfield reflects the existing project):

- **Mediator**: none: endpoints inject the handler directly; cross-cutting concerns ride on Minimal API endpoint filters or HotChocolate middleware
- **Architecture**: `/araia init` records the requested starter entry; SPECIFY
  confirms the architecture fit and the bounded-context evidence, and the
  foundation Delivery Slice materializes it. `modular-monolith` is the default,
  and standalone `/dotnet-scaffold --architecture vsa` invocations are
  output-equivalent to it. `clean` and `hexagonal` resolve to their own
  canonical architecture and require an accepted decision.
- **Validation**: FluentValidation (Scrutor scan registers each per-slice `*.Validator.cs`)
- **Test stack**: xUnit + NSubstitute + Shouldly + Bogus + coverlet, with
  NetArchTest and BenchmarkDotNet in their dedicated projects
- **Solution naming**: long dotted form (e.g., `Genial.Rentabilidade.ComDinheiro.Integration.API.sln`)
- **Project topology**: the default `modular-monolith` entry emits one
  `src/Platform.Api/Platform.Api.csproj` where modules are folders rather than
  per-layer projects; `clean` and `hexagonal` emit their declared layer or
  adapter projects. Test projects live under `tests/` in every entry
- **Module naming**: an initial module is optional and must come from Event
  Storming, an accepted Context Map/ADR, or equivalent domain evidence
- **Slice file naming**: dot-suffix per role: `RegisterCustomer.Endpoint.cs`, `RegisterCustomer.Command.cs`, `RegisterCustomer.Handler.cs`, `RegisterCustomer.Handler.Logger.cs`, `RegisterCustomer.Validator.cs`, `RegisterCustomer.Response.cs`

---

## C# Style Rules

C# style rules live in [`code-style.md`](code-style.md). Every dotnet skill and agent that generates, edits, or reviews C# must read this authoritative style contract. Topics covered: `using` directives, no `using` aliases, parameter-count limit (S107), `var` vs explicit type (IDE0007/IDE0008), no redundant type arguments on `new` (IDE0090), collection expressions (IDE0300/IDE0301/IDE0305), and range/index operators (IDE0056/IDE0057).

---

## Pragmatic Modular Architecture References

The maintained greenfield contract lives with the generator at
[`skills/dotnet-scaffold/references/`](skills/dotnet-scaffold/references/architecture-layouts.md).
It covers topology, module and slice conventions, tactical-pattern thresholds,
event contracts, persistence selection, security/quality gates, agent context,
and deterministic overlay mechanics. The executable implementation patterns for
all nine feature overlays live under
[`skills/dotnet-scaffold/templates/features/`](skills/dotnet-scaffold/templates/features/).

Every .NET agent or skill that generates, edits, or reviews code born from the
scaffold uses those references for the no-mediator modular dialect. Brownfield
source conventions, project-local ADRs, and the resolved Stack Profile take
precedence when they disagree with the greenfield baseline. Never cite a
template or reference as evidence that a capability, metric, or guarantee is
already implemented in a project.

---

## Collaboration Routing

`/araia` owns the pipeline-level collaboration policy and persists it as `pipeline.collab-mode` in each spec manifest. The .NET adapter owns the evidence rules that translate that policy into agent participation:

- `references/collaboration-routing.md`: shared `auto | solo | pair | mob` routing, scoring, planning auto mode, roster rules, and escalation.
- `references/agent-capability-matrix.md`: shared agent domains, aliases, and activation signals.

Pipeline stage mappings pass `{collab-mode}` to collaboration-aware skills. Skills keep standalone defaults, but when `/araia` invokes them, it explicitly passes the manifest policy, and they resolve `auto` with the shared routing rules.

---

## Project Detection

The adapter identifies a project as .NET when:

1. A `.sln` file exists in the working directory or parent directories, OR
2. One or more `.csproj` files exist in the working directory or subdirectories

**Detection command**:
```
Glob for **/*.sln and **/*.csproj
```

If neither exists, this adapter cannot apply.

### Brownfield Detection Signals (mode-detect for `/araia init --mode auto`)

The `/araia init` command in `auto` mode (default) reads this section to decide whether the spec is greenfield or brownfield. Signals here double as brownfield detection rules.

| Signal | Glob / check |
|---|---|
| Solution file | `Glob **/*.sln` (any match) |
| Project file(s) | `Glob **/*.csproj` (any match) |
| Source files | `Glob src/**/*.cs` (any match): weaker signal; only counts if neither `.sln` nor `.csproj` is present but `*.cs` files exist outside `obj/` and `bin/` |

Decision:
- Any signal matches -> `mode = "brownfield"`. Record `mode-detection` with the list (e.g., `.sln + 12 .csproj files`).
- No signals match -> orchestrator prompts the user to confirm greenfield. On `y`: `mode = "greenfield"`, `mode-detection = "none -- no .sln/.csproj/cs files"`.

---

## Build and Test Commands

| Action | Command |
|--------|---------|
| Build | `dotnet build --warnaserror` |
| Test | `dotnet test` |
| Framework detection | Read `<TargetFramework>` from `.csproj` |
| C# version detection | Read `<LangVersion>` from `.csproj` or `Directory.Build.props` |

---

## Skills Required

`skill-catalog.json` is the machine-readable installation source. The adapter
installs independently invocable backend workflows plus their selected .NET
platform/support capabilities. Stage identity never creates an adapter-local
skill: specification and backlog capabilities contribute bounded .NET content
while global SPECIFY and PLAN retain lifecycle ownership.

| Skill | Directory | Classification |
|-------|-----------|----------------|
| dotnet-code-review | `~/.agents/skills/dotnet-code-review/` | Three-persona parallel six-lens review capability |
| dotnet-system-design | `~/.agents/skills/dotnet-system-design/` | Architect-led system design capability; specialist consultation is evidence-driven |
| dotnet-test-driven-development | `~/.agents/skills/dotnet-test-driven-development/` | Engineer-led strict RED-GREEN-REFACTOR capability |
| dotnet-implementation | `~/.agents/skills/dotnet-implementation/` | Engineer-led bounded source implementation capability |
| dotnet-scaffold | `~/.agents/skills/dotnet-scaffold/` | Architect-led foundation brief plus deterministic generator |
| dotnet-discovery | `~/.agents/skills/dotnet-discovery/` | Architect-led brownfield technical discovery capability |
| dotnet-backlog-builder | `~/.agents/skills/dotnet-backlog-builder/` | Engineer-led technical decomposition contribution; not a PLAN facade |
| dotnet-specification | `~/.agents/skills/dotnet-specification/` | Three-persona parallel technical specification contribution; not a SPECIFY facade |
| dotnet-round-table | `~/.agents/skills/dotnet-round-table/` | Architect-mediated decision consultation with engineer and specialist |
| dotnet-solution-inspection | `~/.agents/skills/dotnet-solution-inspection/` | .NET platform solution evidence and Stack Profile capability |
| dotnet-testing | `~/.agents/skills/dotnet-testing/` | Supporting .NET test mechanics and evidence capability |
| dotnet-runtime-diagnostics | `~/.agents/skills/dotnet-runtime-diagnostics/` | .NET platform CLR, SDK, build, compiler, and runtime diagnostics |

`/araia sync` resolves each source through `skill-catalog.json`; a platform
capability can live outside the adapter directory while remaining owned by the
bound adapter in the project ledger.


## Agents Required

The adapter installs exactly three stable personas. Product remains global;
security, testing, documentation, performance, staff-engineering, and
facilitation are review lenses, capability responsibilities, or load-on-demand
specialties rather than additional permanent .NET agent definitions.

| Agent | File | Role |
|-------|------|------|
| dotnet-architect | `~/.agents/agents/dotnet-architect.md` | System design, technical discovery, scaffold foundation briefs, architecture decisions, six-lens review, and round-table mediation |
| dotnet-engineer | `~/.agents/agents/dotnet-engineer.md` | Bounded implementation, TDD, technical backlog decomposition, refactoring, documentation, specification, and six-lens review |
| dotnet-specialist | `~/.agents/agents/dotnet-specialist.md` | Read-only .NET/runtime/toolchain and evidence-activated specialty depth; mandatory specification, round-table, and six-lens review participant |

`/araia sync` parses this table and copies each required agent from `./.agents/araia/adapters/dotnet/agents/{name}.md` into the project-local `./.agents/agents/{name}.md`.

---

## Capability and Specialty Routing

Specialty knowledge remains under [`references/specialties/`](references/specialties/README.md).
`dotnet-engineer` loads implementation
bindings; `dotnet-architect` loads domain/architecture material;
`dotnet-specialist` loads a pack for evidenced depth that ordinary architecture
or engineering cannot resolve reliably. The invoking capability passes the
evidence-backed hint; stage names and keywords alone never activate packs.

| Specialty pack | File | Activation signals |
|---|---|---|
| Data persistence and caching | `references/specialties/data.md` | EF Core, Dapper, MongoDB (surface use), Redis, HybridCache, repositories, outbox/inbox |
| Strategic and tactical DDD | `references/specialties/ddd.md` | Bounded contexts, aggregates, value objects, domain events, ACL design, modular-monolith vs microservice |
| MongoDB depth | `references/specialties/mongo.md` | Project references `MongoDB.Driver` + task requires document-modeling depth (schema patterns, aggregation tuning, change streams, Atlas Search / Vector Search) |
| PostgreSQL, PostGIS, and pgvector | `references/specialties/postgresql.md` | Project references `Npgsql*`, `UseNpgsql`, `NetTopologySuite` / PostGIS, or `Pgvector*`; load with `data.md` for PostgreSQL-specific implementation depth |
| Kafka streaming | `references/specialties/kafka.md` | Project references `Confluent.Kafka`, `KafkaFlow`, or `MassTransit.Kafka` |
| RabbitMQ messaging | `references/specialties/rabbitmq.md` | Project references `RabbitMQ.Client` or `MassTransit` |
| GraphQL APIs | `references/specialties/graphql.md` | Project references `HotChocolate`, `StrawberryShake`, or `HotChocolate.Fusion` |
| Event Storming and domain discovery | `references/specialties/event-storming.md` | Domain-discovery sessions, bounded-context identification, aggregate discovery, hotspot registers |

When a capability needs ordinary library binding or source changes, dispatch
`dotnet-engineer` with the matching pack. Consult the read-only
`dotnet-specialist` when concrete evidence requires deeper runtime, SDK,
compiler, concurrency, framework/provider, performance, security, data,
messaging, API, or domain expertise. Other adapters mirror the boundary, not
the .NET names.

---

## Specialization Skills (Optional)

These skills are opt-in: no pipeline stage requires them, but they address specific quality or analysis needs. Sync prompts the user once per skill (Pin / Decline / Defer) and persists the answer in `.araia/installed.md`.

| Skill | Directory | Activation |
|-------|-----------|------------|
| dotnet-sonar-scan | `~/.agents/skills/dotnet-sonar-scan/` | Offline static analysis against a curated SonarQube rule catalog. Ingests a pre-exported `api/issues/search` JSON (the skill's `scripts/sonar_slim.py` consolidates it locally) and complements with a local Grep scan for security-critical engines. Calls no SonarQube server. |

### Organization-specific Capabilities

`dotnet-integrations-catalog` lives under
`framework/capabilities/organization/`. It contains organization-specific
Azure DevOps, environment, and endpoint conventions and is not part of the
generic .NET bundle. Install it through an organization-owned package rather
than pinning it from this adapter.

---

## Document Authoring

`technical-document-writer` reads this table for `/araia author` and for deep per-artifact quality passes from SPECIFY producers.

| Type | Template | Authoring roles | Review roles | Validator |
|---|---|---|---|---|
| `prd` | `references/templates/requirements.template.md` | Global product workflow | `dotnet-architect`, `dotnet-engineer` | product scope, metric, acceptance, and supported backend constraints |
| `engineering-requirements` | `references/templates/engineering-requirements.template.md` | `dotnet-architect`, Domain Modeler | `dotnet-engineer`, `dotnet-specialist` | architecture, domain, contract, runtime, and PRD traceability |
| `adr` | `references/templates/adr.template.md` | `dotnet-architect` | `dotnet-engineer`, `dotnet-specialist` | decision, lifecycle, invariant, feasibility, and evidence integrity |
| `rfc` | `none` | `dotnet-architect` | `dotnet-engineer`, `dotnet-specialist` | source ATA, objections, compatibility, and derived-ADR integrity |
| `technical-design` | `references/templates/design.template.md` | `dotnet-architect`, Domain Modeler | `dotnet-engineer`, `dotnet-specialist` | design-to-ADR, implementation, runtime, and contract consistency |
| `contract` | `references/templates/contracts.template.md` | API Specialist, `dotnet-architect` | `dotnet-engineer`, `dotnet-specialist` | schema, error, compatibility, security, and consumer integrity |
| `quality-strategy` | `references/templates/quality.template.md` | `dotnet-engineer`, Quality Analyst | `dotnet-specialist`, `dotnet-architect` | requirement coverage and executable gate consistency |

## Orchestrator Skill Bindings

`/araia` resolves these bindings from the active adapter when dispatching
review-style commands. A binding can be an installable skill or an internal
profile plus agent role.

| Command | Binding | Notes |
|---------|---------|-------|
| `/araia review` | `dotnet-code-review` | Always dispatches architect, engineer, and specialist in parallel across all six lenses. The global command owns approval, reports, triage, and provider effects. |
| `/araia review --fix <id>` | `dotnet-implementation` | Global review triage and approval first; then one bounded correction contribution. |
| `/araia review --verify <id>` | `references/contributions/source-review.md` + recorded criterion owner | Exact-finding read-only revalidation; it is not a complete code-review run. The global command owns optional status stamping. |
| `/araia docs` | `references/contributions/documentation-fix.md` + `dotnet-engineer` | Direct-fix documenter contribution; no separate .NET docs skill. |
| Internal technical decision escalation | `dotnet-round-table` inside global `araia:DECISION` | Architect mediates; engineer and specialist are mandatory participants. The global workflow owns approval and publication. |

When adding a review-style command to `/araia`, declare the binding here so the
orchestrator does not construct an adapter-prefixed skill name.

### Triage Panel Contract

The global `review` command owns the triage gate that pre-evaluates each
selected finding before contributors write any code. The .NET binding supplies three
role perspectives:

| Slot | Perspective | Default agent (.NET) |
|---|---|---|
| 1 | Conformity, architecture, and security | `dotnet-architect` |
| 2 | Correctness, engineering scope, and tests | `dotnet-engineer` |
| 3 | .NET/runtime feasibility, performance, and specialty evidence | `dotnet-specialist` |

Each role dispatch returns a strict JSON verdict (`accept` | `reject` |
`defer`) with confidence and rationale. The global command aggregates and
presents the recommendation. Only the accepted set becomes the bounded
correction unit that the global command delegates to `dotnet-implementation`.

Other adapters (react, flutter, …) that adopt the `--fix` flow MUST provide an analogous three-perspective panel using their own agents. A single-agent triage is not contract-compliant: the multi-perspective check is the reason `--fix` exists upstream of any code change.

The global command standardizes bypass flags: `--skip-panel` (human-only gate when external reviewers performed triage) and `--panel-only` (run the panel and emit a panel report without entering the human gate, for asynchronous review).

The global `$araia review` command owns the complete triage mechanics.

### Verify Contract

Any adapter that declares a source-review binding supports read-only
finding revalidation. The .NET binding uses the source-review profile and
classifies each selected finding as CONFIRMED RESOLVED, FALSE-RESOLVED, NOW
FIXED, or STILL OPEN; the global command owns optional stamping.

Contract requirements for any adapter that adopts `verify`:

- **Read-only by default.** The bare `verify <id>` invocation prints the classification and writes nothing. Reconciliation happens only under `--stamp`.
- **Confirmation-gated re-opening.** Lowering a finding from `resolved` back to actionable raises the verdict, so require explicit confirmation, even under `--stamp`.
- **No source edits.** `verify` confirms state; apply fixes through `fix` or by hand.
- **Tier 2, no network.** The orchestrator handles upstream PR comment thread resolution status through `/araia review --verify-threads`; the skill has no network role.

The global `$araia review` command owns the complete verification mechanics.

---

## Quality Gate Customization

| Gate | Custom Threshold |
|------|-----------------|
| G5 | Build command: `dotnet build --warnaserror` |
| G5 | Test command: `dotnet test` |
| G6 | Full EQI or targeted remediation-recheck merged aggregate and every assessed criterion >= effective `MIN_SCORE` (default 9.0); verdict APPROVED; zero blockers |
| G6 | Blockers maximum: 0 |

### G2 Artifact Layout Overrides

The orchestrator consumes these overrides at G2 evaluation (per `./.agents/araia/pipeline/quality-gates.md`).

| Key | Value |
|---|---|
| `artifact-model` | `lean-v1` from `shared/specify-artifact-model.md` |
| `fixed-artifacts` | `development-specification`, `implementation-map`, `verification-plan` |
| `conditional-artifacts` | `adr`, `design`, `contracts`, `ata`, `rfc`, `domain`, `glossary`, `performance`, `testing`, `breaking-changes` |
| `design-placement` | `conditional/design/*.md` |
| `design-trigger-check` | Multiple modules/contexts, non-trivial persistence or state, migration, security-sensitive flow, integration topology, or structural brownfield change |
| `engineering-schema-check` | Development Specification: product traceability structures plus non-empty target architecture, module responsibility, domain rules, contract-surface summary, security/privacy posture, supported NFRs, applicability register, decision register, unknown-information-policy compliance, and traceability sections |
| `implementation-map-schema-check` | Each seed has outcome, requirement IDs, owner/adapter, dependencies, wave, and applicable risk/rollout constraint; no story points, task hours, TDD task breakdown, final file list, or per-Delivery Slice DoR/DoD |
| `verification-schema-check` | Every rule, NFR, PAC, and applicable contract criterion maps to observable, oracle, verification level, gate, and owner |
| `design-schema-check` | Technical Design layout: sections numbered 3 (project topology), 5 (dependency matrix), 7 (persistence with ESR indexes), 8 (transactional outbox), 10 (threat model with concrete parameters), 12 (SLO table). Brownfield specs declare deviations in section 13 "Open Technical Decisions" with `file:line` evidence |
| `quality-coherence-source` | Development Specification domain rules, NFRs, PACs, and applicable contract criteria (each maps to the Verification Plan matrix) |
| `contracts-coherence-source` | Engineering sections `## Module Responsibility`, `## Expected Functional Slices`, `## Integration Events` (each public contract surface maps to one of these) |
| `consistency-review` | informational (the dotnet SPECIFY flow does not dispatch a consistency reviewer; G2 skips this check silently) |

### G4 Backlog Shape Overrides

The orchestrator consumes these overrides at G4 evaluation (per `./.agents/araia/pipeline/quality-gates.md`). This section declares what this adapter recognizes as a task breakdown and how it measures effort, so G4 does not assume a TDD-phase-marker or story-point shape the dotnet Delivery Slice deliberately omits.

| Key | Value |
|---|---|
| `task-breakdown-check` | An Azure DevOps task table (typed tasks, one row per task). Delivery Slices do NOT carry `[RED]`/`[GREEN]`/`[REFACTOR]` phase markers; `araia:IMPLEMENT` resolves the implementation strategy at execution time, routing `strict-tdd` to `dotnet-test-driven-development`, while `dotnet-testing` supplies reusable mechanics |
| `effort-unit` | Per-task paired story points and hours using only `2 SP = 8h`, `3 SP = 16h`, `5 SP = 24h`, or `8 SP = 40h`; split tasks above `8 SP / 40h`. Total Delivery Slice effort and story points equal the sums of its tasks |
| `provenance-check` | Each Delivery Slice carries a `## Provenance` block with a source spec, an ERS requirement title, and a downstream artifact name (see `references/templates/slice.template.md`) |

See `stage-mapping.md` and `role-mapping.md` for detailed mappings.
