using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// Memoization behind the published read contracts. Two families of entries:
/// the "current published" pointers, valid for a short window because a
/// publish may move them at any time; and per-version values, immutable by
/// the governance contract and therefore never expired. Both live in memory
/// per process: workers converge on a new publication within the pointer
/// window, which is the accepted staleness.
/// </summary>
/// <remarks>
/// Each family owns a store with its own budget, so pointer traffic can never
/// push a pinned layout out of memory. At the ceiling the store refuses the
/// arriving entry and schedules a compaction that frees a slice of the budget:
/// a burst of distinct keys costs a slice of the working set instead of all of
/// it, and the entries that survive keep answering while the slice is freed.
/// </remarks>
internal sealed class PublishedReadCache : IDisposable
{
    internal static readonly TimeSpan PointerLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Entries each family holds. Every entry weighs one, so the budget is a count.</summary>
    internal const int MaxEntries = 4096;

    /// <summary>Share of the budget one compaction pass frees once the ceiling is reached.</summary>
    internal const double CompactionShare = 0.05;

    /// <summary>
    /// Shared on purpose: the store copies the fields onto the entry and never
    /// writes back, and the pointer path runs on every published read.
    /// </summary>
    private static readonly MemoryCacheEntryOptions PointerEntry = new()
    {
        Size = 1,
        AbsoluteExpirationRelativeToNow = PointerLifetime,
    };

    private static readonly MemoryCacheEntryOptions ImmutableEntry = new() { Size = 1 };

    private readonly MemoryCache _pointers;
    private readonly MemoryCache _immutable;
    private long _pointerHits;
    private long _pointerLoads;

    public PublishedReadCache(TimeProvider timeProvider)
    {
        var clock = new TimeProviderSystemClock(timeProvider);
        _pointers = Build(clock);
        _immutable = Build(clock);
    }

    /// <summary>How many pointer lookups were answered from memory. Observability for tests.</summary>
    internal long PointerHits => Interlocked.Read(ref _pointerHits);

    /// <summary>How many pointer lookups had to load. Observability for tests.</summary>
    internal long PointerLoads => Interlocked.Read(ref _pointerLoads);

    /// <summary>
    /// Pointer entries resident right now. Reading it locks the whole store, so
    /// it stays out of the read path and exists for tests.
    /// </summary>
    internal int PointerCount => _pointers.Count;

    /// <summary>
    /// Per-version entries resident right now. Reading it locks the whole store,
    /// so it stays out of the read path and exists for tests.
    /// </summary>
    internal int ImmutableCount => _immutable.Count;

    /// <summary>A "current published" value, when the pointer window still covers it.</summary>
    internal bool TryGetPointer<T>(string key, out T value)
        where T : class
    {
        if (_pointers.TryGetValue(key, out var stored) && stored is T typed)
        {
            Interlocked.Increment(ref _pointerHits);
            value = typed;
            return true;
        }

        Interlocked.Increment(ref _pointerLoads);
        value = null!;
        return false;
    }

    internal void SetPointer<T>(string key, T value)
        where T : class
        => _pointers.Set(key, value, PointerEntry);

    /// <summary>A per-version value; versions are immutable, so entries never expire.</summary>
    internal bool TryGetImmutable<T>(string key, out T value)
        where T : class
    {
        if (_immutable.TryGetValue(key, out var stored) && stored is T typed)
        {
            value = typed;
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>
    /// Keeps the value that arrived first, because callers hold the instance
    /// they read and a replacement would hand two callers two objects for one
    /// immutable version. Two writers that miss at the same instant still race,
    /// and the loser's instance is the one already in flight.
    /// </summary>
    internal void SetImmutable<T>(string key, T value)
        where T : class
    {
        if (_immutable.TryGetValue(key, out _))
        {
            return;
        }

        _immutable.Set(key, value, ImmutableEntry);
    }

    /// <summary>
    /// Frees the slots of pointers whose window closed, which the store would
    /// otherwise reclaim on its own schedule. Tests call it to stop depending
    /// on that schedule.
    /// </summary>
    internal void ReclaimExpiredPointers() => _pointers.Compact(0);

    public void Dispose()
    {
        _pointers.Dispose();
        _immutable.Dispose();
    }

    private static MemoryCache Build(ISystemClock clock)
        => new(Options.Create(new MemoryCacheOptions
        {
            SizeLimit = MaxEntries,
            CompactionPercentage = CompactionShare,
            Clock = clock,
        }));

    /// <summary>
    /// Bridges the injected clock onto the one seam the memory store exposes.
    /// Without it the store would expire pointers against the machine clock and
    /// nothing could move the window under a test.
    /// </summary>
    private sealed class TimeProviderSystemClock(TimeProvider timeProvider) : ISystemClock
    {
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    }
}
