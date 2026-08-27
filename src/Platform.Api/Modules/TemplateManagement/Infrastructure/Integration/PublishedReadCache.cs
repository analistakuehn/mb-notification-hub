using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// Memoization behind the published read contracts. Two families of entries:
/// the "current published" pointers, valid for a short window because a
/// publish may move them at any time; and per-version values, immutable by
/// the governance contract and therefore never expired. Both live in memory
/// per process: the process that commits a lifecycle transition drops the
/// pointers that transition names, right after the commit, and every other
/// process converges on the new state within the pointer window. The window
/// stays the guaranteed bound, because the commands run in the API host and
/// the renders run in the workers.
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
    private long _generation;
    private long _pointerHits;
    private long _pointerLoads;

    public PublishedReadCache(TimeProvider timeProvider)
    {
        var clock = new TimeProviderSystemClock(timeProvider);
        _pointers = Build(clock);
        _immutable = Build(clock);
    }

    /// <summary>
    /// The fence a reader captures before it goes to the store and hands back
    /// to <see cref="SetPointerIfCurrent{T}" />. It counts invalidations for
    /// every pointer key at once, and that is deliberate: an invalidation also
    /// discards the writes in flight for keys it did not name, which costs a
    /// few cold loads and buys a design with a single counter. The transitions
    /// that move it are human governance and rare, so those loads are rare
    /// with them.
    /// </summary>
    internal long Generation => Interlocked.Read(ref _generation);

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

    /// <summary>
    /// Writes a pointer without reading the fence. No published read writes
    /// through it: every reader captures <see cref="Generation" /> before its
    /// query leaves and lands on <see cref="SetPointerIfCurrent{T}" />. This
    /// one exists to seed a store directly, and the contention probe binds it
    /// by name, so renaming it or turning it into an overload breaks that
    /// binding at run time and not at compile time.
    /// </summary>
    internal void SetPointer<T>(string key, T value)
        where T : class
        => _pointers.Set(key, value, PointerEntry);

    /// <summary>
    /// Memoizes a "current published" value under the fence its reader
    /// captured before loading. It closes the read-old-write-late race, where
    /// a load whose query left before a transition committed puts the
    /// superseded value back for another whole window, on a key the process
    /// running the command reads often.
    /// <para>
    /// The fence is read twice, and both readings are load bearing. The first
    /// refuses a write whose transition already landed. The second covers the
    /// interval the first cannot see, between the check and the end of the
    /// write: an invalidation that lands there would leave the superseded
    /// value resident, which is the same harm through a narrower door. Every
    /// interleaving is covered by one of the two, or by the removal of the
    /// invalidation itself, which always follows the increment that the second
    /// reading observes.
    /// </para>
    /// <para>
    /// The removal below can also drop a fresher value that a concurrent load
    /// wrote in that same interval. That costs one cold load and never answers
    /// a superseded value, which is the trade this surface takes. It runs on
    /// the miss path only, after a query to the store.
    /// </para>
    /// </summary>
    internal void SetPointerIfCurrent<T>(string key, T value, long generation)
        where T : class
    {
        if (generation != Interlocked.Read(ref _generation))
        {
            return;
        }

        _pointers.Set(key, value, PointerEntry);

        if (generation != Interlocked.Read(ref _generation))
        {
            _pointers.Remove(key);
        }
    }

    /// <summary>
    /// Drops one pointer key, in this process, for a transition that already
    /// committed. The order of the two statements is load bearing and must not
    /// be swapped: incrementing first means a reader that reads the fence from
    /// here on is refused its write, and a reader already past its check is
    /// caught by the second reading in <see cref="SetPointerIfCurrent{T}" />.
    /// Removing first would open the whole interval between the removal and
    /// the increment, in which a stale write passes both readings and stays
    /// resident for a full window.
    /// </summary>
    internal void InvalidatePointer(string key)
    {
        Interlocked.Increment(ref _generation);
        _pointers.Remove(key);
    }

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
