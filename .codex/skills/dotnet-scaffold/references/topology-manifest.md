# Topology Manifest (`starter.yaml`)

A macro architecture is declared, not coded. The generator owns the invariants
that hold everywhere (one context per module, technology-free domain, versioned
cross-context contracts, fail-closed host, separated validation projects). The
manifest owns placement: which projects exist, where each logical surface lands,
which file carries each registration, and which dependency edges the
architecture tests forbid.

An entry that only renames an existing topology declares `canonical-architecture`
pointing at another entry and stops there. An entry that introduces a real
topology declares the full schema below.

## Header

```yaml
id: clean                          # kebab-case, unique, matches the directory intent
canonical-architecture: clean      # equal to `id` for a real topology
aliases: [clean-architecture, onion]
template-set: common               # build and editor configuration to reuse
title: Clean Architecture with bounded-context modules
host-name: Platform.WebApi         # composition root, exactly one project with role `host`
host-path: src/Platform.WebApi
infrastructure-name: Platform.Infrastructure   # owner of module-scoped overlay output
infrastructure-path: src/Platform.Infrastructure
features-root: src/Platform.Application/Modules
slice-shape: layered-with-vertical-slices
```

`infrastructure-name` receives persistence, messaging, and cache overlay
packages and files. In a modular monolith it is the host itself. `features-root`
and `slice-shape` are published in the Stack Profile.

## Projects

```yaml
projects:
  - { name: Platform.Domain, path: src/Platform.Domain, sdk: Microsoft.NET.Sdk, role: domain, namespace-suffix: Domain }
  - { name: Platform.Application, path: src/Platform.Application, sdk: Microsoft.NET.Sdk, role: application,
      namespace-suffix: Application, references: Platform.Domain, packages: "FluentValidation|Microsoft.Extensions.Options" }
```

- `references` and `packages` are pipe-separated, because a comma ends an inline
  mapping entry.
- Production roles are `host`, `domain`, `application`, `infrastructure`, and
  `library`. Exactly one project declares `host`.
- Validation roles are `arch-tests`, `security-arch-tests`, `unit-tests`,
  `integration-tests`, and `performance-benchmarks`. All five are required, and
  the generator wires their frameworks, visibility, and sources.
- Every production project receives an `AssemblyMarker`, an `InternalsVisibleTo`
  entry per validation project, and a place in `SolutionAssemblies`.
- Library roles receive their namespaces as SDK `<Using>` items rather than a
  checked-in globals file, so an unused entry never trips the unnecessary-using
  analyzer.

## Surfaces

A surface maps a logical responsibility to a folder inside one project. Quote
any value containing `{{ModuleName}}`, because an unquoted `}` terminates the
inline mapping.

```yaml
surfaces:
  composition: { project: Platform.Application, path: "src/Platform.Application/Composition" }
  endpoint-composition: { project: Platform.WebApi, path: "src/Platform.WebApi/Composition" }
  host-infrastructure: { project: Platform.WebApi, path: "src/Platform.WebApi/Infrastructure" }
  shared-kernel: { project: Platform.Domain, path: "src/Platform.Domain/SharedKernel", namespace: "SharedKernel" }
  module-context: { project: Platform.Application, path: "src/Platform.Application/Modules/{{ModuleName}}" }
  domain: { project: Platform.Domain, path: "src/Platform.Domain/Modules/{{ModuleName}}" }
  features: { project: Platform.Application, path: "src/Platform.Application/Modules/{{ModuleName}}/Features" }
  integration: { project: Platform.Application, path: "src/Platform.Application/Modules/{{ModuleName}}/Integration/V1" }
  module-infrastructure: { project: Platform.Infrastructure, path: "src/Platform.Infrastructure/Modules/{{ModuleName}}" }
```

Those nine keys are required. Additional keys are allowed and useful: the
hexagonal entry declares `ports`. Any surface whose path contains
`{{ModuleName}}` is created per module, and a module-scoped surface with no
generated content receives a `.gitkeep`.

The namespace is derived from the path relative to the owning project and the
project's `namespace-suffix`. Declare `namespace` only to override that
derivation, as the shared kernel does.

`composition` must sit where every module can reference it without acquiring
transport dependencies. `endpoint-composition` must sit in the host.

## Registrations

```yaml
registrations:
  application: { project: Platform.Application, path: "src/Platform.Application/Modules/{{ModuleName}}/{{ModuleName}}Module.cs", type: "{{ModuleName}}Module" }
  infrastructure: { project: Platform.Infrastructure, path: "src/Platform.Infrastructure/Modules/{{ModuleName}}/{{ModuleName}}InfrastructureModule.cs", type: "{{ModuleName}}InfrastructureModule" }
  endpoints: { project: Platform.WebApi, path: "src/Platform.WebApi/Modules/{{ModuleName}}/{{ModuleName}}EndpointModule.cs", type: "{{ModuleName}}EndpointModule" }
```

All three keys are required. Each carries a distinct marker set:

| Key | Markers | Contract |
|---|---|---|
| `application` | `usings-module`, `di-module` | `IModule` |
| `infrastructure` | `usings-persistence`, `usings-messaging`, `usings-cache`, `di-persistence`, `di-messaging`, `di-cache` | `IModule` |
| `endpoints` | `module-endpoints` | `IEndpointModule` |

Keys that resolve to the same path are merged into one type implementing the
union of their contracts and carrying the union of their markers. That is how
the modular monolith emits a single `<Context>Module.cs`.

The `infrastructure` registration must live in the project named by
`infrastructure-name`, because overlays patch it with technology registration
and add the matching packages to that project.

## Dependency rules

```yaml
dependency-rules:
  - { id: domain-must-not-depend-on-outer-layers, scope: "Domain", forbid: "Application|Infrastructure|WebApi" }
  - { id: application-must-stay-transport-and-provider-free, scope: "Application",
      forbid-packages: "Microsoft.AspNetCore|Microsoft.EntityFrameworkCore|MongoDB.Driver" }
```

- `id` is kebab-case and unique; it becomes the architecture test method name.
- `scope` is a namespace suffix appended to the root namespace. `*` matches one
  namespace segment, so `Api.Modules.*.Domain` covers every context.
- `forbid` lists namespace suffixes, also relative to the root namespace.
- `forbid-packages` lists external namespace prefixes.
- At least one of `forbid` or `forbid-packages` is required, and at least one
  rule must exist.

Declare the layering rules that are specific to the topology. Do not restate the
invariants the generator already emits for every entry: cross-context isolation,
the single error axis, and the shared-kernel type budget.

## Verification

A new topology is finished when it generates, restores in locked mode, builds
with warnings as errors, and passes its own tests with no module, with one
module, and with the full overlay matrix. Add the entry to the catalog table in
[`architecture-layouts.md`](architecture-layouts.md) and to the scaffold
contract tests in the same change.
