# Existing Code Strategy

Full risk-driven strategy for introducing tests into production codebases. Philosophy: **test by risk, not by coverage**.

---

## Step 1 -- Map the Risk Zones

Classify code before writing tests. Prevents wasting effort on low-impact areas while critical logic remains unprotected.

### Red Zone -- Test First, No Exceptions

Code whose failure causes financial, regulatory, or reputational damage:
- Financial calculations (TWR, MWR, XIRR, tax, fees)
- Transaction processing (payments, transfers, settlements)
- Compliance/regulatory rules (KYC, AML, reporting)
- External API integrations moving money/data
- Authentication/authorization logic

### Yellow Zone -- Test When You Touch

Complex conditional logic that changes frequently:
- Business rule engines, policy evaluators
- Approval workflows, state machines
- Data transformation/enrichment pipelines
- Scheduling/batch processing logic

### Green Zone -- Test If Convenient

Stable, simple, or purely structural:
- DTOs, request/response objects, view models
- Trivial mappings (AutoMapper, manual mappers with no logic)
- DI container registration
- Controllers that only delegate to services
- Configuration classes

---

## Step 2 -- Characterization Tests (Capture Current Behavior)

From Feathers' *Working Effectively with Legacy Code*. Documents what code does today, not what it should do.

**Why first**: safety net before refactoring. If test breaks after change, you altered behavior.

**How**:
1. Call with known inputs
2. Observe actual output
3. Assert that output, even if it seems wrong
4. Document known bugs with comments, not with "correct" assertions

```csharp
public class ReportGeneratorCharacterizationTests
{
    [Fact]
    public void Should_produce_same_output_as_current_production()
    {
        // Arrange  --  reproduce a real scenario
        var generator = new ReportGenerator();
        var data = new ReportData
        {
            Period = new DateRange(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31)),
            Accounts = new[] { "ACC-001", "ACC-002" }
        };

        // Act
        var result = generator.Generate(data);

        // Assert  --  values captured from current production output
        result.TotalRows.ShouldBe(847);
        result.Summary.GrossReturn.ShouldBe(0.1234m, tolerance: 0.0001m);
        // NOTE: Known bug  --  commission is calculated before discount.
        // This will be fixed in JIRA-4521 with a new test for correct behavior.
        result.Summary.NetReturn.ShouldBe(0.1189m, tolerance: 0.0001m);
    }
}
```

### Characterization Tests for Data Access

Two options when code hits a database:

1. **Real test database** (preferred for integration-level characterization):
   ```csharp
   public class PortfolioRepositoryCharacterizationTests : IAsyncLifetime
   {
       private readonly MongoDbFixture _fixture = new();

       public async Task InitializeAsync() => await _fixture.SeedFrom("test-data/portfolios.json");
       public async Task DisposeAsync() => await _fixture.Dispose();

       [Fact]
       public async Task Should_return_portfolio_with_all_assets()
       {
           var repo = new PortfolioRepository(_fixture.Database);

           var result = await repo.GetById(KnownIds.Portfolio1);

           result.ShouldNotBeNull();
           result.Assets.Count.ShouldBe(5);
           result.Assets[0].Ticker.ShouldBe("PETR4");
       }
   }
   ```

2. **Extract the dependency and mock it** (when you only care about the logic around the data access)

---

## Step 3 -- Identify and Create Seams

A **seam** alters behavior without editing surrounding code. Legacy code has few seams -- find or create minimal ones.

### Pattern 1: Interface Extraction

If a dependency implements (or can be given) an interface, inject a substitute:

```csharp
// The dependency
var repository = Substitute.For<IPortfolioRepository>();
repository.GetById(Arg.Any<Guid>())
    .Returns(new Portfolio { Id = portfolioId, Balance = 50_000m });

// The system under test, now injectable
var service = new PortfolioService(repository);
```

Cleanest seam. Extracting interfaces from concrete class dependencies is a small, safe refactoring that dramatically improves testability.

### Pattern 2: Extract and Override

When a class creates its own dependencies internally, extract creation into a virtual method and override it in a test subclass. **Temporary technique** -- a stepping stone toward proper DI.

```csharp
// Production code  --  add one virtual method
public class BatchProcessor
{
    protected virtual IDatabaseConnection CreateConnection()
        => new SqlServerConnection(ConnectionStrings.Main);

    public void Process(Batch batch)
    {
        using var connection = CreateConnection();
        // ... existing logic unchanged
    }
}

// Test code  --  override the seam
public class TestableBatchProcessor : BatchProcessor
{
    private readonly IDatabaseConnection _fakeConnection;

    public TestableBatchProcessor(IDatabaseConnection connection)
        => _fakeConnection = connection;

    protected override IDatabaseConnection CreateConnection()
        => _fakeConnection;
}
```

