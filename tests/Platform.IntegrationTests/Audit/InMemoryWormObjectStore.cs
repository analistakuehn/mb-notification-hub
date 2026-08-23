using System.Collections.Concurrent;
using System.Security.Cryptography;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// Evidence sink for tests that exercise the maintenance round without an
/// object store. It keeps the contract honest (head, get, put) and nothing
/// else: tests that assert what the real store does, including the immutable
/// retention it declares, run against the emulator instead.
/// </summary>
internal sealed class InMemoryWormObjectStore : IWormObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => [.. _objects.Keys];

    public Task<WormObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
        => Task.FromResult(_objects.TryGetValue(key, out var content)
            ? new WormObjectHead(key, Convert.ToHexStringLower(SHA256.HashData(content)), content.Length)
            : null);

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
        => Task.FromResult(_objects.TryGetValue(key, out var content) ? content : null);

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        _objects[key] = content;
        return Task.CompletedTask;
    }
}
