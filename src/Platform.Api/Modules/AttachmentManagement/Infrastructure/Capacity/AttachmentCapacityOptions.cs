namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;

/// <summary>
/// How much a notification may carry in attachments: the ceiling of one
/// attachment, the ceiling of the whole set, and how many attachments the set
/// may hold. All three are approved values and none of them is this module's to
/// choose, which is why none of them is written as a constant anywhere in the
/// source.
/// <para>
/// Unlike the policy section next to it, nothing here has a default that
/// closes. A ceiling nobody configured cannot mean zero: zero refuses every
/// notification that carries an attachment instead of refusing the attachment,
/// which is a product decision taken by an omission. So zero is the mark of a
/// value nobody set, the startup guard refuses it, and the process does not
/// come up without the section.
/// </para>
/// </summary>
public sealed class AttachmentCapacityOptions
{
    public const string SectionName = "Modules:AttachmentManagement:Capacity";

    /// <summary>
    /// The largest single attachment the module accepts, in raw content bytes.
    /// It is checked at registration, which is before the producer spends the
    /// transfer, and never only after the bytes have arrived.
    /// </summary>
    public long MaxAttachmentBytes { get; init; }

    /// <summary>
    /// The largest set one notification may carry, as the sum of the raw
    /// content of its attachments. What limits the cost of a send is this sum
    /// and not the size of any one member, so the per attachment ceiling is
    /// bounded by it and never the other way round.
    /// </summary>
    public long MaxEnvelopeBytes { get; init; }

    /// <summary>
    /// How many attachments one notification may carry. The sum above already
    /// bounds the bytes, so what this bounds is cardinality: how many rows an
    /// acceptance holds at once, and how many whole reads happen before the
    /// point of no return.
    /// </summary>
    public int MaxAttachmentsPerNotification { get; init; }
}
