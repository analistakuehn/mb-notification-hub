# Greenfield Strategy

Tests drive the design when building new code. Every line of production code is born from a failing test. Design emerges incrementally.

---

## Phase 0 -- Test List

Before writing any test or production code, analyze the requirement and produce a **Test List**. Your map -- update constantly.

```markdown
## Test List

- [ ] Simplest happy path (the most basic possible input/output)
- [ ] Second variation (forces first generalization)
- [ ] Edge case: null/empty input
- [ ] Edge case: boundary value (zero, max, min)
- [ ] Business rule variation 1
- [ ] Business rule variation 2
- [ ] [TRICKY] Complex scenario (defer  --  the solution will emerge)
- [ ] [TRICKY] Concurrent access scenario (defer)
- [ ] [R] Refactoring: extract common setup into fixture
```

**Rules:**
- Start with simplest, most obvious cases
- Defer complex/tricky cases -- simpler cases build the solution incrementally
- Mark refactoring opportunities with `[R]`
- Present to user before starting -- they may see missed cases

---

## Phase 1  --  RED: Write a Failing Test

### Start from the Assert

Think about what the result should look like *before* thinking about how to get there. This forces you to design the public interface from the consumer's perspective.

```csharp
[Fact]
public void Should_return_I_for_1()
{
    // Start here  --  what do I want?
    result.ShouldBe("I");

    // Then work backwards  --  how do I get the result?
    var result = converter.ConvertToRoman(1);

    // Then  --  where does the converter come from?
    var converter = new RomanNumeralConverter();
}
```

Then reorder into proper Arrange-Act-Assert:

```csharp
[Fact]
public void Should_return_I_for_1()
{
    var converter = new RomanNumeralConverter();

    var result = converter.ConvertToRoman(1);

    result.ShouldBe("I");
}
```

### Design principles during RED

- **Compilation errors count as RED** -- no run needed
- **Expressive names**: `ConvertToRoman` not `Convert`, `CalculateDiscount` not `Calc`
- **Consider multiple API designs** (extension method vs class vs static) -- pick what reads best in the test
- **The test IS the specification** -- if test name/body don't clearly communicate intent, rewrite it

---

## Phase 2 -- GREEN: Make It Pass the Simplest Way

Only goal: speed. Red to Green as fast as possible.

```csharp
public string ConvertToRoman(int number)
{
    return "I"; // Fake it  --  this is correct for now
}
```

**Rules:**
- Return hardcoded values if needed (Fake It)
- Don't generalize  --  that's the Refactor phase
- Don't add code for cases that don't have a failing test yet
- Run tests  --  confirm all green

---

## Phase 3 -- REFACTOR: Improve Without Changing Behavior

Ask:
- **Duplication** to eliminate?
- **Clarity** improvement (names, structure)?
- Convert `Fact` to `Theory` with `[InlineData]`?
- Extract a private method or new class?

**Rules:**
- Refactor only when all tests green
- Run tests after each micro-change -- never accumulate
- If unsure whether to refactor now, mark `[R]` and continue

---

## Phase 4 -- REPEAT

1. Mark completed: `- [x] Case N`
2. Select next simplest case
3. Back to Phase 1

---

## Key Techniques

### Triangulation

When a fake works for one case, add a second case to force generalization:

```
Test 1: f(1) = "I"    → return "I"                (fake)
Test 2: f(2) = "II"   → if(n==1) return "I" ...   (still fake)
Test 3: f(3) = "III"  → loop emerging!             (generalization)
```

Each test exerts evolutionary pressure on the implementation. Algorithm emerges naturally.

### Transformation Priority Premise (TPP)

Prefer simple transformations before complex ones:

1. `{}→nil`  --  nothing to null/constant
2. `nil→constant`  --  null to fixed value
3. `constant→variable`  --  constant to variable
4. `unconditional→conditional`  --  add if
5. `scalar→collection`  --  value to list
6. `statement→recursion`  --  iteration to recursion
7. `if→while`  --  conditional to loop
8. `collection→composite`  --  list to data structure

### When Dependencies Appear

As design emerges and external dependencies are needed:

1. **Define the interface in the test** -- `IOrderRepository`, `IEmailSender`
2. **Create a substitute** with NSubstitute
3. **Inject via constructor**
4. Production code depends on abstractions from day one

```csharp
[Fact]
public async Task Should_save_order_and_send_confirmation()
{
    var repository = Substitute.For<IOrderRepository>();
    var emailSender = Substitute.For<IEmailSender>();
    var service = new OrderService(repository, emailSender);
    var order = new Order("ORD-001", customer: "Alice", total: 150m);

    await service.PlaceOrder(order);

    await repository.Received(1).SaveAsync(order);
    await emailSender.Received(1).SendAsync(
        Arg.Is<OrderConfirmation>(c => c.OrderId == "ORD-001"));
}
```

### When to Defer Refactoring

**Defer** (by minutes, not weeks) when:
- Next test brings more context
- No clear duplication yet
- One more case will reveal the pattern

**Don't defer** when:
- 3+ obvious duplications exist
- Code hard to read
- Extensive scrolling needed to navigate

---

## TDD Summary Template

At the end of a TDD session, provide:

```markdown
## TDD Summary

### Test List (Final)
- [x] Case 1: description
- [x] Case 2: description
- [x] [TRICKY] Case 3: description
- [x] [R] Refactoring: description

### Statistics
| Metric | Value |
|--------|-------|
| Total iterations | N |
| Tests written | N |
| Refactoring steps | N |
| Final test result | All passing |

### Design Decisions (emerged from TDD)
- [Decisions that emerged naturally from the test-driven process]

### Files Created/Modified
| File | Type | Description |
|------|------|-------------|
| MyClass.cs | Production | [what it does] |
| MyClassTests.cs | Test | [what it covers] |
```
