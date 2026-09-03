# Stack Profile Detection Rules

This document defines the rules `dotnet-solution-inspection` uses to populate `.araia/stack-profile.yaml` from a brownfield codebase scan. Load it on demand during Step 5 (Publish Files and Stack Profile).

The profile schema and consumer protocol are documented at [`./.agents/araia/adapters/dotnet/references/stack-profile-protocol.md`](./.agents/araia/adapters/dotnet/references/stack-profile-protocol.md). When in doubt about field semantics, defer to the protocol.

---

## Detection by axis

For every axis, the analyzer scans the inputs in priority order and emits the first match. When no signal matches, the analyzer emits the documented default and adds a `# unresolved` YAML comment so downstream skills know the value is a guess.

### `target-framework`, `lang-version`, `nullable`, `implicit-usings`

| Source | Field |
|---|---|
| `*.csproj` `<TargetFramework>` (or `<TargetFrameworks>`, take the highest) | `target-framework` |
| `*.csproj` or `Directory.Build.props` `<LangVersion>` (default `latest`) | `lang-version` |
| `*.csproj` or `Directory.Build.props` `<Nullable>` (default `disable` per SDK) | `nullable` |
| `*.csproj` or `Directory.Build.props` `<ImplicitUsings>` (default `disable` per SDK) | `implicit-usings` |

### `namespace-strategy`, `root-namespace-prefix`

| Signal | Output |
|---|---|
| Csproj has explicit `<RootNamespace>` set to a prefixed form (e.g., `XRCambio.Application`) for **every** project | `prefixed`; `root-namespace-prefix` = the common prefix |
| No explicit `<RootNamespace>` and folder name is a generic `Application/`, `Services/`, etc. | `bare`; `root-namespace-prefix` = the solution short name (best-effort) |
| Mixed | `prefixed`; flag a `# inconsistent` comment |

### `central-package-management`

| Signal | Output |
|---|---|
| `Directory.Packages.props` exists with `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` | `true` |
| Otherwise | `false` |

### `architecture`

Inspect paths first, then confirm dependency direction from `<ProjectReference>` entries:

| Signal | Output |
|---|---|
| one production `src/Platform.Api/Platform.Api.csproj` with bounded contexts under `src/Platform.Api/Modules/<Context>/` | `modular-monolith` |
| `src/Services/Features/` with a web host under `src/Application/` | `vertical-slice` |
| sibling `src/Domain/`, `src/Application/`, `src/Infrastructure/`, and `src/WebApi/`, with dependencies pointing inward | `clean` |
| `src/Core/{Domain,Application}/` plus `src/Adapters/{Inbound,Outbound}/` | `hexagonal` |
| no canonical shape or a mixed/custom topology | `custom` |

When path and project-reference signals disagree, emit `custom` with `# inconsistent-architecture`; do not force the nearest named architecture.

### `mediator`

Priority order, first match wins:

