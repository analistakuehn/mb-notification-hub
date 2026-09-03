namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// The published handle for the content of one recorded generation, and the
/// way back from it to that generation.
/// <para>
/// The snapshot that leaves this module has to say which bytes were accepted,
/// and it may not say it with the proof of those bytes. The algorithm, the
/// digest and the coordinates stay on the generation row; what crosses is this
/// handle, and a caller that hands it back gets the comparison done here,
/// against that row, with only a verdict coming out. That is what lets the
/// composition be frozen outside the module while the proof stays inside it.
/// </para>
/// <para>
/// It is the generation identifier under a prefix of its own, and the prefix
/// is what makes it a handle rather than a key. That identifier is random, it
/// is not the object key, and it never was: the key is built from another
/// random value that lives on the aggregate and leaves through nothing. Handing
/// out this transform reveals that a row exists and when it was minted, which
/// is what the public reference of the attachment already reveals about the
/// attachment itself.
/// </para>
/// <para>
/// A minted column beside the identifier would be the other shape, and it buys
/// nothing today: it would be a second random value with the same lifetime and
/// the same one-to-one tie to the row, and it would grow a column set that is
/// frozen and asks for a written reason for every addition. It becomes the
/// answer the day a published handle has to be rotated without rotating the
/// row it names, and this is the note that says where to start.
/// </para>
/// </summary>
internal static class AttachmentContentIdentity
{
    /// <summary>
    /// Prefix of a content handle. It is not the prefix of an attachment
    /// reference, and the difference is load-bearing: the two are handed out
    /// by the same module to the same consumer, and one of them names an
    /// attachment while the other names the bytes it was accepted with.
    /// </summary>
    internal const string Prefix = "aci_";

    internal const int Length = 36;

    /// <summary>
    /// The handle for one recorded generation. It is handed the whole row
    /// rather than the identifier of it, because the row is what the caller
    /// holds and because a handle built from anything else on that row is a
    /// change this signature admits and a rule can then refuse. A parameter
    /// that could only ever be the identifier would make that rule agree
    /// with itself.
    /// </summary>
    internal static string For(AttachmentObjectGeneration generation)
        => For(generation.Id);

    /// <summary>
    /// The same handle, for the reader that never materializes the row. The
    /// claim reads the released generation with its own statement on the
    /// caller's transaction, so it holds the identifier and nothing else, and
    /// spelling the format a second time there is what would let the two
    /// spellings drift.
    /// <para>
    /// The row-taking form above stays the one the rule is written against,
    /// and it delegates here, so a handle minted from an identifier is the
    /// same handle that rule refuses to let carry anything else. An oracle
    /// holds the two forms equal, which is what keeps this one covered by a
    /// rule it does not run through.
    /// </para>
    /// </summary>
    internal static string For(Guid generationId)
        => $"{Prefix}{generationId:N}";

    /// <summary>
    /// The generation a handle names, or nothing when the text is not a handle
    /// this module minted. Text of the wrong length, text carrying another
    /// prefix and text whose body is not an identifier all leave through the
    /// same answer, because a handle that cannot be resolved names no
    /// generation whatever the reason.
    /// </summary>
    internal static Guid? GenerationOf(string? value)
        => value is { Length: Length }
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value.AsSpan(Prefix.Length), "N", out Guid generationId)
                ? generationId
                : null;
}
