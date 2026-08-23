namespace NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

/// <summary>
/// What the store knows about an object without downloading it: its length
/// and the digest the writer recorded next to it. The digest is what makes a
/// rerun cheap and safe, it decides whether the object already carries exactly
/// the bytes this round would write.
/// </summary>
internal sealed record WormObjectHead(string Key, string? Sha256Hex, long Length);

/// <summary>
/// Write-once evidence store of this module. The contract is deliberately
/// small (head, get, put) and module-owned rather than platform-owned: the
/// audit trail is its only consumer today, and a second consumer is what would
/// justify promoting it to the platform.
/// </summary>
/// <remarks>
/// Implementations write with the retention the bucket enforces; the caller
/// never deletes and never overwrites deliberately, because the point of the
/// destination is that neither is possible.
/// </remarks>
internal interface IWormObjectStore
{
    /// <summary>Metadata of an object, or null when the key holds nothing.</summary>
    Task<WormObjectHead?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Full content of an object, or null when the key holds nothing. A
    /// missing object is an expected answer here: following a manifest link
    /// that does not resolve is exactly how a removed export is detected.
    /// </summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Writes an object under the configured immutable retention.</summary>
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken);
}
