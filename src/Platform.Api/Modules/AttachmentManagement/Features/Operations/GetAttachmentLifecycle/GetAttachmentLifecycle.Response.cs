namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Operations;

internal static partial class GetAttachmentLifecycle
{
    /// <summary>
    /// What the operations surface may know about one attachment. Every member
    /// is an instant, a state or a reason someone declared; none of them is a
    /// storage coordinate, a content identity, a file name, a declared type or
    /// a proof of the bytes, so the answer stays a reading of the lifecycle and
    /// never a way to reach the content.
    /// </summary>
    /// <param name="Reference">The opaque identity, the only correlator that travels.</param>
    /// <param name="State">The state the attachment carries now.</param>
    /// <param name="ValidationDetail">
    /// Which check refused, or which verdict did not conclude. This is the one
    /// place it leaves the module: a producer is told a single word for the
    /// whole family, and this member is why that single word costs nothing in
    /// diagnosis.
    /// </param>
    /// <param name="InconclusiveUntil">
    /// When the tolerance for a verdict that did not conclude runs out.
    /// </param>
    /// <param name="ReleasedAt">When the release in force was granted.</param>
    /// <param name="ReleaseExpiresAt">
    /// When the release in force stops being usable, computed from the validity
    /// in force now rather than read from a column. Nothing writes an expiry
    /// into the state, so this is the only place the mechanism can be observed
    /// before a message is on its way out.
    /// </param>
    /// <param name="RevokedAt">When the release was taken back.</param>
    /// <param name="RevocationReason">Why it was taken back, as declared.</param>
    internal sealed record Response(
        string Reference,
        string State,
        string? ValidationDetail,
        DateTimeOffset? InconclusiveUntil,
        DateTimeOffset? ReleasedAt,
        DateTimeOffset? ReleaseExpiresAt,
        DateTimeOffset? RevokedAt,
        string? RevocationReason);
}
