# Clean Architecture Profile

Select `--architecture clean` when an accepted decision requires layer projects
and compile-time dependency inversion, not when Clean is the familiar word. The modular monolith already protects the same boundaries with
architecture tests and costs fewer files per feature. Clean earns its extra
projects when the team wants the compiler, not a test, to refuse an inward
dependency, or when a layer must ship on its own terms.

Everything the framework requires elsewhere still applies here. Contexts come
from domain discovery, a module hosts one Bounded Context, tactical patterns
arrive by evidence, Domain Events stay internal, Integration Events are
versioned public contracts, and the host fails closed.

## Generated projects

| Project | Role | References | Holds |
|---|---|---|---|
| `Platform.Domain` | domain | none | Shared kernel and each context's aggregates, value objects, domain policies, and Domain Events |
| `Platform.Application` | application | `Platform.Domain` | Composition contract, each context's slices, versioned contracts, and application registration |
| `Platform.Infrastructure` | infrastructure | `Platform.Application` | Each context's persistence, providers, broker, cache, outbox, and inbox |
| `Platform.WebApi` | host | `Platform.Application`, `Platform.Infrastructure` | Composition root, transport concerns, endpoint registration |

```text
WebApi ─────────────┐
                    ├──> Application ──> Domain
Infrastructure ─────┘
```

Dependencies point inward only. `Domain` references nothing but the BCL. The
composition root is the single place that knows every layer.

## Layout

```text
src/Platform.Domain/
  SharedKernel/                   Result, DomainEvent, PersonalData
  Modules/<Context>/              aggregates, value objects, policies, Domain Events
src/Platform.Application/
  Composition/                    IModule and service discovery
  Modules/<Context>/
    <Context>Module.cs
    AGENTS.md
    hotspots.md
    Features/<UseCase>/
    Integration/V1/
src/Platform.Infrastructure/
  Modules/<Context>/
    <Context>InfrastructureModule.cs
    Persistence/ Messaging/ Caching/
src/Platform.WebApi/
  Program.cs
  Composition/                    IEndpointModule, discovery, assembly list
  Infrastructure/                 endpoint filters, transport defaults
  Modules/<Context>/<Context>EndpointModule.cs
```

The context name repeats in each layer. That repetition is the real cost of this
topology: one feature spans four projects instead of one folder. Keep the
context spelling identical everywhere, because the architecture tests derive
context identity from the namespace segment that follows each module root.

## Vertical slices inside the layers

Layers do not cancel vertical slices. A use case still keeps its command,
validator, handler, logger, and response together under
`Application/Modules/<Context>/Features/<UseCase>/`. Only two roles leave the
slice folder, because they belong to other layers:

- the endpoint, which maps transport input and `Result<T>` to HTTP in `WebApi`;
- the persistence implementation, which lives in `Infrastructure`.

Do not reintroduce global `Controllers/`, `Services/`, `DTOs/`, or `Mappers/`
trees inside a layer. Those are technical layers on top of technical layers, and
they produce exactly the navigation cost this framework rejects.

## Ports and adapters within Clean

A handler that needs an external capability declares the contract it wants in
its own layer and lets `Infrastructure` implement it. That is dependency
inversion applied where a real boundary exists, not an interface per class.

The abstraction rules do not relax here. A concrete repository over a
module-owned context is still the default; an interface still requires evidence
of multiple implementations, a volatile external boundary, a required test seam,
an independently varying business policy, or an accepted boundary decision. The
layer split answers where a justified abstraction lives, never whether one is
justified.

## Registration and composition

Three registration files per context, each with one job:

| File | Layer | Registers |
|---|---|---|
| `<Context>Module.cs` | Application | Handlers, validators, domain policies |
| `<Context>InfrastructureModule.cs` | Infrastructure | Persistence, messaging, cache, providers |
| `<Context>EndpointModule.cs` | WebApi | Endpoints, authorization, and rate-limiting policies |

Splitting them is what keeps `Application` free of `Microsoft.EntityFrameworkCore`
and `Microsoft.AspNetCore`. `Program.cs` scans `SolutionAssemblies.All` once for
`IModule` and once for `IEndpointModule`, so adding a context requires no edit to
the composition root.

## Enforced dependency rules

The generated architecture tests assert this topology's own rules, in addition
to the cross-context isolation, error-axis, and shared-kernel budget checks that
every topology receives:

| Rule | Meaning |
|---|---|
| `domain-must-stay-technology-free` | No ASP.NET Core, EF Core, MongoDB, broker, or cache dependency in `Domain` |
| `domain-must-not-depend-on-outer-layers` | `Domain` sees neither `Application`, `Infrastructure`, nor `WebApi` |
| `application-must-stay-transport-and-provider-free` | No transport or provider package inside `Application` |
| `application-must-not-depend-on-adapters` | `Application` sees neither `Infrastructure` nor `WebApi` |
| `infrastructure-must-not-depend-on-the-host` | Adapters do not reach back into the composition root |
| `shared-kernel-must-stay-technology-free` | The shared kernel stays a pure model surface |

Project references already prevent most of these at compile time. The tests keep
them true when someone adds a reference, and they document the intent where a
reviewer will read it.

## Stack Profile

```yaml
architecture: clean
slice-layout:
  features-root: src/Platform.Application/Modules
  slice-shape: layered-with-vertical-slices
  file-naming: dot-suffix
```

## When to prefer another entry

Choose `modular-monolith` when nothing yet requires physical layer separation:
it protects the same boundaries with fewer files per change. Choose `hexagonal`
when several inbound or outbound adapters implement the same ports and the
port vocabulary earns its keep. Extraction into services stays an
evidence-driven decision described in
[`module-conventions.md`](module-conventions.md).
