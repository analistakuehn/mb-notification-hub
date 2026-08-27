# Vertical-Slice Conventions

A feature is the primary unit of change inside a Bounded Context. Keep the code
needed to understand and modify one use case together, so a business change
touches neighbouring files instead of a tour through global technical folders.

```text
<features-root>/<Context>/Features/Mutations/CreateQuote/
  CreateQuote.Command.cs
  CreateQuote.Validator.cs
  CreateQuote.Handler.cs
  CreateQuote.Handler.Logger.cs
  CreateQuote.Response.cs
  CreateQuote.Endpoint.cs
```

`<features-root>` comes from the selected topology. The slice shape does not
change with the topology; only its address does. In the modular monolith the
whole slice sits in one project. In Clean and Hexagonal the endpoint lives in the
host and the persistence implementation lives in the adapter project, while
command, validator, handler, logger, and response stay together in the
application layer.

Use `Queries/` and `Mutations/` when that distinction helps navigation. Do not
grow a second layer tree inside a slice. File names use dot suffixes so a
directory listing groups every role under the use-case name; concatenated names
such as `CreateQuoteHandler.cs` are not the generated dialect.

## Role boundaries

- `Command` or query input carries transport-friendly primitives. Never bind HTTP
  or GraphQL input directly to an aggregate or entity.
- `Validator` checks shape and structure: required values, lengths, formats,
  ranges, and mutually exclusive fields. It is not the home of business
  invariants and it does not touch the database.
- `Handler` rebuilds value objects from primitives, invokes aggregate behavior,
  coordinates module-local persistence and providers, and returns the project's
  single error axis. It does not reimplement domain rules.
- `Response` is an explicit public projection. Do not return mutable domain
  objects across a transport boundary.
- `Endpoint` maps transport input and `Result<T>` to the protocol. State-changing
  and upstream-cost endpoints declare named authorization and rate-limiting
  policies, and anonymous access is explicit.
- `Handler.Logger` uses source-generated `LoggerMessage` methods and records
  identifiers or classifications, never personal data, financial values, tokens,
  secrets, or raw provider responses.

## Shape

The generated dialect uses a static partial container with nested role types:

```csharp
public static partial class CreateQuote
{
    public sealed record Command(string Pair, decimal Amount);
    public sealed record Response(Guid QuoteId, string Status);
}
```

The handler is another partial of the same container. Minimal API and GraphQL
adapters invoke the same handler, so a transport never duplicates use-case
logic. The generator omits a mediator because direct, explicit calls keep the
navigation surface small; introduce one only when a measured cross-cutting need
outweighs its indirection, and record that decision.

## The known trade-off

Repeating the same role set per slice buys consistency and costs a shotgun edit
when a cross-cutting field arrives, an audit header for example. Mitigate it when
the pain appears, not before: a shared record for transversal fields, an endpoint
filter that injects the concern at runtime, or a source generator. Choosing the
mitigation preemptively reintroduces the ceremony the slice removed.

## Where the domain sits

Slices orchestrate; they do not own invariants. A rule that must always hold
belongs to an aggregate or a value object in the context's domain surface, and
the handler calls it. When a slice starts accumulating conditionals about valid
states, that is the signal to move the rule into the model, not to add another
branch. Tactical guidance lives in [`tactical-patterns.md`](tactical-patterns.md).

## Testing

Start with a failing behavior test. Test aggregate invariants and Domain Events
as unit tests, handler orchestration at the narrowest useful seam, and
persistence, outbox and inbox, broker, provider, and HTTP behavior as
integration tests. Dependency and security rules belong to their dedicated
architecture test projects.

Name tests for behavior, `Subject_Scenario_ExpectedBehavior`, never for
specification identifiers. A slice is not complete because its happy path
compiles.
