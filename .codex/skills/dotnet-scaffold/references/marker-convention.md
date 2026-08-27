# Marker Convention

Overlays compose only through named staging markers. Ad-hoc string matching is
forbidden, because deterministic regeneration depends on an explicit insertion
contract.

| Host | Syntax |
|---|---|
| C# | `// <araia:name><$araia:name>` or an open and close pair |
| XML and MSBuild | `<!-- <araia:name><$araia:name> -->` or an open and close pair |
| JSON | No markers. Use the structural `appsettings-additions` merge |

Names match `^[a-z][a-z0-9-]*$` and are unique per file. A snippet cannot contain
a marker. The generator requires the exact target marker, substitutes the snippet
once, deduplicates identical contributions, re-indents inserted lines to the
marker's column, and aborts on a missing marker or file. It never patches
outside the declared block.

## Inventory

Markers are placed by surface, not by path, so the same inventory holds in every
topology. Overlay manifests address them through tokens documented in
[`template-placeholders.md`](template-placeholders.md).

| Location | Markers |
|---|---|
| Host `Program.cs` (`{{HostProgramFile}}`) | `di-base`, `pipeline-base`, `endpoints`, `usings-transports`, `di-transports`, `pipeline-transports` |
| Infrastructure registration (`{{ModuleRegistrationFile}}`) | `usings-persistence`, `usings-messaging`, `usings-cache`, `di-persistence`, `di-messaging`, `di-cache` |
| Application registration | `usings-module`, `di-module` |
| Endpoint registration | `module-endpoints` |
| `Directory.Packages.props` | `packages-transports`, `packages-persistence`, `packages-messaging`, `packages-cache` |
| Host and infrastructure project files | `packageref-{project-slug}-{axis}` |

`{project-slug}` lowercases the project name and replaces dots with hyphens.
`axis` is `transports`, `persistence`, `messaging`, or `cache`.

The host owns transport composition. The context that owns module infrastructure
owns persistence, messaging, and cache composition. In a modular monolith those
are the same project and the registration keys collapse onto one file, so that
file carries the union of the marker sets. In a layered topology they are
different projects and different files.

Program.cs carries no module-scoped markers. Persistence, messaging, and cache
never register at the composition root, because that would put provider packages
in the host and bypass the context that owns the technology.

## Cleanup

After every overlay applies, a cleanup pass removes each remaining marker
comment, preserves inserted content, and normalizes blank lines. Generated `.cs`,
`.csproj`, and `.props` files therefore contain no `<araia:...>` marker.

Regeneration builds a fresh staging tree. It never relies on markers surviving in
previously published output, which is why editing generated files by hand does
not corrupt a later run.
