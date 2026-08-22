# TemplateManagement module

## Boundary

- Keep one bounded context in this module. Its name comes from domain discovery, never from a table, screen, or technical layer.
- Keep invariants in the aggregates and value objects under `src/Platform.Api/Modules/TemplateManagement/Domain/`.
- Keep use-case orchestration in the slices under `src/Platform.Api/Modules/TemplateManagement/Features/`.
- Do not read or write another context's data store, infrastructure types, or mutable domain types.
- Publish cross-context facts as distinct, versioned contracts under `src/Platform.Api/Modules/TemplateManagement/Integration/V1/`.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `src/Platform.Api/Modules/TemplateManagement/Domain/` | aggregates, value objects, domain policies, internal Domain Events |
| `src/Platform.Api/Modules/TemplateManagement/Features/` | vertical slices for this context |
| `src/Platform.Api/Modules/TemplateManagement/Integration/V1/` | versioned public Integration Event contracts |
| `src/Platform.Api/Modules/TemplateManagement/Infrastructure/` | persistence, providers, broker, cache, outbox, inbox |
| `src/Platform.Api/Modules/TemplateManagement/TemplateManagementModule.cs` | application service registration for this context; technology registration for this context; endpoint mapping for this context |

## Implementation

- Organize new use cases as vertical slices; keep one use case's input, structural validation, orchestration, response, logging, and transport mapping together.
- Keep commands primitive at the transport boundary and rebuild value objects before invoking domain behavior.
- Keep repositories and domain policies concrete unless an observed boundary or test seam justifies an interface.
- Return `Result<T>` for expected outcomes; reserve exceptions for unexpected system failures.
- Raise a Domain Event when behavior inside this context reacts to a fact. Map it to a versioned Integration Event only when another context consumes it.

## Audit trail (provisional ownership)

- The `audit_event` and `approval` tables are provisionally owned and migrated
  by this module until the dedicated Audit module takes ownership, together
  with the hash-chain integrity columns and the monthly partitioning. Do not
  add hash-chain columns here; they belong to that hand-over.
- Both tables are append-only by construction: a database trigger rejects
  `UPDATE` and `DELETE`. Every governed effect (create, publish, deprecate,
  disable, rollback) inserts its `audit_event` in the same transaction as the
  effect, through the same `SaveChanges` call; a separate save is a defect.
- `details` carries compact JSON evidence (content hash, validation outcome,
  reason). Never personal data, variables, or rendered content.

## Error axis

- This module has exactly one error axis: `Result`/`Result<T>` from the
  SharedKernel carrying a `DomainError`-encoded string (stable code plus scalar
  detail, unit-separator framing). The string is decoded once, at the HTTP
  boundary (`Infrastructure/Http/ApiResults.cs`); handlers and domain code
  never parse it back.
- Hard rule: a structured or list-shaped payload never travels in the error
  string. When a use case produces a report, a collection, or any composite
  outcome, model it as a response value returned through `Result.Success`,
  even when the outcome describes failures (the validation report is the
  canonical example: failed checks are data, not errors).
- Review trigger: the moment a second module needs coded errors, promote a
  typed `Error` to the SharedKernel through an accepted ADR instead of copying
  the `DomainError` encoding dialect.

## Logging

- Each use case ships a dedicated `<UseCase>.Handler.Logger.cs` file holding a
  top-level `internal static partial class <UseCase>Logger` (extension methods
  do not compile in nested classes, so the logger class sits beside the slice
  container, not inside it).
- Log methods are source-generated: `[LoggerMessage] internal static partial
  void Evento(this ILogger logger, ...)`; handlers call `logger.Evento(...)` on
  the injected `ILogger`.
- Identifiers stay in English (`TemplateCreated`, `EndpointInvocationStarted`);
  pt-BR appears only in log message text and user-facing text, with proper
  diacritics. Placeholders carry real domain names (template key, version,
  channel, locale), never personal data, variables, or rendered content.
- The dialect covers every `.Logger.cs` in the repository, including the host's
  `Infrastructure/EndpointFilters/RequestLoggingFilter.Logger.cs`.

## Security and tests

- Require named authorization and rate-limiting policies on state-changing endpoints.
- Never bind HTTP bodies directly to domain types.
- Do not log personal data, financial values, tokens, secrets, or connection strings.
- Start with a failing behavior test; add unit tests for aggregate invariants and Domain Events.

Update this file in the same change that alters the module boundary, public contracts, ubiquitous language, or non-negotiable security rules.
