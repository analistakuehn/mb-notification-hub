using System.Net.Http.Headers;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// The closed table that says what a run of leading bytes is. It is code and
/// not configuration: the list of types an operator may admit is configuration,
/// and it may only name a type this table can recognize, so no admitted type
/// can be a type nothing detects.
/// <para>
/// Only the leading bytes are read, and nothing here opens a container, counts
/// entries or measures expansion. A byte prefix cannot tell a protected
/// document from a plain one, and this table does not pretend otherwise: it
/// answers what the bytes start with, and the verdict about the content is
/// somebody else's.
/// </para>
/// <para>
/// Deliberately absent: every format built on a container, because they share
/// one prefix and a prefix cannot separate them. Admitting one of them would
/// mean admitting all of them, which is why the table cannot name them and the
/// startup guard refuses to admit a type the table does not know.
/// </para>
/// </summary>
internal static class AttachmentContentSignatures
{
    private static readonly (string MediaType, byte[] Prefix)[] Table =
    [
        ("image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        ("image/gif", [0x47, 0x49, 0x46, 0x38]),
        ("application/pdf", [0x25, 0x50, 0x44, 0x46]),
        ("image/jpeg", [0xFF, 0xD8, 0xFF]),
    ];

    /// <summary>
    /// How many leading bytes have to be kept for the table to answer. Derived
    /// from the table so a longer signature added below cannot leave the reader
    /// holding fewer bytes than it needs.
    /// </summary>
    internal static int MaxPrefixLength { get; } = Table.Max(entry => entry.Prefix.Length);

    /// <summary>Every type this table can recognize.</summary>
    internal static IReadOnlyCollection<string> Known { get; } =
        [.. Table.Select(entry => entry.MediaType).Order(StringComparer.Ordinal)];

    /// <summary>
    /// What the bytes start with, or nothing when no signature matches. The
    /// table is walked from the longest signature down, so a shorter one can
    /// never answer for bytes a longer one describes.
    /// </summary>
    internal static string? Detect(ReadOnlySpan<byte> prefix)
    {
        foreach ((var mediaType, var signature) in Table)
        {
            if (prefix.Length >= signature.Length
                && prefix[..signature.Length].SequenceEqual(signature))
            {
                return mediaType;
            }
        }

        return null;
    }

    /// <summary>
    /// The media type alone, lowered, with the parameters a declaration may
    /// carry dropped. Comparing declarations as written would call the same
    /// type two different types over a charset nobody meant to declare.
    /// </summary>
    internal static string? Canonical(string? declared)
        => MediaTypeHeaderValue.TryParse(declared, out MediaTypeHeaderValue? parsed)
            && parsed.MediaType is { Length: > 0 } mediaType
                ? mediaType.ToLowerInvariant()
                : null;

    internal static bool Knows(string? mediaType)
        => Canonical(mediaType) is { } canonical && Known.Contains(canonical);
}
