namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

/// <summary>
/// The executable policy's values. The defaults here release nothing: with no
/// admitted type, every attachment is refused, which is the state the module is
/// meant to be in until an operator decides otherwise.
/// </summary>
public sealed class AttachmentValidationOptions
{
    public const string SectionName = "Modules:AttachmentManagement:Validation";

    /// <summary>
    /// The types an operator admits. Empty means none, and none means nothing
    /// is released.
    /// <para>
    /// Empty is also the shape the binder needs: configuration binding appends
    /// to a non-empty default, so a default carrying entries could only be
    /// added to and never reduced by configuration.
    /// </para>
    /// <para>
    /// Every entry has to be a type the signature table recognizes, and
    /// startup refuses one that is not. Admitting a type nothing can detect
    /// would refuse every file of that type as unrecognized, which reads in
    /// production as the feature being broken rather than as configuration
    /// being wrong.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AdmittedContentTypes { get; init; } = [];

    /// <summary>
    /// How long a release stays usable, counted from the release. It is at
    /// least the longest life a notification can have, so nothing already
    /// accepted dies of an expired release while it is still on its way out.
    /// </summary>
    public TimeSpan ReleaseValidity { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// When the value above took effect, declared rather than observed. Taken
    /// from the process start it would move on every restart and nothing would
    /// ever expire.
    /// <para>
    /// Unset means no grace, which is the strict reading. It is set when the
    /// validity is shortened, so releases granted under the old value get the
    /// new duration counted from the deployment instead of expiring on it.
    /// </para>
    /// <para>
    /// Nothing in the validation path reads it. The comparison that does is
    /// the one made before an attempt goes out, and it reads it through
    /// <see cref="Persistence.AttachmentRelease.DeadlineAt"/>.
    /// </para>
    /// </summary>
    public DateTimeOffset? ValidityEffectiveFrom { get; init; }

    /// <summary>
    /// How long an attachment waits on a verdict that does not conclude before
    /// the wait ends against it. Kept at or under the reconciliation cycle, so
    /// the attachment stays the fastest part of the system to settle.
    /// <para>
    /// Inert until a verifier exists: the policy that ships refuses and
    /// approves and never leaves a verdict open, so nothing reaches the state
    /// this value bounds.
    /// </para>
    /// </summary>
    public TimeSpan InconclusiveWindow { get; init; } = TimeSpan.FromHours(24);
}
