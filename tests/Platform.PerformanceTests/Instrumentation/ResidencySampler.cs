using System.Diagnostics;

namespace NotificationHub.PerformanceTests.Instrumentation;

/// <summary>Highest managed heap and working set seen while one arm ran.</summary>
internal sealed record ResidencyPeak(long HeapBytes, long WorkingSetBytes, int Samples);

/// <summary>
/// Samples what the process is holding while an arm runs, and reports the
/// highest reading of each.
/// <para>
/// It exists because the difference between a reading taken before the arm and
/// one taken after it is not a cost: measured on this work the difference comes
/// out negative, since the collector is free to return more than the arm asked
/// for, and a quantity that takes negative values carries no upper limit. The
/// peak of a reading taken during the arm does carry one.
/// </para>
/// </summary>
internal sealed class ResidencySampler : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private readonly Process _process;
    private long _peakHeap;
    private long _peakWorkingSet;
    private int _samples;

    private ResidencySampler(TimeSpan interval)
    {
        _process = Process.GetCurrentProcess();
        _loop = Task.Run(() => SampleAsync(interval, _stopping.Token));
    }

    /// <summary>
    /// Ten milliseconds: fast enough that an operation of a few milliseconds is
    /// crossed by a reading, slow enough that the sampler itself does not show
    /// up in the CPU of the arm it watches.
    /// </summary>
    internal static ResidencySampler Start() => new(TimeSpan.FromMilliseconds(10));

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _process.Dispose();
    }

    /// <summary>Stops the loop and returns what it saw. Never returns zero samples.</summary>
    internal async Task<ResidencyPeak> StopAsync()
    {
        Observe();
        await _stopping.CancelAsync();
        await _loop;
        return new ResidencyPeak(
            Volatile.Read(ref _peakHeap),
            Volatile.Read(ref _peakWorkingSet),
            Volatile.Read(ref _samples));
    }

    private async Task SampleAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Observe();
            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Observe()
    {
        _process.Refresh();
        SetMaximum(ref _peakHeap, GC.GetTotalMemory(forceFullCollection: false));
        SetMaximum(ref _peakWorkingSet, _process.WorkingSet64);
        Interlocked.Increment(ref _samples);
    }

    private static void SetMaximum(ref long target, long candidate)
    {
        var observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref target, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
