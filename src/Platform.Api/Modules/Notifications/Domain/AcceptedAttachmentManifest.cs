using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// The durable document that says which attachments one notification was
/// accepted over. It is written by the acceptance and read by everything that
/// comes after it.
/// <para>
/// It freezes identity and composition: which attachments make up the set, in
/// which order, which content each of them was accepted with, and the name,
/// the media type and the length each was released under. It freezes no
/// eligibility. Whether a release is still in force, whether it was taken
/// back and whether it is still within its validity are read again, every
/// time, immediately before the call that cannot be taken back, and none of
/// those answers is stored here.
/// </para>
/// <para>
/// The document is neutral on purpose. It carries no bytes, no digest, no
/// store, no key, no generation of the provider, no managed key, no address
/// and no credential. The only member that says which content was accepted is
/// the opaque handle the owning module can resolve, and it says nothing on
/// its own.
/// </para>
/// </summary>
internal static class AcceptedAttachmentManifest
{
    /// <summary>
    /// The only envelope version this reader recognises. A document that
    /// names another one is refused rather than tolerated: reading a version
    /// this code does not know would mean guessing what the members it cannot
    /// see were there to constrain.
    /// </summary>
    internal const int SchemaVersion = 1;

    /// <summary>
    /// The document is not the envelope. It does not parse, it parses as
    /// something other than the envelope object, it carries a member the
    /// envelope does not declare, it spells a member with another case, or a
    /// member arrived with a type the envelope has no reading for.
    /// </summary>
    internal const string RefusedMalformedDocument = "malformed-document";

    /// <summary>The envelope parses and names a version this reader does not know.</summary>
    internal const string RefusedUnknownSchemaVersion = "unknown-schema-version";

    /// <summary>
    /// The envelope parses, names the known version, and describes a set that
    /// cannot exist: no list at all, an empty list, a blank member, a negative
    /// length, or the same reference twice.
    /// </summary>
    internal const string RefusedUnusableSet = "unusable-set";

    /// <summary>
    /// The three refusals, and nothing else, are what a reader is allowed to
    /// say out loud about a document it cannot read.
    /// <para>
    /// The refusal names the shape of the defect and never an instance of it.
    /// Nothing inside the document comes from a closed vocabulary: a
    /// reference, a name and a media type are producer data, and quoting one
    /// into a trail would publish, on the operational side, the very values
    /// the acceptance path keeps out of its answers and its log lines. The
    /// document itself stays where it already is, on the row, for an
    /// investigation entitled to read it.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Refusals { get; } =
    [
        RefusedMalformedDocument,
        RefusedUnknownSchemaVersion,
        RefusedUnusableSet,
    ];

    /// <summary>
    /// Case-sensitive member names, no tolerance for a member the envelope
    /// does not declare, and no tolerance for the same member twice.
    /// <para>
    /// All three are what turn an unrecognised document into a refusal instead
    /// of a set that quietly lost something. A document whose extra member was
    /// ignored would read back missing whatever that member was there to
    /// constrain; a document whose repeated member was resolved by taking the
    /// last one would read back as a set the writer never wrote.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions DocumentOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    /// <summary>
    /// Writes the accepted set as the document the row stores, in the order
    /// the set was accepted in.
    /// </summary>
    public static string Serialize(AcceptedAttachmentSet accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);

