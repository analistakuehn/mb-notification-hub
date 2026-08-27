# Stack Profile Awareness Protocol

The .NET adapter operates against multiple architectural dialects (MediatR vs no-mediator vs custom mediator; FluentAssertions vs Shouldly; MassTransit vs raw consumers; Result Pattern vs exceptions). Agents and skills consult the **Stack Profile** to learn which dialect a project uses so their code, reviews, and refactors match the codebase rather than asserting a fixed opinion.

This document is the single source of truth for that mechanism. Every agent or skill that has a stack-specific opinion references this protocol.

## The Stack Profile artifact

The Stack Profile is a YAML file at `.araia/stack-profile.yaml` in the project root. The "Stack Detection (Brownfield)" and "Reference Stacks (Greenfield)" sections in [`adapters/dotnet/adapter.md`](../adapter.md) document its schema.

### Axes that drive agent behavior

| Axis | Values | Consumers |
|---|---|---|
| `architecture` | `modular-monolith`, `vertical-slice`, `clean`, `hexagonal`, `custom` | Architect, specialist, implementation, code review, discovery |
| `mediator` | `none`, `mediator-source-gen`, `minidiator`, `mediatr`, `wolverine` | Specialist, implementation, code review, discovery, global VERIFY |
| `validation` | `fluent-validation`, `none` | Specialist, code-review |
| `error-handling` | `result-pattern`, `exceptions`, `hybrid` | Specialist, architect, code-review |
| `test-framework` | `xunit`, `nunit`, `mstest` | Engineer, test-driven-development |
| `test-mocking` | `nsubstitute`, `moq`, `fakeiteasy`, `none` | Engineer, test-driven-development |
| `test-assertions` | `shouldly`, `awesome-assertions`, `xunit-builtin`, `fluent-assertions` | Engineer, test-driven-development |
| `test-data` | `bogus`, `autofixture`, `none` | Engineer, test-driven-development |
| `messaging-consumer-pattern` | `masstransit`, `raw`, `wolverine`, `cap` | Specialist (loading `specialties/rabbitmq.md` or `specialties/kafka.md`) |
| `transports` | list of `minimal-api`, `graphql`, `controllers` | Specialist, code-review |
| `persistence` | list of `mongo`, `ef`, `dapper`, `postgres`, `oracle`, `sqlserver` | Specialist (loading `specialties/data.md` plus `specialties/mongo.md` or `specialties/postgresql.md` when depth applies), code-review |
| `cache` | list of `redis`, `hybrid`, `memory` | Specialist, engineer |

## How agents read the profile

**Step 1: Locate the file.** Glob upward from the working directory for `.araia/stack-profile.yaml`. The first match wins.

**Step 2: If found, parse and trust.** The profile is authoritative. Agents adapt their architectural opinion to the values verbatim.

**Step 3: If missing, scan.** Run the fast stack-detection sweep from
`dotnet-solution-inspection`:

- `*.csproj` `<PackageReference>` → infer mediator (`MediatR.*` → `mediatr`, `Genial.Arquitetura.MinDiator` → `minidiator`, `Mediator` → `mediator-source-gen`, `WolverineFx.*` → `wolverine`, none of the above → `none`).
- Project paths and references → infer architecture (`src/Platform.Api/Modules` in one production project → `modular-monolith`; legacy `src/Services/Features` → `vertical-slice`; domain, application, infrastructure, and web-host projects layered by dependency, such as `src/Platform.{Domain,Application,Infrastructure,WebApi}` → `clean`; core projects plus inbound and outbound adapter projects, such as `src/Platform.Core.*` with `src/Platform.Adapters.{Inbound,Outbound}.*` → `hexagonal`; otherwise `custom`).
- `Directory.Packages.props` for central pins.
- Test csproj packages → infer test stack (`Shouldly` → `shouldly`, `FluentAssertions` → `fluent-assertions`, etc.).
- File patterns (`AppDbContext.*.cs` partials → `mediator: none + ef`; `[ExtendObjectType]` decorators → `transports: graphql`).

**Step 4: If scan is ambiguous, ask.** No silent default that contradicts visible code or locks the project into an opinionated stack without user opt-in.

**Step 5: Greenfield default.** When neither a profile nor a codebase exists (for example `$araia init` on an empty directory), default to `architecture: modular-monolith` and the no-mediator dialect in [`../skills/dotnet-scaffold/references/`](../skills/dotnet-scaffold/references/architecture-layouts.md). This matches `dotnet-scaffold` and avoids introducing MediatR, MassTransit, per-layer projects, or preventive interfaces the user did not request.

Do not guess a module name. Its bounded-context evidence is a separate input.
The greenfield `vertical-slice` starter entry still writes
`architecture: modular-monolith`; its selection provenance belongs to scaffold
metadata, not this cross-consumer profile. The `clean` and `hexagonal` entries
write their own canonical value, so greenfield output and brownfield detection
agree on the same vocabulary. The legacy `src/Services/Features` vertical-slice
topology remains detectable but is not a greenfield output topology.

