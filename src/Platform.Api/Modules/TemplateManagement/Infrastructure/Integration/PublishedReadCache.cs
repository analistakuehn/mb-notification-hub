using System.Collections.Concurrent;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// Memoization behind the published read contracts. Two families of entries:
/// the "current published" pointers, valid for a short window because a
/// publish may move them at any time; and per-version values, immutable by
/// the governance contract and therefore never expired. Both live in memory
/// per process: workers converge on a new publication within the pointer
/// window, which is the accepted staleness.
/// </summary>
internal sealed class PublishedReadCache(TimeProvider timeProvider)
{
    internal static readonly TimeSpan PointerLifetime = TimeSpan.FromSeconds(60);

    private const int MaxEntries = 4096;

    private readonly ConcurrentDictionary<string, PointerEntry> _pointers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _immutable = new(StringComparer.Ordinal);
    private long _pointerHits;
    private long _pointerLoads;

    /// <summary>How many pointer lookups were answered from memory. Observability for tests.</summary>
    internal long PointerHits => Interlocked.Read(ref _pointerHits);

    /// <summary>How many pointer lookups had to load. Observability for tests.</summary>
    internal long PointerLoads => Interlocked.Read(ref _pointerLoads);

    /// <summary>A "current published" value, when the pointer window still covers it.</summary>
    internal bool TryGetPointer<T>(string key, out T value)
        where T : class
    {
        if (_pointers.TryGetValue(key, out PointerEntry? entry)
            && entry.ExpiresAt > timeProvider.GetUtcNow()
            && entry.Value is T typed)
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
    {
        if (_pointers.Count >= MaxEntries)
        {
            _pointers.Clear();
        }

        _pointers[key] = new PointerEntry(value, timeProvider.GetUtcNow() + PointerLifetime);
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

    internal void SetImmutable<T>(string key, T value)
        where T : class
    {
        if (_immutable.Count >= MaxEntries)
        {
            _immutable.Clear();
        }

        _immutable.TryAdd(key, value);
    }

    private sealed record PointerEntry(object Value, DateTimeOffset ExpiresAt);
}
