# Architecture Layouts and the Starter Catalog

The generator supports several macro architectures and applies the same
non-negotiables to all of them. What changes between entries is where each
logical surface lands. What never changes is that a module hosts one Bounded
Context, that domain code stays free of technology, that contexts talk through
public contracts, and that the guardrails run as executable tests.

The recommended default remains the Pragmatic Modular Architecture: a modular
monolith at the macro level with vertical slices inside each context. Choose a
layered entry when an accepted decision calls for it, not by habit.

## Catalog

`--architecture` selects a declarative starter entry. The generator loads the
entry from its manifest; no directory is hard-coded in `scaffold.py`.

| Entry directory | Canonical id | Aliases | Resolved architecture | Template set |
|---|---|---|---|---|
| `templates/starters/modular/` | `modular-monolith` | `pragmatic-modular`, `pragmatic-modular-architecture` | `modular-monolith` | `common` |
| `templates/starters/vsa/` | `vertical-slice` | `vsa`, `vertical-slice-architecture` | `modular-monolith` | `common` |
| `templates/starters/clean/` | `clean` | `clean-architecture`, `onion` | `clean` | `common` |
| `templates/starters/hexagonal/` | `hexagonal` | `hexagonal-architecture`, `ports-and-adapters` | `hexagonal` | `common` |

`vertical-slice` is an entry point, not a rival macro architecture. Modular
monolith answers how to decompose the system; vertical slice answers how to
organize a use case inside a context. Selecting `vsa` therefore returns
`starter: vertical-slice` with `architecture: modular-monolith` and the same
canonical plan.

`clean` and `hexagonal` are distinct canonical architectures with their own
project graph, their own physical surfaces, and their own enforced dependency
rules. Selecting them changes the generated solution, the Stack Profile, and the
architecture test suite.

Every entry directory contains only `starter.yaml`. Build and editor
configuration lives once under `templates/starters/common/`, so topologies share
boilerplate without duplicating it.

The idempotency fingerprint records the canonical architecture and the template
set, not the alias or the entry id. `modular-monolith` and `vsa` therefore rerun
over one another without rewriting identical files, while switching to `clean`
or `hexagonal` is a different generation configuration.

## Modular monolith

```text
src/Platform.Api/
  Program.cs
  AssemblyMarker.cs
  Composition/                    IModule, IEndpointModule, discovery, assembly list
  Infrastructure/                 host transport concerns
  Modules/
    <Context>/
      <Context>Module.cs          registers services and maps endpoints
      AGENTS.md
      hotspots.md
      Domain/
      Features/
      Integration/V1/
      Infrastructure/
    SharedKernel/
```

One production project holds every context. Physical project boundaries are not
a prerequisite for logical ones: architecture tests, module composition,
namespaces, data ownership, and integration contracts enforce separation without
multiplying navigation. This is the lowest-navigation-cost option and the
recommended starting point.

## Clean

```text
src/Platform.Domain/
  SharedKernel/
  Modules/<Context>/              aggregates, value objects, domain policies, Domain Events
src/Platform.Application/
  Composition/                    IModule and service discovery
  Modules/<Context>/
    <Context>Module.cs            application service registration
    AGENTS.md
    hotspots.md
    Features/                     vertical slices
    Integration/V1/               versioned public contracts
src/Platform.Infrastructure/
  Modules/<Context>/
    <Context>InfrastructureModule.cs
src/Platform.WebApi/
  Program.cs
  Composition/                    IEndpointModule, discovery, assembly list
  Infrastructure/                 host transport concerns
  Modules/<Context>/<Context>EndpointModule.cs
```

The dependency direction is enforced by project references and re-asserted by
architecture tests: `WebApi` and `Infrastructure` depend on `Application`,
`Application` depends on `Domain`, and `Domain` depends on nothing but the BCL
and the shared kernel. Full detail lives in [`clean.md`](clean.md).

## Hexagonal

```text
src/Platform.Core.Domain/
  SharedKernel/
  Modules/<Context>/
src/Platform.Core.Application/
  Composition/
  Modules/<Context>/
    <Context>Module.cs
    AGENTS.md
    hotspots.md
    Features/
    Integration/V1/
    Ports/                        domain-facing contracts this context owns
src/Platform.Adapters.Outbound.Infrastructure/
  Modules/<Context>/<Context>OutboundAdapterModule.cs
src/Platform.Adapters.Inbound.Api/
  Program.cs
  Composition/
  Infrastructure/
  Modules/<Context>/<Context>InboundAdapterModule.cs
```

The core owns the ports; inbound and outbound adapters implement or consume
them, and neither may be referenced from the core. Full detail lives in
[`hexagonal.md`](hexagonal.md).

## Shared composition contract

Every topology generates the same contract, placed according to its manifest:

- `IModule` declares `ConfigureServices` and lives where modules can reference it
  without pulling in transport packages.
- `IEndpointModule` declares `MapEndpoints` and lives in the host, so an
  inner layer never gains an ASP.NET Core dependency to register a route.
- `SolutionAssemblies` lists one assembly marker per production project, and the
  host scans that list once for service registration and once for endpoints.

Where a topology collapses several registration keys onto one file, the
generator merges them into a single type that implements both contracts. That is
why the modular monolith produces one `<Context>Module.cs` while Clean produces
three focused registration files.

## Validation projects

Every topology generates the same five validation projects: architecture fitness
functions, security fitness functions, deterministic domain behavior, real
integration boundaries, and measured performance baselines. Keeping them
separate makes failures attributable and lets CI apply different execution and
promotion policies.

The architecture test suite is generated from the selected topology's declared
dependency rules, so each entry enforces its own layering rather than a shared
approximation of it.

## Choosing between them

| Situation | Entry |
|---|---|
| New system, one team, boundaries still being learned | `modular-monolith` |
| Emphasis on feature-level organization inside contexts | `vertical-slice` |
| An accepted decision requires layer projects and compile-time dependency inversion | `clean` |
| Several inbound or outbound adapters implement the same ports | `hexagonal` |

Prefer Clean over Hexagonal when there is one web host and one infrastructure
family; Hexagonal earns its extra vocabulary when multiple adapters sit on both
sides of the core. Prefer the modular monolith over both when nothing yet
demands physical layer separation, because layer projects raise the file count
per feature without adding a guarantee that architecture tests do not already
provide.

Extraction into services is a later, evidence-driven decision, not an entry in
this catalog. See [`module-conventions.md`](module-conventions.md).

## Adding a topology

A new macro architecture is a manifest, not a code change. The schema for
projects, surfaces, registrations, and dependency rules lives in
[`topology-manifest.md`](topology-manifest.md).
