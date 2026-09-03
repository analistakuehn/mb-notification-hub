namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

/// <summary>
/// The fine details this module writes to durable state when it refuses or
/// waits. They are not the public vocabulary: every refusal below leaves under
/// the single public reason for content, and which of them it was is read from
/// the attachment by the authorized query.
/// <para>
/// The taxonomy is the smallest one the measurement supports. A prefix says
/// what the bytes start with and nothing more, so there is no detail here for
/// a document nobody can open, for a password, or for an archive: a byte
/// prefix cannot tell any of those from a plain file of the same type, and a
/// detail that claimed to would be a claim nothing measured.
/// </para>
/// </summary>
internal static class AttachmentValidationDetails
{
    /// <summary>No signature in the closed table matched the leading bytes.</summary>
    internal const string ContentNotInspectable = "content-not-inspectable";

    /// <summary>The leading bytes and the declaration name different types.</summary>
    internal const string ContentTypeDivergent = "content-type-divergent";

    /// <summary>
    /// The two agree, and the type is not one an operator admitted. With no
    /// admitted type configured, this is where everything lands, which is what
    /// closing by default means here.
    /// </summary>
    internal const string ContentTypeNotAdmitted = "content-type-not-admitted";

    /// <summary>
    /// The tolerance for a verdict that never concluded ran out. Reaching it
    /// ends the attachment: waiting is bounded, and the bound closes.
    /// </summary>
    internal const string InconclusiveWindowElapsed = "inconclusive-window-elapsed";
}