| Signal | Output |
|---|---|
| `<PackageReference Include="MediatR" .../>` or `MediatR.Extensions.Microsoft.DependencyInjection` | `mediatr` |
| `<PackageReference Include="Genial.Arquitetura.MinDiator" .../>` | `minidiator` |
| `<PackageReference Include="Mediator" Version="2.*" .../>` (Martin Othamar's package; distinguish from `MediatR` by the absent `R` and version range) | `mediator-source-gen` |
| `<PackageReference Include="WolverineFx*" .../>` | `wolverine` |
| None of the above match in any csproj | `none` |

### `validation`

| Signal | Output |
|---|---|
| Any `<PackageReference Include="FluentValidation*" .../>` | `fluent-validation` |
| Otherwise | `none` |

### `error-handling`

Heuristic-based; scan `src/**/*.cs`:

| Signal | Output |
|---|---|
| Project defines a `Result<T>` (or `Result`) class/struct AND uses it as a return type in 30%+ of public methods | `result-pattern` |
| `throw new \w+Exception` count is high AND no `Result<T>` definition is present | `exceptions` |
| `Result<T>` definition exists AND `throw` is present in business logic | `hybrid` |

When the heuristic is inconclusive, emit `result-pattern` (greenfield default) with a `# unresolved` comment.

### `test-framework`

| Signal | Output |
|---|---|
| `<PackageReference Include="xunit" .../>` in any `*Tests*.csproj` | `xunit` |
| `<PackageReference Include="NUnit" .../>` | `nunit` |
| `<PackageReference Include="MSTest.*" .../>` | `mstest` |
| Multiple frameworks | take majority; flag `# mixed-test-frameworks` |

### `test-mocking`

| Signal | Output |
|---|---|
| `<PackageReference Include="NSubstitute" .../>` | `nsubstitute` |
| `<PackageReference Include="Moq" .../>` | `moq` |
| `<PackageReference Include="FakeItEasy" .../>` | `fakeiteasy` |
| None | `none` |

### `test-assertions`

| Signal | Output |
|---|---|
| `<PackageReference Include="Shouldly" .../>` | `shouldly` |
| `<PackageReference Include="AwesomeAssertions" .../>` | `awesome-assertions` |
| `<PackageReference Include="FluentAssertions" .../>` | `fluent-assertions` |
| None of the above | `xunit-builtin` |

### `test-data`

| Signal | Output |
|---|---|
| `<PackageReference Include="Bogus" .../>` | `bogus` |
| `<PackageReference Include="AutoFixture*" .../>` | `autofixture` |
| None | `none` |

### `arch-tests`

| Signal | Output |
|---|---|
| `<PackageReference Include="TngTech.ArchUnitNET*" .../>` | `archunitnet` |
| `<PackageReference Include="NetArchTest*" .../>` | `netarchtest` |
| None | `none` |

### `http-mocking`

| Signal | Output |
|---|---|
| `<PackageReference Include="RichardSzalay.MockHttp" .../>` | `mockhttp` |
| `<PackageReference Include="WireMock.Net" .../>` | `wiremock-net` |
| None | `none` |

### `transports` (list)

Append every match:

| Signal | Output entry |
|---|---|
| `<FrameworkReference Include="Microsoft.AspNetCore.App" />` AND `Program.cs` contains `app.MapGet`/`MapPost`/`MapPut`/`MapDelete` | `minimal-api` |
| `<PackageReference Include="HotChocolate.AspNetCore" .../>` | `graphql` |
| Any class inheriting `ControllerBase` or decorated with `[ApiController]` | `controllers` |
| `Microsoft.AspNetCore.SignalR.Hub` subclasses | `signalr` |
| gRPC service classes (Grpc.AspNetCore) | `grpc` |

### `persistence` (list)

| Signal | Output entry |
|---|---|
| `MongoDB.Driver` | `mongo` |
| `Microsoft.EntityFrameworkCore` (any provider) | `ef` |
| `Dapper` | `dapper` |
| `Oracle.ManagedDataAccess.*` | `oracle` |
| `Microsoft.Data.SqlClient` | `sqlserver` |
| `Npgsql`, `Npgsql.EntityFrameworkCore.PostgreSQL*`, `Pgvector*`, or `Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite` | `postgres` |

### `messaging` (list)

| Signal | Output entry |
|---|---|
| `RabbitMQ.Client` | `rabbitmq` |
| `Confluent.Kafka` | `kafka` |
| `Azure.Messaging.ServiceBus` | `servicebus` |
| `Amazon.SQS` | `sqs` |

### `messaging-consumer-pattern`

| Signal | Output |
|---|---|
| `<PackageReference Include="MassTransit*" .../>` | `masstransit` |
| `<PackageReference Include="WolverineFx.*" .../>` | `wolverine` |
| `<PackageReference Include="DotNetCore.CAP" .../>` | `cap` |
| `RabbitMQ.Client` or `Confluent.Kafka` without any of the above | `raw` |
| None | `none` |

### `cache` (list)

| Signal | Output entry |
|---|---|
| `StackExchange.Redis` | `redis` |
| `Microsoft.Extensions.Caching.Hybrid` | `hybrid` |
| `Microsoft.Extensions.Caching.Memory` (without redis or hybrid) | `memory` |

### `resilience`

| Signal | Output |
|---|---|
| `Microsoft.Extensions.Http.Resilience` | `http-resilience` |
| `Polly*` (any) without `Microsoft.Extensions.Http.Resilience` | `polly` |
| Both | `polly` (more capable) |
| None | `none` |

### `serialization`

| Signal | Output |
|---|---|
| `MemoryPack*` | `memorypack` |
| `MessagePack*` | `messagepack` |
| `protobuf-net*` | `protobuf-net` |
| Otherwise (System.Text.Json default) | `system-text-json` |

### `telemetry`

| Signal | Output |
|---|---|
| `OpenTelemetry.*` | `opentelemetry` |
| `Genial.Arquitetura.LoggerAction.ELK` (or similar ELK-shaped pin) | `elk` |
| `Serilog.*` (without OpenTelemetry) | `serilog` |
| None | `none` |

### `auth`

| Signal | Output |
|---|---|
| `Microsoft.AspNetCore.Authentication.JwtBearer` | `jwt-bearer` |
| `Microsoft.AspNetCore.Identity*` | `identity` |
| `Microsoft.Identity.Web` (Azure AD / Entra ID) | `entra-id` |
| None | `none` |

### `distributed-locks`

| Signal | Output |
|---|---|
| `RedLock.net` | `redlock-net` |
| `DistributedLock.*` (Madelson) | `distributedlock` |
| None | `none` |

### `slice-layout`

Heuristic on repo structure:

| Field | Heuristic |
|---|---|
| `features-root` | For `modular-monolith`, emit the common module parent `src/Platform.Api/Modules`; otherwise use the first directory matching `**/Features/` under `src/` (commonly legacy `src/Services/Features` or `src/Application/Features`). |
| `slice-shape` | For module-owned `Features/` folders under the canonical parent, emit `module-with-vertical-slices`. Otherwise inspect one family: `Mutations/` plus `Queries/` → `family-with-mutations-queries-graphql`; `Application/Commands/` plus `Application/Queries/` → `family-with-application-commands`; flat handler files → `flat`. |
| `file-naming` | Sample 5+ slice files. If majority follow `{SliceName}.{Role}.cs` (dots) → `dot-suffix`. If `{SliceName}{Role}.cs` (concatenated) → `concatenated`. Mixed → `mixed` plus comment. |

---

## Output format

Write to `.araia/stack-profile.yaml` at the project root with this YAML structure (mirrors [`./.agents/araia/adapters/dotnet/adapter.md`](./.agents/araia/adapters/dotnet/adapter.md) Stack Profile artifact):

```yaml
# Generated by dotnet-solution-inspection on {YYYY-MM-DD HH:MM:SSZ}.
# Edit by hand only when the analyzer's detection is wrong; add `# manually-edited: true`
# at the top to make the analyzer warn before overwriting.

target-framework: net10.0
lang-version: latest
nullable: enable
implicit-usings: enable
namespace-strategy: prefixed
root-namespace-prefix: XRCambio
central-package-management: true
architecture: modular-monolith
mediator: none
validation: fluent-validation
error-handling: result-pattern
test-framework: xunit
test-mocking: nsubstitute
test-assertions: shouldly
test-data: bogus
arch-tests: netarchtest
http-mocking: mvc-testing
transports: [minimal-api, graphql]
persistence: [mongo, dapper]
messaging: [rabbitmq]
messaging-consumer-pattern: raw
cache: [redis, hybrid]
resilience: http-resilience
serialization: memorypack
telemetry: opentelemetry
auth: jwt-bearer
distributed-locks: redlock-net
slice-layout:
  features-root: src/Platform.Api/Modules
  slice-shape: module-with-vertical-slices
  file-naming: dot-suffix
```

Lists are emitted in flow style (`[a, b, c]`) for compactness; scalars in block style. The order of keys above is canonical, preserve it on regeneration so diffs stay reviewable.

---

## Refresh policy

The analyzer runs and rewrites the profile every time it executes. Behavior on overwrite:

1. **No existing profile** → write the new file.
2. **Existing profile, no `# manually-edited` marker, content matches a fresh scan** → no-op (skip the write to keep mtime stable).
3. **Existing profile, no marker, content differs from a fresh scan** → write the new file; include a one-line diff in the Step 5.7 final summary so the user sees what changed.
4. **Existing profile WITH `# manually-edited: true` marker** → do NOT overwrite. Surface the contradiction (Auto-Clarity trigger 1) and require explicit user confirmation before overwriting. If the user confirms, drop the marker and write fresh.

The marker line `# manually-edited: true` lives on its own line near the top of the file, after the generation timestamp. The analyzer treats any presence of this exact string (case-insensitive on the value) as a hand-edit signal.
