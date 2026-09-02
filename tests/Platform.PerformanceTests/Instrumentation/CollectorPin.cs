using System.Runtime;

namespace NotificationHub.PerformanceTests.Instrumentation;

/// <summary>
/// The collector the run happened under, read from the collector and not from
/// the file that asked for it.
/// <para>
/// The heap count is here because the mode alone does not describe a
/// configuration: the server collector on this host opens one heap per core
/// unless a count is pinned, and the same arm measured against two heaps and
/// against twenty-two answers with latencies that differ by more than most
/// regressions a gate is meant to catch. The deployment target of this hub is
/// a container of one processor, so the count is pinned at one, recorded, and
/// compared for exact equality.
/// </para>
/// <para>
/// The pin is what makes the number reproducible, not what makes it readable:
/// the effective count is reported here whether it was pinned in the runtime
/// configuration, forced by the environment, or derived from the machine, and
/// the environment wins over the configuration. That is what gives the check
/// a reachable red path.
/// </para>
/// </summary>
internal static class CollectorPin
{
    private const string HeapCountVariable = "HeapCount";

    /// <summary>Heap count the deployment target implies: one processor, one heap.</summary>
    internal const int RatifiedHeapCount = 1;

    internal static bool ServerGarbageCollection => GCSettings.IsServerGC;

    internal static string LatencyMode => GCSettings.LatencyMode.ToString();

    /// <summary>Heaps the collector is actually running with, or zero when it will not say.</summary>
    internal static int HeapCount
        => GC.GetConfigurationVariables().TryGetValue(HeapCountVariable, out var value) && value is long count
            ? (int)count
            : 0;

    /// <summary>Time the process spent stopped for collection, since it started.</summary>
    internal static TimeSpan TotalPause => GC.GetTotalPauseDuration();
}