### Pattern 3: Static Wrappers

Static calls (`DateTime.Now`, `File.ReadAllText`, `HttpClient`) kill testability. Wrap in injectable services:

```csharp
public interface ISystemClock
{
    DateTime Now { get; }
}

public class SystemClock : ISystemClock
{
    public DateTime Now => DateTime.UtcNow;
}
```

In .NET 8+, use `TimeProvider` -- the framework's built-in abstraction.

### Pattern 4: Sprout Method / Sprout Class

Adding new behavior to an untestable method:
1. Write new behavior in a **new, tested method or class**
2. Call from existing code
3. Legacy code remains untested; new logic is covered

Pragmatic -- don't need to test everything retroactively to ship safely.

---

## Step 4 -- Unit Tests on Business Rules

With seams in place and characterization tests as safety net, write tests for **expected** behavior. Pure business rules first -- easiest to test, most valuable to protect.

```csharp
public class IncomeTaxCalculationTests
{
    private readonly ISystemClock _clock;
    private readonly IncomeTaxCalculator _calculator;

    public IncomeTaxCalculationTests()
    {
        _clock = Substitute.For<ISystemClock>();
        _clock.Now.Returns(new DateTime(2025, 3, 15));
        _calculator = new IncomeTaxCalculator(_clock);
    }

    [Theory]
    [InlineData(180, 0.225)]   // Up to 180 days: 22.5%
    [InlineData(360, 0.20)]    // 181 to 360 days: 20%
    [InlineData(720, 0.175)]   // 361 to 720 days: 17.5%
    [InlineData(721, 0.15)]    // Over 720 days: 15%
    public void Should_apply_regressive_tax_rate_based_on_holding_period(
        int daysHeld,
        double expectedRate)
    {
        var operation = new RedemptionOperation
        {
            PurchaseDate = _clock.Now.AddDays(-daysHeld),
            GrossProfit = 1_000m
        };

        var result = _calculator.CalculateTax(operation);

        result.Rate.ShouldBe((decimal)expectedRate);
        result.TaxAmount.ShouldBe(1_000m * (decimal)expectedRate);
    }

    [Fact]
    public void Should_not_charge_tax_on_loss()
    {
        var operation = new RedemptionOperation
        {
            PurchaseDate = _clock.Now.AddDays(-90),
            GrossProfit = -500m
        };

        var result = _calculator.CalculateTax(operation);

        result.TaxAmount.ShouldBe(0m);
        result.IsExempt.ShouldBeTrue();
    }
}
```

`[Theory]` with `[InlineData]` eliminates duplication; Shouldly's `ShouldBe`/`ShouldBeTrue` make assertions self-documenting.

---

## Step 5 -- Integration Tests at Boundaries

Unit tests validate isolated logic. Treacherous bugs live at boundaries: serialization, database queries, messaging, HTTP calls.

```csharp
public class PortfolioRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly MongoDbFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.ResetCollection("portfolios");
    public async Task DisposeAsync() => await _fixture.Dispose();

    [Fact]
    public async Task Should_persist_and_retrieve_portfolio_with_assets()
    {
        var repository = new PortfolioRepository(_fixture.Database);
        var portfolio = new Portfolio
        {
            Holder = "John Smith",
            Assets = new List<Asset>
            {
                new() { Ticker = "AAPL", Quantity = 100, AveragePrice = 178.50m }
            }
        };

        await repository.Insert(portfolio);
        var retrieved = await repository.GetById(portfolio.Id);

        retrieved.ShouldNotBeNull();
        retrieved.Holder.ShouldBe("John Smith");
        retrieved.Assets.Count.ShouldBe(1);
        retrieved.Assets[0].Ticker.ShouldBe("AAPL");
    }
}
```

xUnit's `IAsyncLifetime` is essential for async setup/teardown in integration tests with real databases.

---

## Incremental Adoption Checklist

Order when introducing tests to existing projects:

1. [ ] Create test project(s) and add NuGet references
2. [ ] Write 2-3 characterization tests for most critical code (Red Zone)
3. [ ] Identify seams in critical code -- add interfaces or extract methods as needed
4. [ ] Unit tests for core business rules behind those seams
5. [ ] Integration tests for the most dangerous boundary (DB, external API)
6. [ ] Boy Scout Rule: every code change comes with tests from now on
7. [ ] Gradually expand to Yellow Zone modules as they're touched
