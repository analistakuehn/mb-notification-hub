# .NET Testing Special Scenarios

Loaded on demand when the current task matches one of the scenarios below. Default greenfield TDD does NOT need this file.

## External dependencies (DB, API)

Define interfaces in the test; mock with NSubstitute/Moq; production code depends only on abstractions; integration tests go in a separate project.

## Legacy / Brownfield code (tests on existing production code)

Strict test-first doesn't apply. Shift to **risk-driven, safety-first**.

1. **Map Risk Zones:**
   - **Red**: financial calcs, transactions, compliance, payments. Test these first.
   - **Yellow**: complex conditional logic that changes often (rule engines, workflows).
   - **Green**: stable/simple structural code (DTOs, trivial mappings, delegation controllers).
2. **Characterization Tests** (Feathers): capture current behavior before changing anything. Expected values come from current behavior even if buggy (document bugs; fix later with separate tests).
3. **Identify Seams** -- points where behavior can be substituted:
   - Existing interface -> inject substitute
   - Extract and override (virtual method) -- temporary stepping stone to DI
   - Static wrappers (`DateTime.Now`, `File.ReadAllText`) -> injectable services or `TimeProvider` (.NET 8+)
   - Sprout method/class: new tested method/class called from existing code
4. **Unit tests on business rules** with seams and characterization tests as safety net.
5. **Integration tests at boundaries** (serialization, DB, messaging, HTTP). Use `IAsyncLifetime` for async setup/teardown.

**Boy Scout Rule:** every piece of code you touch from now on leaves with tests. Bug fix -> failing test reproduces it. New feature -> TDD from here. Refactor -> characterization test first.

## TDD with DDD

- **Entities / Value Objects**: test invariants and business rules.
- **Domain Services**: test rule orchestration.
- **Application Services**: mock infrastructure.
- **Result Pattern**: test both success and failure paths explicitly.

## TDD with vertical-slice handlers (per Stack Profile axis `mediator`)

- **Handlers** (both dialects: `mediator: none` `sealed partial class Handler` or `mediator: mediatr/minidiator/wolverine` `IRequestHandler<TRequest, TResponse>`): mock repositories and domain services; test orchestration. The handler test shape is identical across dialects -- the difference is only how the handler is dispatched at runtime.
- **Query handlers**: mock read repositories; test projection and pagination.
- **Validators**: test rules in isolation with FluentValidation (`AbstractValidator<T>` works the same regardless of mediator dialect).
- **Pipeline behaviors / endpoint filters**: test cross-cutting concerns independently. With `mediator: none`, target the Minimal API endpoint filters (`ValidationFilter<T>`, `RequestLoggingFilter`); with mediator dialects, target the corresponding pipeline behaviors.

## TDD with Anemic Models (DTOs, requests, responses)

Anemic models have no behavior -- test their **structural contract**. Any added/removed/renamed/retyped property must break the test.

**Default: reflection + types** (explicit, no external dep, validates names AND types). Build an `expectedContract` as `Dictionary<string, Type>` using `nameof(...)`, compare against `typeof(T).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType)`.

Alternatives: **names-only reflection** when types don't matter; **Verify snapshot** (`await Verify(dto);`) for public API contracts or cross-team shared schemas. One contract test per DTO.
