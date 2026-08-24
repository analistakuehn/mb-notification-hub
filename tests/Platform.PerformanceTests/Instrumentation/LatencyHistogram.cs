namespace NotificationHub.PerformanceTests.Instrumentation;

/// <summary>
/// Percentiles over the raw samples of one phase. The probe keeps every sample
/// instead of bucketing: the runs are thousands of appends, not millions, and
/// an exact tail matters more here than the memory a bucketed histogram would
/// save. A cell with few samples reports its count so the reader can discount
/// its tail instead of trusting a p99 built from twenty values.
/// </summary>
internal sealed class LatencyHistogram
{
    private readonly List<double> _samples = [];

    private double[]? _sorted;

    internal int Count => _samples.Count;

    internal void Add(double milliseconds)
    {
        _samples.Add(milliseconds);
        _sorted = null;
    }

    internal double Percentile(double percentile)
    {
        if (_samples.Count == 0)
        {
            return double.NaN;
        }

        var sorted = Sorted();
        var rank = (percentile / 100.0) * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
    }

    internal double Mean() => _samples.Count == 0 ? double.NaN : _samples.Average();

    internal double Max() => _samples.Count == 0 ? double.NaN : _samples.Max();

    internal PhaseStatistics Snapshot() => new(
        _samples.Count,
        Percentile(50),
        Percentile(95),
        Percentile(99),
        Max(),
        Mean());

    private double[] Sorted()
    {
        if (_sorted is null)
        {
            _sorted = [.. _samples];
            Array.Sort(_sorted);
        }

        return _sorted;
    }
}

/// <summary>Frozen percentiles of one phase, in milliseconds.</summary>
internal sealed record PhaseStatistics(int Samples, double P50, double P95, double P99, double Max, double Mean);
