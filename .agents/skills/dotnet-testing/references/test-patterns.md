# Test Patterns  --  xUnit + NSubstitute + Shouldly

Quick-reference patterns for common testing scenarios. All examples use the standard stack: xUnit for the framework, NSubstitute for mocking, Shouldly for assertions.

---

## Simple Test (Fact)

```csharp
[Fact]
public void Should_return_empty_when_input_is_null()
{
    var service = new MyService();

    var result = service.Process(null);

    result.ShouldBeEmpty();
}
```

---

## Parameterized Test (Theory + InlineData)

Use `[Theory]` when multiple inputs exercise the same behavior. Each `[InlineData]` row is a separate test execution.

```csharp
[Theory]
[InlineData(1, "I")]
[InlineData(4, "IV")]
[InlineData(9, "IX")]
[InlineData(14, "XIV")]
public void Should_convert_number_to_roman(int input, string expected)
{
    var converter = new RomanNumeralConverter();

    converter.ConvertToRoman(input).ShouldBe(expected);
}
```

---

## Parameterized Test (Theory + MemberData)

When test data is complex or reusable, use `[MemberData]`:

```csharp
public static IEnumerable<object[]> DiscountScenarios()
{
    yield return new object[] { 100m, 0, 100m };      // No discount
    yield return new object[] { 100m, 10, 90m };      // 10% off
    yield return new object[] { 200m, 25, 150m };     // 25% off
    yield return new object[] { 0m, 50, 0m };         // Zero amount
}

[Theory]
[MemberData(nameof(DiscountScenarios))]
public void Should_apply_discount_correctly(decimal amount, int percent, decimal expected)
{
    var calculator = new PricingCalculator();

    var result = calculator.ApplyDiscount(amount, percent);

    result.ShouldBe(expected);
}
```

---

## Exception Test

```csharp
[Fact]
public void Should_throw_when_amount_is_negative()
{
    var calculator = new PricingCalculator();

    var act = () => calculator.ApplyDiscount(-100m, 10);

    act.ShouldThrow<ArgumentOutOfRangeException>()
       .Message.ShouldContain("Amount must be non-negative");
}
```

---

## Async Test

```csharp
[Fact]
public async Task Should_return_user_when_found()
{
    var repo = Substitute.For<IUserRepository>();
    repo.GetByIdAsync(1).Returns(new User(1, "Alice"));
    var service = new UserService(repo);

    var result = await service.GetByIdAsync(1);

    result.ShouldNotBeNull();
    result.Name.ShouldBe("Alice");
}
```

---

## Mocking with NSubstitute

### Stub a return value

```csharp
var repository = Substitute.For<IPortfolioRepository>();
repository.GetById(Arg.Any<Guid>())
    .Returns(new Portfolio { Id = portfolioId, Balance = 50_000m });
```

### Stub async methods

```csharp
repository.GetByIdAsync(Arg.Any<Guid>())
    .Returns(Task.FromResult(new Portfolio { Balance = 50_000m }));

// Or more concisely:
repository.GetByIdAsync(Arg.Any<Guid>())
    .Returns(new Portfolio { Balance = 50_000m });
```

### Verify an interaction occurred

```csharp
[Fact]
public async Task Should_publish_event_when_processing_redemption()
{
    var eventBus = Substitute.For<IEventBus>();
    var repository = Substitute.For<IPortfolioRepository>();
    repository.GetById(Arg.Any<Guid>())
        .Returns(new Portfolio { Balance = 10_000m });

    var service = new RedemptionService(repository, eventBus);

    await service.ProcessRedemption(new RedemptionCommand
    {
        PortfolioId = Guid.NewGuid(),
        Amount = 5_000m
    });

    await eventBus.Received(1).Publish(
        Arg.Is<RedemptionProcessedEvent>(e =>
            e.Amount == 5_000m && e.RemainingBalance == 5_000m)
    );
}
```

`Received(1)` ensures exactly one call. `Arg.Is<T>` with a predicate validates content without coupling to irrelevant fields like auto-generated IDs.

### Verify no interaction

```csharp
await eventBus.DidNotReceive().Publish(Arg.Any<DomainEvent>());
```

### Conditional returns

```csharp
repository.GetById(Arg.Is<Guid>(id => id == knownId))
    .Returns(existingPortfolio);

repository.GetById(Arg.Is<Guid>(id => id != knownId))
    .Returns((Portfolio?)null);
```

---

## Integration Test with IAsyncLifetime

xUnit's `IAsyncLifetime` provides async setup/teardown  --  essential for integration tests with databases.

