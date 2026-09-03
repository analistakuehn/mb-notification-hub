# Overlay Manifest (`manifest.yaml`)

Every capability folder declares its deterministic composition contract. An
overlay is topology-agnostic: it addresses destinations through the tokens in
[`template-placeholders.md`](template-placeholders.md), so the same manifest
composes correctly into a modular monolith, a Clean solution, or a Hexagonal
solution.

```yaml
id: ef
axis: persistence                     # transport | persistence | messaging | cache
description: |
  Adds context-owned EF Core persistence.
requires: []
conflicts: []
packages:
  - { name: Microsoft.EntityFrameworkCore, version: "10.0.10" }
project-references:
  axis-owner:
    - Microsoft.EntityFrameworkCore
files:
  - src: files/Services/Infrastructure/Persistence/AppDbContext.cs.tmpl
    dest: "{{ModuleInfrastructureRoot}}/Persistence/{{ModuleName}}DbContext.cs"
patches:
  - file: "{{ModuleRegistrationFile}}"
    marker: di-persistence
    snippet-file: patches/program-di.snippet
appsettings-additions:
  Modules:
    "{{ModuleName}}":
      Persistence:
        Ef:
          ConnectionString: "Host=localhost;Database={{NamespacePrefix}}_{{ModuleName}};Username=postgres"
readme-section: |
  ### EF Core persistence
  ...
```

## Contract

- `id` is globally unique and belongs to exactly one declared `axis`.
- `requires` and `conflicts` reference known ids and form an acyclic requirement
  graph.
- `packages` declares central versions. `project-references` attaches package
  names to the project that owns the axis.
- `axis-owner` is a logical key, not a project name. The generator resolves it to
  the host for the transport axis and to the project named by
  `infrastructure-name` for module-scoped axes. In a modular monolith both
  resolve to the same project.
- Every `files.dest` is unique across the selected graph. Transport files target
  `{{HostInfrastructureRoot}}`; module-scoped files target
  `{{ModuleInfrastructureRoot}}`.
- Patches target an existing named marker, and a snippet cannot create a marker.
  Module-scoped registration patches target `{{ModuleRegistrationFile}}`;
  transport patches target `{{HostProgramFile}}`.
- Template sources declare namespaces relative to the overlay's logical root,
  and the generator rewrites them to the resolved surface namespace for the
  selected topology.
- `appsettings-additions` performs a structural deep merge. A key whose existing
  scalar value differs fails instead of silently winning. Secrets never appear in
  committed values; options bind with validation and fail at startup when a
  required value is absent.
- `readme-section` documents configuration, ownership, safe usage, and any
  capability the overlay deliberately does not implement.

## Scope of an overlay

An infrastructure overlay is not a business feature and not a delivery
guarantee. Messaging overlays expose broker primitives; cross-context delivery
still requires the producing context's transactional outbox, the versioned
Integration Event mapping, an idempotent inbox, the tests, and a numeric
consistency objective, as described in
[`event-contracts.md`](event-contracts.md).

A persistence overlay ships a context-owned store and its registration. It does
not decide whether a repository, a specification, or a projection is justified;
that judgment lives in [`tactical-patterns.md`](tactical-patterns.md).

## Adding an overlay

1. Declare the manifest with tokenized destinations, never a literal project path.
2. Keep the axis honest: an overlay that registers technology belongs to a
   module-scoped axis and patches the infrastructure registration.
3. Verify the overlay renders and builds in every catalog entry, not only the
   modular monolith, because a literal path passes there and fails elsewhere.
4. Add its packages to central package management through `packages`, and route
   them with `project-references`.
