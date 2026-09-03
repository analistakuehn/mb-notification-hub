# .NET Implementation Output Templates

Templates loaded on demand when `dotnet-implementation` produces a bounded implementation or source-review receipt.

## Code Review Report

```markdown
## Code Review: [File/Feature]
### Summary -- APPROVED / APPROVED WITH COMMENTS / CHANGES REQUESTED
### Issues Found
| # | Severity | Location | Issue | Recommendation |
|---|---|---|---|---|
| 1 | HIGH | OrderService.cs:45 | Exception used for validation | Return Result<T> with ValidationError |
| 2 | MED | Order.cs:12 | Public setter on Status | Encapsulate with ChangeStatus() |
| 3 | LOW | GetOrdersQuery.cs:8 | Missing AsNoTracking | Add .AsNoTracking() |
### Positive Aspects
### Suggested Refactoring
```

## Implementation Scaffold (per `mediator` axis)

### `mediator: mediatr | minidiator | mediator-source-gen | wolverine` (Application/Commands+Queries layout)

```
Features/[FeatureName]/
├── Domain/
│   ├── [AggregateName].cs
│   ├── ValueObjects/
│   └── Events/
├── Application/
│   ├── Commands/[CommandName]/
│   │   ├── [CommandName].Command.cs
│   │   ├── [CommandName].Validator.cs
│   │   ├── [CommandName].Handler.cs
│   │   ├── [CommandName].Response.cs
│   │   └── [CommandName].Result.cs
│   ├── Queries/[QueryName]/
│   │   ├── [QueryName].Query.cs
│   │   ├── [QueryName].Handler.cs
│   │   └── [QueryName].Response.cs
│   └── Services/[FeatureName]Service.cs
├── Infrastructure/Persistence/[AggregateName]Configuration.cs
└── [FeatureName]Module.cs
```

### `mediator: none` (module-owned vertical slices)

```
Modules/[Context]/
├── [Context]Module.cs                        # IModule: ConfigureServices + MapEndpoints
├── AGENTS.md
├── hotspots.md
├── Domain/
│   ├── [AggregateName].cs
│   ├── ValueObjects/
│   └── Events/
├── Integration/V1/
├── Features/
│   ├── Mutations/[CommandName]/               # static partial class [CommandName]
│   │   ├── [CommandName].Command.cs
│   │   ├── [CommandName].Validator.cs
│   │   ├── [CommandName].Handler.cs
│   │   ├── [CommandName].Handler.Logger.cs
│   │   ├── [CommandName].Response.cs
│   │   └── [CommandName].Endpoint.cs          # or .Resolver.cs
│   └── Queries/[QueryName]/
│       ├── [QueryName].Input.cs               # or Query.cs for Minimal API GETs
│       ├── [QueryName].Handler.cs
│       ├── [QueryName].Handler.Logger.cs
│       ├── [QueryName].Response.cs
│       └── [QueryName].Resolver.cs            # or .Endpoint.cs
└── Infrastructure/Persistence/[AggregateName]Repository.cs
```
