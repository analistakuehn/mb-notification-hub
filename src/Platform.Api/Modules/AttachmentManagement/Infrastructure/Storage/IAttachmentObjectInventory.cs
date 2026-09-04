namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

internal enum AttachmentKeyInventoryStatus
{
    /// <summary>The store answered, and the answer is complete for that key.</summary>
    Listed,

    /// <summary>
    /// The store could not be reached, or answered only part of what it holds.
    /// A partial answer is not an inventory: acting on it would treat a
    /// generation the store never mentioned as one that is not there.
    /// </summary>
    Unavailable,
}

/// <summary>
/// What the store holds under one attachment's key, generation by generation.
/// </summary>
internal sealed record AttachmentKeyInventory(
    AttachmentKeyInventoryStatus Status,
    IReadOnlyList<AttachmentObjectLocator> Generations)
{
    internal static AttachmentKeyInventory Listed(IReadOnlyList<AttachmentObjectLocator> generations)
        => new(AttachmentKeyInventoryStatus.Listed, generations);

    internal static AttachmentKeyInventory Unavailable()
        => new(AttachmentKeyInventoryStatus.Unavailable, []);
}

/// <summary>
/// Reads back what a key holds, which is the one question the custody contract
/// deliberately refuses to answer.
/// <para>
/// It is a port of its own and not a method on the custody, because the two
/// make opposite promises. Custody names an exact generation on every call
/// after the write, so that no caller can reach whatever the key points at now;
/// this exists precisely to find the generations no caller can name, and giving
/// it to every holder of the custody contract would put an enumeration in the
/// hands of the request path that accepts producer bytes.
/// </para>
/// </summary>
internal interface IAttachmentObjectInventory
{
    /// <summary>
    /// Every generation the store holds under the key one attachment derives.
    /// Delete markers are left out: they are not bytes, and removing one would
    /// uncover the generation underneath it rather than end anything.
    /// </summary>
    Task<AttachmentKeyInventory> ListAsync(Guid contentId, CancellationToken cancellationToken);
}
