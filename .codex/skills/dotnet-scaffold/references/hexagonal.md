# Hexagonal Architecture Profile

Select `--architecture hexagonal` when the system has several inbound or
outbound adapters around the same core and ports are stable, domain-facing
contracts. The port vocabulary pays for itself when more than one adapter
implements or consumes a port: an HTTP host plus a worker on the inbound side, a
provider plus a simulator plus a cache on the outbound side. With one web host
and one infrastructure family, prefer [`clean.md`](clean.md), and with no
demonstrated need for layer projects at all, prefer the modular monolith.

Every framework rule still applies. Contexts come from domain discovery, a
module hosts one Bounded Context, tactical patterns arrive by evidence, Domain
Events stay internal, Integration Events are versioned public contracts, and the
host fails closed.

## Generated projects

| Project | Role | References | Holds |
|---|---|---|---|
| `Platform.Core.Domain` | domain | none | Shared kernel and each context's aggregates, value objects, domain policies, and Domain Events |
| `Platform.Core.Application` | application | `Platform.Core.Domain` | Composition contract, slices, versioned contracts, and the ports each context owns |
| `Platform.Adapters.Outbound.Infrastructure` | infrastructure | `Platform.Core.Application` | Port implementations: persistence, providers, broker, cache, outbox, inbox |
| `Platform.Adapters.Inbound.Api` | host | `Platform.Core.Application`, `Platform.Adapters.Outbound.Infrastructure` | Composition root, transport concerns, endpoint registration |

```text
Adapters.Inbound.Api ──> Core.Application ──> Core.Domain
                              ^
Adapters.Outbound.Infrastructure
```

The core never references an adapter. Inbound adapters drive the core, outbound
adapters are driven by it, and both point inward.

## Layout

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
    Features/<UseCase>/
    Integration/V1/
    Ports/
src/Platform.Adapters.Outbound.Infrastructure/
  Modules/<Context>/
    <Context>OutboundAdapterModule.cs
    Persistence/ Messaging/ Caching/
src/Platform.Adapters.Inbound.Api/
  Program.cs
  Composition/
  Infrastructure/
  Modules/<Context>/<Context>InboundAdapterModule.cs
```

## Ports

`Ports/` is the only folder this topology adds over Clean, and it carries the
whole justification for choosing it. A port is a contract the core owns, written
in domain language, describing a capability the core needs or offers. It is not
a mirror of a vendor SDK.

Keep ports honest:

- Name and shape the port after the domain need, not the provider's API. The
  translation from the provider's vocabulary is the adapter's job, which is the
  anti-corruption layer in practice.
- Put the port in the context that consumes it. A port shared by every context
  is either a shared-kernel universal or a sign the boundary is wrong.
- Create a port when a real boundary exists: multiple implementations active or
  imminent, a volatile external dependency, a required test seam, or an
  independently varying business policy. Persistence for one aggregate against a
  module-owned context is not automatically a port; a concrete repository is
  still the default.
- Do not create an inbound port per use case. The handler is already the entry
  point, and inbound adapters call it directly.

An outbound port with exactly one adapter, no test seam, and no volatility is
ceremony. Delete it or keep the implementation concrete.

## Vertical slices inside the hexagon

Ports and adapters do not cancel vertical slices. A use case keeps its command,
validator, handler, logger, and response together under
`Core.Application/Modules/<Context>/Features/<UseCase>/`. The endpoint lives in
the inbound adapter and the port implementation lives in the outbound adapter,
because those are adapter responsibilities, not because the slice was split for
style.

## Registration and composition

| File | Project | Registers |
|---|---|---|
| `<Context>Module.cs` | Core.Application | Handlers, validators, domain policies |
| `<Context>OutboundAdapterModule.cs` | Adapters.Outbound.Infrastructure | Port implementations, persistence, messaging, cache |
| `<Context>InboundAdapterModule.cs` | Adapters.Inbound.Api | Endpoints, authorization, and rate-limiting policies |

That split is what keeps `Core.Application` free of transport and provider
packages. `Program.cs` scans `SolutionAssemblies.All` once for `IModule` and once
for `IEndpointModule`, so a new context needs no composition-root edit.

Adding a second inbound adapter, a worker for example, means adding a project
with role `host` semantics in its own solution entry or extending the existing
host; adding a second outbound adapter for the same port means one more class in
the outbound project and one registration line. Neither touches the core. That
is the property this topology is bought for.

## Enforced dependency rules

Generated in addition to the cross-context isolation, error-axis, and
shared-kernel budget checks every topology receives:

| Rule | Meaning |
|---|---|
| `core-domain-must-stay-technology-free` | No ASP.NET Core, EF Core, MongoDB, broker, or cache dependency in the core domain |
| `core-domain-must-not-depend-on-application-or-adapters` | The domain is the innermost layer |
| `core-application-must-stay-adapter-free` | The core never references an adapter namespace |
| `core-application-must-stay-transport-and-provider-free` | No transport or provider package inside the core |
| `outbound-adapters-must-not-depend-on-inbound-adapters` | Adapters stay independent of each other |
| `shared-kernel-must-stay-technology-free` | The shared kernel stays a pure model surface |

## Stack Profile

```yaml
architecture: hexagonal
slice-layout:
  features-root: src/Platform.Core.Application/Modules
  slice-shape: ports-and-adapters-with-vertical-slices
  file-naming: dot-suffix
```

## Operational note

Project and namespace names in this topology are long. On Windows, generate into
a short root path, because a deep module path under a long output directory can
exceed the legacy path limit during publication.
