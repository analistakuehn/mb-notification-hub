using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Pins exactly one durable generation of a stored object. The value is only
/// usable when it names a store, a key and a generation. There are two ways
/// in, one from what the store answered and one from a durable row, and both
/// run the same rejection, so no caller can assemble a locator that points at
/// whatever is current.
/// </summary>
internal sealed record AttachmentObjectLocator
{
    /// <summary>Longest bucket name the provider documents.</summary>
    internal const int MaxStoreLength = 63;

    /// <summary>Longest object key the provider documents.</summary>
    internal const int MaxKeyLength = 1024;

    /// <summary>Longest generation identifier the provider documents.</summary>
    internal const int MaxVersionLength = 1024;

    /// <summary>
    /// Stands in for the whole value in any text rendering. A locator is a
    /// storage coordinate and never belongs in a log line or a response.
    /// </summary>
    internal const string Redacted = "attachment-object";

    // Measured: the rendering a record synthesizes covers public members only,
    // so today, with every member internal, it would print nothing anyway. The
    // override is not a barrier against that rendering; it pins the coordinate
    // free text against the day a member turns public.

    private const string AbsentGeneration = "null";

    private AttachmentObjectLocator(string store, string key, string version)
    {
        Store = store;
        Key = key;
        Version = version;
    }

    internal string Store { get; }

    internal string Key { get; }

    internal string Version { get; }

    /// <summary>
    /// Builds a locator from what the store answered. A generation the store
    /// did not name arrives here as null, as blank text, or as the four-letter
    /// word the provider uses for "no generation at all", and none of them
    /// pins a generation: the first two name nothing and the third is a
    /// pointer the next write moves.
    /// </summary>
    internal static Result<AttachmentObjectLocator> Create(
        string? store,
        string? key,
        string? version)
        => IsUsable(store, MaxStoreLength)
            && IsUsable(key, MaxKeyLength)
            && IsUsable(version, MaxVersionLength)
            && !IsAbsentGeneration(version)
                ? Result.Success(new AttachmentObjectLocator(store!, key!, version!))
                : Result.ValidationError<AttachmentObjectLocator>(ErrorCodes.StoreUnavailable);

    /// <summary>
    /// Rebuilds a locator from a durable row, through the same rejection the
    /// construction above applies.
    /// <para>
    /// This is the second way into the type and it used to be the way without
    /// a guard, which made the single construction point above a claim the
    /// type did not keep. Measured, that opening had a consequence: an empty
    /// generation reaching removal makes the store place a delete marker
    /// instead of removing anything, and the marker is what lets the
    /// conditional write place a second durable generation under the same key.
    /// A row that cannot name a generation is a defect in stored state, and it
    /// fails here instead of travelling.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The stored triple does not name a store, a key and a generation.
    /// </exception>
    internal static AttachmentObjectLocator FromStoredRow(string store, string key, string version)
    {
        Result<AttachmentObjectLocator> locator = Create(store, key, version);
        return locator.IsSuccess && locator.Value is { } restored
            ? restored
            : throw new InvalidOperationException(
                "A stored attachment generation does not name a store, a key and a generation.");
    }

    public override string ToString() => Redacted;

    private static bool IsUsable(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static bool IsAbsentGeneration(string? value)
        => string.Equals(value, AbsentGeneration, StringComparison.OrdinalIgnoreCase);
}
