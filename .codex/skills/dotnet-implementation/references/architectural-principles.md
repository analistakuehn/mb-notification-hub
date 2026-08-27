# .NET Implementation Principles

Loaded on demand by `dotnet-implementation` or a `dotnet-engineer` implementer dispatch.

**Domain-Driven Design**
- Rich domain models with encapsulated business logic; never anemic entities with public setters.
- Aggregates are consistency boundaries; use factory methods that enforce invariants.
- Model value objects (Email, Money, Address) with validation in construction.
- Raise domain events for significant business occurrences.
- Smart Enums (`Ardalis.SmartEnum`) with behavior, not primitive `enum`s.
- Keep domain layer pure; no infrastructure or framework deps.

**Result Pattern (railway-oriented)**
- Return `Result<T>` or `Result` from any operation that can fail.
- Never throw for business rule violations or validation failures.
- Use `Match()` for success/failure handling.
- Static factory methods on `Result` for common errors (`NotFound`, `ValidationError`, `BusinessRuleViolation`).
- Chain with railway composition. Reserve exceptions for truly exceptional system failures.

**Vertical-slice handlers (dialect depends on Stack Profile axis `mediator`)**

For both dialects: business logic lives in the Handler; commands return `Result<TResponse>` (when `error-handling: result-pattern`); queries return paginated DTOs; Application Services coordinate across aggregates and own transactions when the operation crosses aggregates.

- **`mediator: mediatr | minidiator | mediator-source-gen | wolverine`**: handlers implement `IRequestHandler<TRequest, TResponse>` (or the equivalent for the specific mediator). Dispatch through the mediator's `Send`. Pipeline behaviors carry validation/logging/transactions. Folder structure follows the project's existing convention, typically `Features/[FeatureName]/Application/Commands/[CommandName]/` and `.../Queries/[QueryName]/`.

**EF Core performance**
- `AsNoTracking()` for all read-only queries.
- Project to DTOs with `Select(x => new DTO { ... })` instead of loading full entities.
- `AsSplitQuery()` to avoid cartesian explosion on large relationships.
- Strategic `Include()`: avoid N+1 without over-eager loading.
- `ExecuteUpdateAsync` / `ExecuteDeleteAsync` for bulk ops (EF Core 7+).
- Index foreign keys and frequently queried columns.
- `Ardalis.Specification` for reusable query logic.

**Modern C# (.NET 9/10, C# 13+)**
Records for DTOs/value objects; `required` properties; nullable reference types project-wide; pattern matching (property/relational/list); file-scoped namespaces; global usings; primary constructors and collection expressions; `params` collections; semi-auto properties; extension types.
