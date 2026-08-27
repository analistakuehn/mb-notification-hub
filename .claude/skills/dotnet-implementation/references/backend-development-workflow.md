# Backend Development Workflow

This step-by-step procedural protocol covers the three core authoring tasks. `dotnet-implementation` loads it on demand when project evidence activates this dialect.

## Domain model

1. Aggregate root with private constructor + static factory.
2. Encapsulate collections with private backing fields and `IReadOnly*` public properties.
3. Value objects with validation in factory methods.
4. Domain events for business-significant state changes.
5. Business methods return `Result`.
6. Smart Enums for status/type with transition logic.
7. EF Core entity configurations with indexes/conversions.

## Command (dialect depends on Stack Profile axis `mediator`)

`mediator: none` -> follow [`dotnet-scaffold/references/slice-conventions.md`](../../dotnet-scaffold/references/slice-conventions.md) and the selected transport overlay under [`dotnet-scaffold/templates/features/transports/`](../../dotnet-scaffold/templates/features/transports/). Use a module-owned slice container, dot-suffix files, endpoint-filter validation, and explicit handler/validator registration in `<Context>Module`.

`mediator: mediatr | minidiator | mediator-source-gen | wolverine`:
1. `[CommandName].Command.cs`: record implementing `IRequest<Result<TResponse>>` (or framework equivalent).
2. `[CommandName].Validator.cs`: FluentValidation (structure/format only).
3. `[CommandName].Enricher.cs`: user context, timestamps (if needed).
4. `[CommandName].Handler.cs`: implements `IRequestHandler<TRequest, TResponse>`; delegates to Application Service.
5. Application Service in `Features/[FeatureName]/Application/Services/`.
6. `[CommandName].Response.cs`, `[CommandName].Result.cs` (with static error factories).
7. Endpoint file with OpenAPI docs.
8. `[CommandName].Handler.Logger.cs` using source-gen `[LoggerMessage]`.
9. Register handler, validator, and pipeline behaviors in the feature module.

## Query (dialect depends on `mediator`)

`mediator: none` -> follow [`dotnet-scaffold/references/slice-conventions.md`](../../dotnet-scaffold/references/slice-conventions.md); the GraphQL and Minimal API overlays supply host transport defaults while the resolver/endpoint delegates to the same transport-agnostic handler.

`mediator: mediatr | minidiator | mediator-source-gen | wolverine`:
1. `[QueryName].Query.cs` + `[QueryName].Response.cs` with pagination.
2. Specifications in `Specifications/` for complex filters.
3. Handler uses `IReadRepository` with `AsNoTracking` + projections.
4. Run count and data fetch in parallel for paginated queries.
5. Support dynamic sorting and filtering.

## EF Core optimization

Profile with logging -> identify N+1 -> add `AsNoTracking` / `Select` projections / `AsSplitQuery` / indexes -> use `ExecuteUpdateAsync` for bulk -> consider raw SQL for complex analytics.
