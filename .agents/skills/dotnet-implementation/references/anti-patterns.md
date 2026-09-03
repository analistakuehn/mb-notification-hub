# .NET Implementation Anti-Patterns

Loaded on demand by `dotnet-implementation` or a `dotnet-engineer` implementer dispatch.

| Anti-pattern | Why / Use instead |
|---|---|
| Anemic domain models (public setters) | Public setters expose invariants to violation. Use factory methods + encapsulated mutation. |
| Exception-driven control flow | Expensive, hides error paths. Result Pattern makes failures explicit and composable. |
| N+1 queries | Roundtrip multiplication. Use `Include()`, projections, or batch loading. |
| Missing `AsNoTracking` | EF tracks by default, wasting memory on read paths. Always opt out when not mutating. |
| Domain entities in API responses | Couples internals to external contracts. Project to DTOs. |
| Business logic in handlers | Handlers orchestrate. Logic belongs in domain/Application Services: testable and reusable. |
| Database access in validators | Validators run synchronously. Database-dependent checks belong in Application Services. |
| Outdated C# patterns | Modern alternatives (NRT, pattern matching) are safer and clearer. |
| Fully qualified names | Always use `using` directives at file top. |
| Raw `object` / `object?` | Prefer strongly typed alternatives (`TagList` for OTel tags; generics instead of `object` parameters). |
| Raw `IConfiguration` access | Use `IOptions<T>`/`IOptionsSnapshot<T>`/`IOptionsMonitor<T>` with dedicated options classes. |
| `#pragma warning disable` in source | Suppress via `.editorconfig` at project level. |
| Plain `enum` with associated data | Smart Enum pattern (sealed class + static readonly instances). |
