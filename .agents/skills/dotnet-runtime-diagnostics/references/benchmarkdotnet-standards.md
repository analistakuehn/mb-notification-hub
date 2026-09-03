# BenchmarkDotNet Diagnostic Standards

Loaded on demand by `dotnet-runtime-diagnostics` when benchmark evidence is required.

## Configuration rules

1. **Job configuration** -- appropriate runtimes, JIT settings, GC modes.
2. **Statistical validity** -- adequate warmup and measurement runs.
3. **Memory diagnostics** -- always `[MemoryDiagnoser]`.
4. **Baseline comparison** -- `[Benchmark(Baseline = true)]`.
5. **Realistic data** -- representative sizes and patterns.
6. **Isolation** -- eliminate external deps that introduce variance.
7. **Categories** -- `[BenchmarkCategory]` for organization.
8. **Parameterization** -- `[Params]` for input-size sweeps.

## Reference benchmark template

```csharp
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class ExampleBenchmarks
{
    [Params(100, 1000, 10000)]
    public int Size { get; set; }

    private int[] _data = null!;

    [GlobalSetup]
    public void Setup() => _data = Enumerable.Range(0, Size).ToArray();

    [Benchmark(Baseline = true)]
    public int Baseline() => _data.Where(x => x % 2 == 0).Sum();

    [Benchmark]
    public int Optimized()
    {
        var sum = 0;
        foreach (var item in _data.AsSpan())
            if (item % 2 == 0) sum += item;
        return sum;
    }
}
```