```csharp
public class OrderRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly MongoDbFixture _fixture = new();

    public async Task InitializeAsync()
        => await _fixture.ResetCollection("orders");

    public async Task DisposeAsync()
        => await _fixture.Dispose();

    [Fact]
    public async Task Should_persist_and_retrieve_order()
    {
        var repository = new OrderRepository(_fixture.Database);
        var order = new Order
        {
            CustomerId = "CUST-001",
            Items = new List<OrderItem>
            {
                new() { ProductId = "PROD-A", Quantity = 2, UnitPrice = 29.90m }
            }
        };

        await repository.Insert(order);
        var retrieved = await repository.GetById(order.Id);

        retrieved.ShouldNotBeNull();
        retrieved.CustomerId.ShouldBe("CUST-001");
        retrieved.Items.Count.ShouldBe(1);
        retrieved.Items[0].ProductId.ShouldBe("PROD-A");
    }
}
```

---

## Result Pattern Test

When the codebase uses the Result pattern for error handling:

```csharp
[Fact]
public void Should_return_failure_when_email_is_invalid()
{
    var result = Email.Create("not-an-email");

    result.IsFailure.ShouldBeTrue();
    result.Error.ShouldContain("invalid email");
}

[Fact]
public void Should_return_success_when_email_is_valid()
{
    var result = Email.Create("user@example.com");

    result.IsSuccess.ShouldBeTrue();
    result.Value.Value.ShouldBe("user@example.com");
}
```

---

## Contract Test for DTOs (Reflection + Types)

When a DTO's structure is a contract (API surface, message format), protect it with a structural test:

```csharp
[Fact]
public void Contract_Should_contain_properties_with_correct_types()
{
    var expectedContract = new Dictionary<string, Type>
    {
        { nameof(OrderResponse.Id), typeof(string) },
        { nameof(OrderResponse.Status), typeof(OrderStatus) },
        { nameof(OrderResponse.Total), typeof(decimal) },
        { nameof(OrderResponse.CreatedAt), typeof(DateTime) },
    };

    var actualContract = typeof(OrderResponse)
        .GetProperties()
        .ToDictionary(p => p.Name, p => p.PropertyType);

    expectedContract.ShouldBe(actualContract);
}
```

---

## Shouldly Quick Reference

| Assertion | Example |
|-----------|---------|
| Equality | `result.ShouldBe(42)` |
| Approximate | `result.ShouldBe(3.14m, tolerance: 0.01m)` |
| Null check | `result.ShouldNotBeNull()` / `result.ShouldBeNull()` |
| Boolean | `result.ShouldBeTrue()` / `result.ShouldBeFalse()` |
| Collection empty | `list.ShouldBeEmpty()` / `list.ShouldNotBeEmpty()` |
| Collection count | `list.Count.ShouldBe(3)` |
| Contains | `list.ShouldContain(item)` |
| String contains | `message.ShouldContain("expected substring")` |
| Type check | `result.ShouldBeOfType<OrderCreatedEvent>()` |
| Exception | `act.ShouldThrow<ArgumentException>()` |
| Greater/Less | `result.ShouldBeGreaterThan(0)` |
| In range | `result.ShouldBeInRange(1, 10)` |
| All items | `list.ShouldAllBe(x => x > 0)` |
| Satisfy | `result.ShouldSatisfyAllConditions(() => result.Name.ShouldBe("X"), () => result.Age.ShouldBe(30))` |

---

## Test Organization Patterns

### Constructor for shared setup (xUnit way)

```csharp
public class OrderServiceTests
{
    private readonly IOrderRepository _repository;
    private readonly IEventBus _eventBus;
    private readonly OrderService _sut;

    public OrderServiceTests()
    {
        _repository = Substitute.For<IOrderRepository>();
        _eventBus = Substitute.For<IEventBus>();
        _sut = new OrderService(_repository, _eventBus);
    }

    [Fact]
    public void Should_create_order_with_valid_data() { /* ... */ }

    [Fact]
    public void Should_reject_order_with_empty_items() { /* ... */ }
}
```

### Collection Fixture for expensive shared resources

```csharp
[CollectionDefinition("Database")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture> { }

[Collection("Database")]
public class OrderRepositoryTests
{
    private readonly DatabaseFixture _db;

    public OrderRepositoryTests(DatabaseFixture db) => _db = db;

    [Fact]
    public async Task Should_query_orders_by_status() { /* ... */ }
}
```

Use `ICollectionFixture<T>` when the resource (database container, HTTP server) is expensive to create and can be shared across test classes.
