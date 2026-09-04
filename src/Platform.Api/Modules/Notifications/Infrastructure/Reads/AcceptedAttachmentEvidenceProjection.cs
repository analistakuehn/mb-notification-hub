using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;

/// <summary>
/// The two members the accepted set contributes to one evidence answer. They
/// travel together because exactly one of them is ever set, and a caller that
/// held only the first could not tell a notification with no attachments from
/// one whose document nobody can read.
/// </summary>
internal readonly record struct AcceptedAttachmentsEvidenceView(
    IReadOnlyList<AcceptedAttachmentEvidence>? Accepted,
    string? Refusal);

/// <summary>
/// Turns what the notification row says about its accepted set, and what the
/// owning module still records about the content of each member, into the
/// evidence answer.
/// <para>
/// The row is the authority and this projection never reaches past it. The
/// composition, the order and the values the release was granted over come from
/// the document the acceptance wrote, so an auditor is told what the producer
/// was told, and not what the owning module happens to hold today. That module
/// answers one question only, about the exact content each frozen handle names.
/// </para>
/// <para>
/// The three outcomes of reading the document stay three. Absence is a fact and
/// answers with an empty set; a document that does not read is ignorance and
/// answers with no set at all plus the shape of the defect. Collapsing the
/// second into the first would report a notification that carried nothing,
/// which is the one sentence an evidence surface must never produce about a
/// notification whose attachments nobody can name.
/// </para>
/// </summary>
internal static class AcceptedAttachmentEvidenceProjection
{
    /// <summary>The handles to ask the owning module about, in the frozen order.</summary>
    internal static IReadOnlyList<string> HandlesOf(AcceptedManifestRead stored)
        => stored is AcceptedManifestRead.Present present
            ? [.. present.Accepted.Select(item => item.ContentIdentity)]
            : [];

    internal static AcceptedAttachmentsEvidenceView Project(
        AcceptedManifestRead stored,
        IReadOnlyDictionary<string, AttachmentEvidence> recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);

        return stored switch
        {
            AcceptedManifestRead.Present present => new AcceptedAttachmentsEvidenceView(
                [.. present.Accepted.Select(item => ToMember(item, recorded))],
                null),

            // The empty list is the answer and it is not the absence of one:
            // the notification named no attachments, and that is a fact the
            // auditor is entitled to read as such.
            AcceptedManifestRead.Absent => new AcceptedAttachmentsEvidenceView([], null),
            AcceptedManifestRead.Unreadable unreadable =>
                new AcceptedAttachmentsEvidenceView(null, unreadable.Reason),
            _ => throw new ArgumentOutOfRangeException(nameof(stored)),
        };
    }

    private static AcceptedAttachmentEvidence ToMember(
        AcceptedAttachment item,
        IReadOnlyDictionary<string, AttachmentEvidence> recorded)
        => new()
        {
            Reference = item.Reference,
            ContentIdentity = item.ContentIdentity,
            Name = item.Name,
            MediaType = item.MediaType,
            Length = item.Length,

            // A handle the owning module no longer answers for leaves this half
            // absent. Filling it with anything at all, including a shape with
            // empty values, would state that the content was recorded and read
            // back as nothing.
            Recorded = recorded.TryGetValue(item.ContentIdentity, out AttachmentEvidence? found)
                ? found
                : null,
        };
}
