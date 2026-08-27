# Template Placeholders

The renderer performs one case-sensitive substitution pass. Any unreplaced token
matching `{{Name}}` fails before publication, and the renderer does not rescan
its own substitutions.

## Solution tokens

| Token | Source | Example |
|---|---|---|
| `{{SolutionName}}` | normalized `--solution` | `Contoso.Billing` |
| `{{NamespacePrefix}}` | `--namespace`, or the first two solution segments joined | `ContosoBilling` |
| `{{ArchitectureId}}` | the starter's canonical architecture | `clean` |
| `{{TargetFramework}}` | validated `--framework` | `net10.0` |
| `{{TargetFrameworkVersion}}` | framework without the `net` prefix | `10.0` |
| `{{SdkVersion}}` | bundled SDK baseline for the framework | `10.0.100` |
| `{{UserSecretsId}}` | deterministic UUID5 of solution and namespace | `e2c9...` |

## Topology tokens

These resolve from the selected topology's surfaces and registrations, so an
overlay manifest never hard-codes a path or a namespace belonging to one
architecture.

| Token | Resolves to | Modular example | Clean example |
|---|---|---|---|
| `{{HostNamespace}}` | host project namespace | `ContosoBilling.Api` | `ContosoBilling.WebApi` |
| `{{HostProjectPath}}` | host project folder | `src/Platform.Api` | `src/Platform.WebApi` |
| `{{HostProgramFile}}` | host composition root file | `src/Platform.Api/Program.cs` | `src/Platform.WebApi/Program.cs` |
| `{{HostInfrastructureRoot}}` | host transport concerns folder | `src/Platform.Api/Infrastructure` | `src/Platform.WebApi/Infrastructure` |

## Module tokens

| Token | Resolves to | Modular example | Clean example |
|---|---|---|---|
| `{{ModuleName}}` | validated `--module` | `Billing` | `Billing` |
| `{{ModuleNamespace}}` | module context namespace | `ContosoBilling.Api.Modules.Billing` | `ContosoBilling.Application.Modules.Billing` |
| `{{ModuleSchema}}` | lowercase module name | `billing` | `billing` |
| `{{ModuleContextRoot}}` | context home folder | `src/Platform.Api/Modules/Billing` | `src/Platform.Application/Modules/Billing` |
| `{{ModuleFeaturesRoot}}` | slice folder | `src/Platform.Api/Modules/Billing/Features` | `src/Platform.Application/Modules/Billing/Features` |
| `{{ModuleInfrastructureRoot}}` | module technology folder | `src/Platform.Api/Modules/Billing/Infrastructure` | `src/Platform.Infrastructure/Modules/Billing` |
| `{{ModuleInfrastructureProjectPath}}` | project owning module infrastructure | `src/Platform.Api` | `src/Platform.Infrastructure` |
| `{{ModuleRegistrationFile}}` | file carrying the technology registration markers | `src/Platform.Api/Modules/Billing/BillingModule.cs` | `src/Platform.Infrastructure/Modules/Billing/BillingInfrastructureModule.cs` |

## Reserved tokens

| Token | Status |
|---|---|
| `{{ProjectName}}` | reserved per-project token |
| `{{SliceName}}` | reserved for a future slice generator |
| `{{feature-id}}` | current overlay id, scoped to that overlay's rendering |

## Empty and invalid values

Module tokens are empty without `--module`, and module-scoped overlays fail
configuration before they can render an empty module token, so a template never
produces a path with a missing segment.

Namespace derivation strips non-identifier characters from solution segments and
joins the first two; a single-segment solution uses that segment. Reserved or
invalid namespace and module names fail. The renderer never silently repairs a
name.

Starter entry manifests live under `templates/starters/<entry>/` and select a
shared template set. Alias text never reaches a template: `{{ArchitectureId}}`
receives the canonical macro architecture, and selection provenance stays in the
JSON result and the scaffold metadata.

## Adding a token

1. Add it to the renderer context with a deterministic source, resolved from the
   topology rather than a literal path.
2. Document its value per topology and its empty and validation behavior here.
3. Exercise it in the skill eval set and the scaffold contract tests.
4. Verify every overlay combination renders with no unknown token, in every
   catalog entry.
