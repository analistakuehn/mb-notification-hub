namespace NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

/// <summary>
/// Whether a set that was already accepted still fits what one notification is
/// allowed to carry. Two answers, and only one of them lets the set be used.
/// </summary>
public enum AttachmentEnvelopeVerdict
{
    /// <summary>
    /// The set does not fit: it holds more attachments than a notification may
    /// carry, or its members add up to more than the envelope allows.
    /// <para>
    /// One word for the two, because the difference between them changes
    /// nothing the caller does and both are read off the same snapshot the
    /// caller already holds: whoever needs to know which of the two closed
    /// counts the set on the row.
    /// </para>
    /// <para>
    /// It is the value zero on purpose. A verdict nobody produced, and a
    /// stand-in that was never told what to answer, both read as this one, and
    /// the alternative is a default that clears a set for delivery because
    /// nobody measured it.
    /// </para>
    /// </summary>
    Exceeded = 0,

    /// <summary>
    /// The set holds no more attachments than are allowed and its members add
    /// up to no more than the envelope allows.
    /// </summary>
    WithinEnvelope,
}

/// <summary>
/// Measures an accepted set against the capacity one notification is allowed
/// to carry, immediately before the call that cannot be taken back.
/// <para>
/// The capacity is an approved value this module holds and nobody else does,
/// which is why the measurement happens here and only its verdict crosses. A
/// consumer handed the two numbers would be a second place the arithmetic
/// lives, free to disagree with this one the day either number moves.
/// </para>
/// <para>
/// It reads the lengths the snapshot froze and never the current state of an
/// attachment, so it says whether the set as accepted fits, and it opens no
/// content and calls no provider. A set may be measured long after it was
/// accepted and answer differently, because the capacity in force is read now:
/// a ceiling that was tightened after an acceptance is exactly the case this
/// exists for.
/// </para>
/// </summary>
public interface IAttachmentEnvelopeCheck
{
    AttachmentEnvelopeVerdict Measure(AcceptedAttachmentSet accepted);
}
