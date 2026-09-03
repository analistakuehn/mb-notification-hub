namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

internal enum AttachmentContentReadingStatus
{
    /// <summary>The pinned generation was read whole and measured.</summary>
    Measured,

    /// <summary>
    /// The store answered that the pinned generation is not there. Right after
    /// a write the store confirmed, this is not the store being unreachable.
    /// </summary>
    Missing,

    /// <summary>The store could not be reached or refused the reading.</summary>
    Unavailable,
}

/// <summary>
/// Outcome of one measurement, carrying a proof only when the generation was
/// read whole.
/// <para>
/// Absence and unreachability used to arrive as the same empty answer, which
/// left the caller mapping both to an unavailable store. They are different
/// events with different readers, and collapsing them hid the one that says
/// the bytes the store had just named are gone.
/// </para>
/// </summary>
internal sealed record AttachmentContentReading
{
    private AttachmentContentReading(
        AttachmentContentReadingStatus status,
        AttachmentContentProof? proof,
        string? detectedContentType)
    {
        Status = status;
        Proof = proof;
        DetectedContentType = detectedContentType;
    }

    internal AttachmentContentReadingStatus Status { get; }

    internal AttachmentContentProof? Proof { get; }

    /// <summary>
    /// What the leading bytes of the generation say it is, or nothing when no
    /// signature matched them. It is a measurement and not a verdict: nothing
    /// here compares it to what was declared or to what an operator admitted,
    /// because that comparison is the policy, and the policy is evaluated once,
    /// at validation, over the value recorded here.
    /// </summary>
    internal string? DetectedContentType { get; }

    internal static AttachmentContentReading Measured(
        AttachmentContentProof proof,
        string? detectedContentType)
        => new(AttachmentContentReadingStatus.Measured, proof, detectedContentType);

    internal static AttachmentContentReading Missing()
        => new(AttachmentContentReadingStatus.Missing, null, null);

    internal static AttachmentContentReading Unavailable()
        => new(AttachmentContentReadingStatus.Unavailable, null, null);
}
