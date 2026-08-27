# C# Style Rules: .NET Adapter

These rules apply to every dotnet skill and agent for code generated, edited, or reviewed under this adapter. They are the authoritative C# style contract for the .NET adapter; project-local `.editorconfig` overrides on a per-rule basis when explicitly noted.

Every dotnet agent MUST read this file before generating, editing, or reviewing C# code.

---

## Cross-adapter authoritative rules

The following rules are framework-wide and authoritative across every adapter. They are referenced here for awareness; the linked files are the source of truth and override anything in this document if a conflict arises.

- **[`./.claude/araia/shared/no-spec-refs-in-implementation.md`](../../shared/no-spec-refs-in-implementation.md)**: implementation artifacts (C# source, tests, migrations, schemas, configs) MUST NOT reference spec-document identifiers by name, number, path, or URL (Delivery Slice / AC / ADR / PRD / SPEC IDs, issue-tracker links, spec file paths). The PR description and commit message are the right place to record the trail; XML doc comments, test names, and code comments stay self-describing. This applies to test method names (`Archive_WhenStatusOpen_FlipsToArchived` is right; `AC_3_archives_order` is wrong), `[Fact(DisplayName = "...")]`, `[Description]` attributes, and any inline `// SLICE-001` / `// per ADR-2026-05-23` style markers.

---

## Rule precedence (read first)

Several rules below interact on the same construct (`new`, lambdas, collections). When more than one rule applies, follow this order:

1. **Collection expressions win** (C# 12+). If the RHS would be a collection literal compatible with collection expressions (`new List<T> { ... }`, `new T[] { ... }`, empty `new List<T>()` / `Array.Empty<T>()`, or LINQ result assigned to a collection-expression target), apply the **Collection expressions** rule first. Do not also run the `var` rule or IDE0090 on that line, the canonical form is `[...]` (or `[]`), not `var x = new List<T>()` or `T x = new()`.
2. **IDE0090 (target-typed `new()`)** applies only to field, property, parameter default, and return-statement initializers where `var` is not available. For local variables, IDE0090's prescription is already implied by the `var` rule, do not re-derive it.
3. **`var` rule is the default** for local variables when no rule above takes over.
4. **`.editorconfig` overrides** any rule in this document on a per-key basis only when the rule explicitly grants precedence; otherwise this file is authoritative.

When two rules disagree, the rule **higher in this list** wins. Each rule below names which earlier rules it defers to, so the agent never has to choose without explicit guidance.

---

## `using` directives, never inline FQN

Always import types via `using` directives at the top of the file; never inline fully qualified type names in code bodies or signatures. Fully qualified names are allowed only inside XML doc `cref` attributes and in `global using` declarations.

## No `using` aliases

Never introduce `using Alias = Full.Namespace.Type` to resolve a name conflict. Resolve conflicts by renaming the locally-introduced symbol, moving it to a namespace that does not clash, or restructuring imports so only one of the conflicting names is in scope.

## Primary constructors are the default for DI-driven classes

When the project's `<LangVersion>` is `12.0` or newer (C# 12+ / .NET 8+), use a primary constructor instead of an explicit constructor + field assignments whenever the class's only constructor responsibility is to accept and store dependencies. When `<LangVersion>` is older, keep explicit constructors, do not retrofit older projects.

**Apply when**: the class's constructor body would be nothing more than parameter-to-field assignments. Access the parameters directly throughout the class body, do not copy them into private fields. Wrapping `repo` into a `_repo` field is the anti-pattern reviewers must reject; the canonical form is the bare parameter.

**Wrong**: explicit ctor with field copies:

```csharp
public sealed class CustomerHandler
{
    private readonly ICustomerRepository _repo;
    private readonly ILogger<CustomerHandler> _logger;

    public CustomerHandler(ICustomerRepository repo, ILogger<CustomerHandler> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public Task Handle(Guid id) => _repo.GetAsync(id);
}
```

**Right**: primary constructor, parameters used directly:

```csharp
public sealed class CustomerHandler(
    ICustomerRepository repo,
    ILogger<CustomerHandler> logger)
{
    public Task Handle(Guid id) => repo.GetAsync(id);
}
```

**Wrong**: primary constructor with redundant field copy:

```csharp
public sealed class OrderRepository(IDbConnection db)
{
    private readonly IDbConnection _db = db;

    public Task GetAsync(Guid id) => _db.QueryAsync(...);
}
```

**Right**: use the parameter directly:

```csharp
public sealed class OrderRepository(IDbConnection db)
{
    public Task GetAsync(Guid id) => db.QueryAsync(...);
}
```

**Exceptions, keep an explicit constructor when any of these holds:**

- **Invariant enforcement.** When the constructor must validate arguments, run guard clauses, or initialize state before fields are assigned. Domain aggregates and value objects typically fall here and use a `private` or `protected` constructor invoked from a static factory (`Create`, `Reconstruct`). Primary constructors cannot run validation before parameter assignment.
- **Canonical-instance pattern.** Classes that expose a fixed set of static instances via a private constructor (smart enums, sealed singletons).
- **ORM materialization constructor.** The constructor used by EF Core (or another ORM) to rehydrate entities from the database. Primary-constructor parameters are not reliably recognized by EF's materializer outside trivial cases.
- **Initialization beyond simple storage.** When the constructor body must compute derived state, subscribe to events, build internal caches, or run any non-trivial logic. Use explicit constructor; do not split logic between a primary constructor and a static initializer.
- **Multiple constructors with chaining.** Classes that need overloaded constructors with `: this(...)` chaining, primary constructors compose poorly with overloads.
- **Tooling that breaks on primary constructors.** Rare but real: some legacy analyzers and serializers require a real field. When forced, copy the parameter to a field **and** add a one-line comment naming the tool that forces it.

Primary-constructor parameters are mutable from inside the class. Treat them as effectively `readonly` in style: never reassign them. If reassignment is required, the class needs a real field, which itself is a sign that an explicit constructor is the better shape.

## Parameter count: maximum 7 per constructor or method

Mirror SonarQube rule **S107**. The limit covers constructors, instance methods, static methods, and local functions. Generated code MUST NOT exceed 7 parameters; reviewers MUST flag any signature that does. When the natural design pushes past 7, refactor before introducing the eighth, do not suppress S107 inline.

Remedies, in order of preference:

| Pressure source | Refactor |
|---|---|
| 3+ configuration values from `appsettings` | Bind a record into `IOptions<T>` (or `IOptionsSnapshot<T>` for hot reload) and inject the options instead of the loose values. |
| Several related collaborators (e.g., `IOrderRepository`, `IOrderPricingService`, `IOrderValidator`) | Extract a facade or domain service that owns the collaboration; inject the facade. |
| Multiple Polly policies or `HttpClient` instances | Register a `PolicyRegistry` (or `IHttpClientFactory` named clients) and inject the registry/factory. |
| Cross-cutting infra (logger, clock, metrics, tracing) bundled with domain deps | Keep cross-cutting deps; collapse domain-side deps via facade. Logger + clock + 5 domain deps is the danger zone, fix the domain side. |
| Long method signature accumulating workflow data | Introduce a parameter object (record) named after the workflow step (e.g., `RegisterCustomerInput`). |
| Handler taking command + multiple ambient values | Push ambient values (correlation id, tenant id, principal) into a scoped context service injected once. |
| Constructor of a domain entity with many fields | Use a static factory method that validates and constructs; keep the constructor `private` and accept a single `*State` record. |

Suppression with `[SuppressMessage("Major Code Smell", "S107:...")]` is allowed only for framework-generated code or interop boundaries where the signature is dictated externally; suppression requires a one-line comment explaining the external constraint.

## Mark instance methods as `static` when they do not use instance state (CA1822)

Mirror analyzer rule **CA1822**. Any method (or local function) whose body does not read or write `this`: no instance field, no instance property, no instance method call without an explicit receiver, MUST be declared `static`. Static methods avoid an unused `this` pointer at the call site and document intent: the method is a pure function of its parameters.

| Wrong | Right |
|---|---|
| `public int Multiply(int a, int b) => a * b;` (no instance state used) | `public static int Multiply(int a, int b) => a * b;` |
| `private string FormatId(Guid id) => $"customer-{id:N}";` | `private static string FormatId(Guid id) => $"customer-{id:N}";` |
| `private bool IsExpired(DateTimeOffset issuedAt, TimeSpan ttl) => DateTimeOffset.UtcNow - issuedAt > ttl;` | `private static bool IsExpired(DateTimeOffset issuedAt, TimeSpan ttl) => DateTimeOffset.UtcNow - issuedAt > ttl;` |
| Local function inside a method that uses only its parameters: `bool Match(Order o) => o.Status == OrderStatus.Open;` | `static bool Match(Order o) => o.Status == OrderStatus.Open;` |

**Exceptions, keep the method instance-bound when any of these holds:**

- **`virtual`, `override`, `abstract`, or `sealed override`.** Polymorphic dispatch requires an instance receiver; CA1822 does not apply.
- **Interface implementation.** A method implementing an interface member stays instance even if the body happens to ignore `this`. The interface contract is the source of truth.
- **Test methods.** xUnit `[Fact]`/`[Theory]`, NUnit `[Test]`, MSTest `[TestMethod]` are discovered as instance methods by the test runners. Do not mark them `static`.
- **Reflection / DI / source-gen targets.** Methods invoked by reflection-based frameworks that expect instance members: ASP.NET Core Minimal API endpoint handlers registered via `MapXxx(handler)` (when registered as a delegate to an instance method), MVC controller actions, Razor page handlers, HotChocolate resolvers that resolve via instance, MassTransit/Wolverine consumers, and any method targeted by an analyzer-generated partial (e.g., `[LoggerMessage]` partial methods are already declared correctly by the generator, do not retro-mark them).
- **Primary-constructor parameter capture.** When the method body uses a primary-constructor parameter (which is technically captured state, not `this`), the C# compiler still requires the method to be instance. Keep it instance; the analyzer will not flag it once it sees the capture.
- **Imminent expansion.** When the method is genuinely intended to grow to use instance state within the same change set (e.g., test-first TDD where the next test will introduce the dependency). Leave a one-line comment naming the next step; do not leave the comment behind once the expansion lands.
- **Operator-like helpers on a value object.** Methods that conceptually belong to the type's identity (e.g., `Money.Add(Money other)`) stay instance even when the body only touches parameters, they read more naturally as `total.Add(tax)` than `Money.Add(total, tax)`.

When the analyzer flags a method that falls under an exception above, prefer `#pragma warning disable CA1822` with a one-line comment naming the exception, scoped to the single method. Project-wide suppression of CA1822 is not allowed under this adapter.

## `var` vs explicit type

Mirror the IDE0007/IDE0008 defaults that Visual Studio, Rider, and the C# extension enforce: `var_for_built_in_types = false`, `var_when_type_is_apparent = true`, `var_elsewhere = false`. Decide by inspecting the syntactic shape of the right-hand side, no judgment on "obviousness" or "semantic weight":

| RHS shape | Choice | Example |
|---|---|---|
| `new T(...)`, `new T[...] { ... }`, `new() { ... }` (target-typed new with explicit LHS already absurd, use `var`), collection expression assigned via `new` | `var` | `var customer = new Customer(id);` `var items = new List<Order>();` |
| Cast or `as` expression | `var` | `var account = (Account)entity;` `var maybe = entity as Account;` |
| Tuple literal with explicit types, factory method whose name contains the type (`Customer.Create(...)`, `Result.Ok(...)`, `Guid.NewGuid()`, `Enumerable.Empty<Order>()`) | `var` | `var id = Guid.NewGuid();` `var result = Result.Ok(customer);` |
| Anonymous type, anonymous LINQ projection (`Select(x => new { ... })`) | `var` (required) | `var summary = items.Select(x => new { x.Id, x.Total });` |
| Method call, property access, indexer, `await` on a method whose name does not embed the type | **explicit type** | `Customer customer = await _repo.GetAsync(id);` `Result<Order, ApplicationError> result = handler.Handle(command);` `IReadOnlyList<Order> orders = _orders.Recent;` |
| Primitive literal or expression of primitive type (`int`, `long`, `bool`, `string`, `decimal`, `double`, `Guid` literal, `DateTime` literal) | **explicit type** | `int count = 0;` `string name = customer.Name;` `decimal total = 0m;` |
| LINQ chain whose terminal operator yields a non-anonymous type (`ToList`, `ToArray`, `First`, `Single`) | **explicit type** | `List<Order> open = orders.Where(o => o.IsOpen).ToList();` |
| `default` expression without target type, `null`, conditional expression where branches differ in declared type | **explicit type** | `Order? order = default;` `string label = isPrimary ? "primary" : fallback;` |

When the rule conflicts with project-local `.editorconfig`, the `.editorconfig` wins. Otherwise, treat the table as the authority and do not invent intermediate cases.

## No redundant type arguments on `new`: target-typed new (IDE0090)

Scope: **fields, properties, parameter defaults, and return statements** where the LHS already declares the type. For **local variables**, the `var` rule already covers this case and is authoritative, do not apply IDE0090 to local variables. For **collection literals**, the Collection expressions rule takes precedence (see Rule precedence above).

When the LHS declares `T`, write `new()` rather than repeating `new T()`.

| Wrong | Right |
|---|---|
| `private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();` | `private readonly Dictionary<string, int> _counts = new();` |
| `public Customer Owner { get; } = new Customer();` | `public Customer Owner { get; } = new();` |
| `void Register(CancellationToken ct = new CancellationToken()) { ... }` | `void Register(CancellationToken ct = new()) { ... }` (or `default`) |
| `public Order CreateEmpty() => new Order();` (return-typed context) | `public Order CreateEmpty() => new();` |

This rule does not cover `private readonly List<Order> _orders = new List<Order>();`: that case is a collection literal and falls under **Collection expressions**, which prescribes `[]`. Never produce `new()` for a type for which `[]` would be valid.

## Redundant lambda: explicit parameter types (IDE0200, "Lambda expression can be removed")

Lambda parameter types are redundant when the target delegate already fixes them. Write `o => ...` instead of `(Order o) => ...`. Apply to single-parameter and multi-parameter lambdas alike whenever the delegate type is unambiguous at the call site.

| Wrong | Right |
|---|---|
| `items.Where((Order o) => o.IsOpen)` | `items.Where(o => o.IsOpen)` |
| `items.Select((Customer c) => c.Id)` | `items.Select(c => c.Id)` |
| `list.Sort((Order a, Order b) => a.Id.CompareTo(b.Id))` | `list.Sort((a, b) => a.Id.CompareTo(b.Id))` |
| `endpoints.MapPost("/orders", (CreateOrder cmd, IHandler<CreateOrder> h) => ...)` (when `cmd` and `h` are bound by routing) | `endpoints.MapPost("/orders", (cmd, h) => ...)` only if Minimal API binding still resolves; otherwise keep types: see exception below |

**Exceptions, keep the explicit type when it is load-bearing, not redundant:**

- **Disambiguating overloads.** When two overloads accept different delegate types and inference would otherwise fail: `Convert(x => x.Id)` ambiguous between `Func<T, int>` and `Func<T, long>` → keep `(long x) => x.Id` or annotate the return.
- **Minimal API parameter binding.** ASP.NET Core Minimal API handlers use parameter types to decide source binding (`[FromBody]`, `[FromRoute]`, `[FromServices]`). Keep explicit types in the handler signature.
- **Expression trees with anonymous projections.** When the compiler cannot infer because the lambda is assigned to a variable whose target type is `Expression<Func<T, TResult>>` without a clear `TResult`, keep the type.
- **Discard parameters that need a type for documentation.** `(string _, EventArgs _) => ...` is fine when removing the type would hide intent (rare).

This rule does not opine on the parentheses around a single-parameter lambda (`(o) => ...` vs `o => ...`): defer to project-local `.editorconfig` (`csharp_style_prefer_simple_using_statement` family) for that choice.

## Redundant lambda: pass-through to method group (IDE0200, "Convert to method group")

When a lambda exists only to forward its arguments to a single method in the same order, replace it with a method group. The lambda is pure syntactic overhead; the method group reads more directly and matches the canonical .NET style.

| Wrong | Right |
|---|---|
| `items.Select(x => Map(x))` | `items.Select(Map)` |
| `customers.Where(c => IsActive(c))` | `customers.Where(IsActive)` |
| `button.Click += (s, e) => OnClick(s, e);` | `button.Click += OnClick;` |
| `app.MapGet("/health", () => HealthCheck());` | `app.MapGet("/health", HealthCheck);` |
| `Task.Run(() => DoWork())` | `Task.Run(DoWork)` |
| `services.AddSingleton<IClock>(_ => SystemClock.Instance);` (when SystemClock.Instance is `IClock`) | `services.AddSingleton<IClock>(SystemClock.Instance);` (factory form is unnecessary, use the instance overload) |

**Keep the lambda when any of these holds:**

- **Transformation or extra arguments.** `x => Map(x, context)` or `x => Map(x.Inner)` is not a pure pass-through.
- **Multiple statements or expression body with composition.** `x => { Log(x); return Map(x); }` cannot collapse.
- **Async over sync boundary.** `async x => await DoAsync(x)` exposes the `Task` correctly; `DoAsync` directly returns the same `Task` only if the signature already matches, if you remove the lambda, also remove the unnecessary `async`/`await`. Do not introduce a method group that silently changes async behavior.
- **Overload resolution would change.** Method groups bind eagerly to overloads at conversion time. If `Map` has overloads `(Order)` and `(object)`, `items.Select(Map)` may pick a different overload than `items.Select(x => Map(x))` once enumeration types narrow. When in doubt, keep the lambda.
- **Generic argument inference fails.** If `Select<TSource, TResult>(...)` cannot infer `TResult` from the method group alone, keep the lambda or specify generic arguments explicitly.
- **Captured state matters for readability.** A lambda that calls `this.OnClick(s, e)` and a method group `OnClick` are equivalent at runtime, but the lambda makes the receiver explicit. Prefer the method group; mention `this.OnClick` only when the agent has reason to disambiguate from a static `OnClick` in scope.

Hot-path concern: the lambda-to-method-group conversion allocates a fresh delegate on every call site invocation unless the C# compiler caches it (it caches static method groups and instance method groups against `this`; it does **not** cache method groups against captured locals). For hot paths inside loops, the agent should cache the delegate in a field rather than choosing between two equivalent allocating forms.

## Collection expressions for collection literals (IDE0300, IDE0301, IDE0305)

**Precedence**: this rule wins over the `var` rule and IDE0090 (see Rule precedence at the top of this file). Whenever the RHS is a collection literal compatible with collection expressions, produce `[...]` (or `[]`): do not produce `var x = new List<T>()`, `List<T> x = new()`, or `private readonly List<T> _items = new()`.

When the project's `<LangVersion>` is `12.0` or newer (effectively any .NET 8+ project), use the collection-expression form `[...]` instead of classic collection initializers wherever the target type accepts it. When `<LangVersion>` is older, keep classic initializers, do not retrofit older projects.

| Wrong (C# 12+) | Right (C# 12+) |
|---|---|
| `new List<int> { 1, 2, 3 }` | `[1, 2, 3]` |
| `new[] { "a", "b", "c" }` (when target type is `string[]` or `IEnumerable<string>`) | `["a", "b", "c"]` |
| `Array.Empty<Order>()` / `new List<Order>()` as an empty literal | `[]` |
| `private readonly List<Order> _orders = new List<Order>();` (field with empty collection literal) | `private readonly List<Order> _orders = [];` |
| `private readonly List<Order> _orders = new();` (target-typed empty literal, still wrong because rule above takes precedence) | `private readonly List<Order> _orders = [];` |
| `public IReadOnlyList<Order> Snapshot { get; } = new List<Order>();` | `public IReadOnlyList<Order> Snapshot { get; } = [];` |
| `existing.Concat(extras).ToList()` when the result is assigned to a collection-expression-compatible target | `[..existing, ..extras]` |
| `new HashSet<Guid> { id1, id2 }` | `[id1, id2]` (target type drives the concrete collection) |

Collection expressions do not apply when the constructor takes arguments other than elements (e.g., `new Dictionary<K,V>(comparer)`, `new List<Order>(capacity)`): keep the explicit constructor in those cases. When the explicit constructor remains for a local variable, the `var` rule applies (`var dict = new Dictionary<K,V>(comparer);`); when it remains for a field, IDE0090's `new()` form applies (`private readonly Dictionary<K,V> _dict = new(comparer);`).

## Range and index operators (IDE0056, IDE0057)

Prefer the range/index syntax over manual arithmetic on `Length`/`Count` and over LINQ chains that re-express the same slicing.

| Wrong | Right |
|---|---|
| `array[array.Length - 1]` | `array[^1]` |
| `text.Substring(0, text.Length - 1)` | `text[..^1]` |
| `text.Substring(2)` | `text[2..]` |
| `text.Substring(2, 5)` | `text[2..7]` (note: range end is exclusive; convert `length` to `start + length`) |
| `list.Skip(2).Take(3).ToList()` on `List<T>` / `IReadOnlyList<T>` when a slice is intended | `list[2..5]` (or `CollectionsMarshal.AsSpan(list)[2..5]` for hot paths) |
| `array.Take(n).ToArray()` | `array[..n]` |

Range/index applies to types that support it (arrays, `string`, `Span<T>`, `Memory<T>`, `List<T>` via `CollectionsMarshal`, custom types that define `Slice(int, int)` + `Length`/`Count`). On `IEnumerable<T>` without a slice method, keep the LINQ form, do not force an allocation just to satisfy the rule.