## Dialect-aware architectural opinions

Each axis with material variation has TWO presentations: one per dialect. Agents pick based on the profile.

### Example: `mediator` axis

- **`mediator: none`** (greenfield default, also dotnet-scaffold output)
  - Handlers: `sealed partial class Handler` inside a slice's `static partial class` container.
  - Dispatch: endpoints/resolvers inject the Handler directly through DI.
  - Cross-cutting: Minimal API endpoint filters (`ValidationFilter<T>`, `RequestLoggingFilter`) or HotChocolate middleware.
- **`mediator: mediatr | minidiator | mediator-source-gen | wolverine`** (typical brownfield)
  - Handlers: implement `IRequestHandler<TRequest, TResponse>` (or framework-specific equivalent).
  - Dispatch: through the mediator (`IMediator.Send` / `_mediator.Send`).
  - Cross-cutting: pipeline behaviors registered with the mediator.
  - Folder: `Features/[FeatureName]/Application/Commands/[CommandName]/` (Genial-style) or whatever the project demonstrates.
  - Reference: read a sample command in the project to confirm conventions; do not impose the scaffold's no-mediator slice pattern.

### Example: `test-assertions` axis

- `shouldly` → `value.ShouldBe(expected)`, `Should.Throw<>()`, `result.ShouldNotHaveAnyValidationErrors()` (with FluentValidation.TestHelper).
- `awesome-assertions` / `fluent-assertions` → `value.Should().Be(expected)`, `act.Should().Throw<>()`.
- `xunit-builtin` → `Assert.Equal(expected, value)`, `Assert.Throws<>()`.

### Example: `error-handling` axis

- `result-pattern` → fallible methods return `Result<T>`; no exceptions for business-rule violations; map Result to HTTP at the transport boundary.
- `exceptions` → throw typed exceptions for business-rule violations; transport layer catches and maps to HTTP.
- `hybrid` → Result for known business outcomes, exceptions for system failures; document the boundary in the project's ADR layer.

## When the profile contradicts the codebase

`dotnet-scaffold` writes the profile for greenfield bootstrap, and
`dotnet-solution-inspection` writes it inside architect-led `dotnet-discovery`
for brownfield SPECIFY or `$araia adopt`.
`dotnet-solution-inspection` refreshes the profile every time solution
inspection runs.

If an agent observes the profile contradicting the current codebase (e.g., profile says `mediator: none` but the project recently added MediatR), the agent must:

1. **Surface the contradiction explicitly** in its output.
2. **Suggest re-running `dotnet-discovery`** to refresh the profile and its
   architectural interpretation.
3. **Adapt to the observed state** for the current task. Do not blindly trust the stale profile.

## Scaffold reference scope

[`../skills/dotnet-scaffold/references/`](../skills/dotnet-scaffold/references/architecture-layouts.md)
is the maintained greenfield strategy and no-mediator pattern contract. It
documents modular topology, bounded contexts, vertical slices, tactical-pattern
thresholds, Domain Event versus Integration Event boundaries, outbox/inbox,
security and runtime guardrails, persistence selection, and agent context. The
executable overlay shapes live under
[`../skills/dotnet-scaffold/templates/features/`](../skills/dotnet-scaffold/templates/features/).

The Stack Profile controls the project's **observed implementation dialect**;
the scaffold references define the **greenfield baseline**. Agents use both:

- Resolve Stack Profile axes first. Use scaffold slice/template conventions only
  for `mediator: none` projects that match the modular profile.
- For any other mediator or architecture dialect, follow a representative slice
  in the codebase rather than imposing the greenfield shape.
- Consult the relevant scaffold reference for new ADRs, module boundaries,
  integration contracts, outbox/inbox, data ownership, security, NFRs, and agent
  context.
- A project-local approved ADR overrides the generic baseline; state the
  exception and preserve the closest compatible guardrail.

## Authoring rule for agent and skill files

When an agent or skill states a stack-specific architectural opinion, it must:

1. Cite the relevant Stack Profile axis (e.g., `architecture` or `mediator`).
2. Describe the opinion in BOTH dialects when the difference is material.
3. Point at this protocol for the loading mechanism.
4. Point at `skills/dotnet-scaffold/references/` or its overlay templates only
   for the no-mediator modular dialect.

This rule applies to:

- `agents/dotnet-architect.md`, `agents/dotnet-engineer.md`, and
  `agents/dotnet-specialist.md`;
- `dotnet-solution-inspection`, `dotnet-testing`, and
  `dotnet-runtime-diagnostics` platform capabilities;
- `dotnet-implementation` and `dotnet-scaffold`
  adapter capabilities;
- impact-analysis, source-review, and documentation contribution profiles; and
- specialty packs whose behavior depends on Stack Profile axes.

This list is the targeted refactor scope. Files not on this list are stack-neutral and need no changes.