        return JsonSerializer.Serialize(
            new StoredManifest
            {
                SchemaVersion = SchemaVersion,
                Items = [.. accepted.Select(item => new StoredItem
                {
                    Reference = item.Reference,
                    ContentIdentity = item.ContentIdentity,
                    Name = item.Name,
                    MediaType = item.MediaType,
                    Length = item.Length,
                })],
            },
            DocumentOptions);
    }

    /// <summary>
    /// Reads a stored document back as one of three outcomes the caller can
    /// tell apart: the set itself, the absence of a set, or a document this
    /// code cannot make sense of.
    /// <para>
    /// The three never collapse into two. Absence is the ordinary state of a
    /// notification that named no attachments, and of any row written before
    /// the column existed, and it means the notification carries none. An
    /// unreadable document means the notification carries a set nobody can
    /// name, and the one thing a caller must never do with it is fall back to
    /// the current state of the owning module: that would send a composition
    /// the acceptance never agreed to, under a notification whose producer was
    /// told a different one had been accepted.
    /// </para>
    /// <para>
    /// Only the absent value reads as absence, and blank text does not. The
    /// column holds a JSON document, so the store itself refuses text that is
    /// not one: blank text reaching this reader is a document nobody could
    /// have written rather than a row nobody wrote to, and answering absence
    /// there would turn a defect into the ordinary path with no attachments.
    /// </para>
    /// </summary>
    public static AcceptedManifestRead Read(string? manifestJson)
    {
        if (manifestJson is null)
        {
            return new AcceptedManifestRead.Absent();
        }

        StoredManifest? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredManifest>(manifestJson, DocumentOptions);
        }
        catch (JsonException)
        {
            return new AcceptedManifestRead.Unreadable(RefusedMalformedDocument);
        }

        // A document holding the JSON literal null parses without complaint
        // and describes nothing at all. It is not absence: absence is a column
        // that was never written, and this is a column that was.
        if (stored is null)
        {
            return new AcceptedManifestRead.Unreadable(RefusedMalformedDocument);
        }

        if (stored.SchemaVersion != SchemaVersion)
        {
            return new AcceptedManifestRead.Unreadable(RefusedUnknownSchemaVersion);
        }

        if (stored.Items is not { } items)
        {
            return new AcceptedManifestRead.Unreadable(RefusedUnusableSet);
        }

        try
        {
            // The published type is the authority on which sets can exist. It
            // already refuses an empty set, a blank member, a negative length
            // and a repeated reference, and a member that arrived as the JSON
            // literal null reaches it as the blank it already refuses. Stating
            // those rules again here would be a second authority free to drift
            // away from the one the claim itself is held to.
            return new AcceptedManifestRead.Present(
                AcceptedAttachmentSet.Of(items.Select(Restore)));
        }
        catch (ArgumentException)
        {
            return new AcceptedManifestRead.Unreadable(RefusedUnusableSet);
        }
    }

    /// <summary>
    /// Reads the accepted set of one notification off the row already loaded,
    /// and stops the caller when the stored document does not read.
    /// <para>
    /// The row is the only place the set is ever read from. Nothing after the
    /// acceptance keeps a copy of it, so a caller reaching anywhere else would
    /// be reading something free to disagree with what the producer was told
    /// had been accepted.
    /// </para>
    /// <para>
    /// A set and its absence both let the caller go on, and they are the two
    /// ordinary answers: a notification that named attachments carries exactly
    /// those, one that named none carries none. An unreadable document is
    /// neither, and it must not be turned into either. Read as absence, the
    /// notification would leave without the attachments it was accepted over,
    /// which is the most expensive failure this path has; read as a set, it
    /// would leave with a composition nobody can name.
    /// </para>
    /// <para>
    /// The stop is an operational defect and never an answer to the producer.
    /// Whatever asked stops before writing anything, so the state a repaired
    /// row would need is still there and the notification is held rather than
    /// lost.
    /// </para>
    /// </summary>
    /// <exception cref="AcceptedAttachmentsUnreadableException">
    /// The row carries a document this reader cannot make sense of.
    /// </exception>
    public static void RefuseUnreadable(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (Read(notification.AcceptedAttachmentsJson) is AcceptedManifestRead.Unreadable unreadable)
        {
            throw new AcceptedAttachmentsUnreadableException(notification.Id, unreadable.Reason);
        }
    }

    private static AcceptedAttachment Restore(StoredItem? item) => new()
    {
        Reference = item?.Reference ?? string.Empty,
        ContentIdentity = item?.ContentIdentity ?? string.Empty,
        Name = item?.Name ?? string.Empty,
        MediaType = item?.MediaType ?? string.Empty,
        Length = item?.Length ?? -1,
    };

    /// <summary>
    /// The envelope, with every member declared required. Required is what
    /// makes an absent member a refusal instead of a default: a document
    /// missing its length would otherwise read back as a set whose members are
    /// zero bytes long, and one missing its version as a set nobody versioned.
    /// </summary>
    private sealed class StoredManifest
    {
        [JsonRequired]
        public int SchemaVersion { get; init; }

        [JsonRequired]
        public StoredItem?[]? Items { get; init; }
    }

    private sealed class StoredItem
    {
        [JsonRequired]
        public string? Reference { get; init; }

        [JsonRequired]
        public string? ContentIdentity { get; init; }

        [JsonRequired]
        public string? Name { get; init; }

        [JsonRequired]
        public string? MediaType { get; init; }

        [JsonRequired]
        public long Length { get; init; }
    }
}

/// <summary>
/// What reading the stored attachment snapshot of one notification answered.
/// The three cases are distinguishable on purpose, and each of them owes the
/// caller a different behaviour: the set, the path with no attachments, and a
/// refusal that never becomes either of the other two.
/// </summary>
internal abstract record AcceptedManifestRead
{
    private AcceptedManifestRead()
    {
    }

    /// <summary>The notification carries a set and it reads back whole.</summary>
    public sealed record Present(AcceptedAttachmentSet Accepted) : AcceptedManifestRead;

    /// <summary>
    /// The notification carries no set: it was accepted without attachments,
    /// or its row is older than the column.
    /// </summary>
    public sealed record Absent : AcceptedManifestRead;

    /// <summary>
    /// The notification carries a document this code cannot make sense of.
    /// <see cref="Reason"/> is one of
    /// <see cref="AcceptedAttachmentManifest.Refusals"/> and never a value
    /// taken from the document.
    /// </summary>
    public sealed record Unreadable(string Reason) : AcceptedManifestRead;
}

/// <summary>
/// A path that could still reach a provider asked one notification which
/// attachments it was accepted over, and the row answered with a document
/// nothing can make sense of.
/// <para>
/// It is worded as an operational defect because that is what it is: the
/// document is written by the acceptance and never written again, so one that
/// stopped reading is corruption of a durable row, and no redelivery of the
/// same message repairs it. The path that met it wrote nothing, so repairing
/// the row is enough to let the notification carry on.
/// </para>
/// <para>
/// <see cref="Reason"/> names the shape of the defect, from the reader's
/// closed vocabulary, and never quotes the document. A reference, a name and a
/// media type are producer data, and an operational trail is exactly where
/// they must not surface.
/// </para>
/// </summary>
internal sealed class AcceptedAttachmentsUnreadableException : Exception
{
    internal AcceptedAttachmentsUnreadableException(Guid notificationId, string reason)
        : base($"A notificação {notificationId} carrega um conjunto aceito de anexos ilegível "
            + $"({reason}); nenhum caminho que possa alcançar o provedor prossegue sobre um "
            + "conjunto que ninguém consegue nomear.")
    {
        NotificationId = notificationId;
        Reason = reason;
    }

    /// <summary>The notification whose stored document does not read.</summary>
    internal Guid NotificationId { get; }

    /// <summary>One of <see cref="AcceptedAttachmentManifest.Refusals"/>.</summary>
    internal string Reason { get; }
}
