using System.Globalization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class AttachmentReleaseValidityTests
{
    private static readonly TimeSpan Validity = TimeSpan.FromDays(30);

    private static readonly DateTimeOffset ReleasedAt = DateTimeOffset.Parse(
        "2026-09-02T12:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public void A_release_is_written_with_the_deadline_it_was_granted_under()
    {
        AttachmentRelease release = Granted();

        release.ReleasedAt.ShouldBe(ReleasedAt);
        release.ExpiresAt.ShouldBe(ReleasedAt + Validity);
        release.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void A_release_stays_usable_up_to_its_deadline_and_not_at_it()
    {
        AttachmentRelease release = Granted();

        release.IsValidAt(ReleasedAt, Validity, null).ShouldBeTrue();
        release.IsValidAt(ReleasedAt + Validity - TimeSpan.FromTicks(1), Validity, null)
            .ShouldBeTrue();
        release.IsValidAt(ReleasedAt + Validity, Validity, null).ShouldBeFalse();
    }

    /// <summary>
    /// The asymmetry the deadline exists for. Counted from the release alone,
    /// cutting the validity expires, at the instant of the deployment, every
    /// release older than the new value, and every notification already
    /// accepted over one of them fails on its way out. Counted from the later
    /// of the release and the deployment, nobody loses the new duration.
    /// </summary>
    [Fact]
    public void Shortening_the_validity_does_not_expire_a_release_on_the_deployment_instant()
    {
        AttachmentRelease release = Granted();
        var shortened = TimeSpan.FromDays(7);
        DateTimeOffset deployedAt = ReleasedAt + TimeSpan.FromDays(100);

        release.IsValidAt(deployedAt, shortened, null).ShouldBeFalse();
        release.IsValidAt(deployedAt, shortened, deployedAt).ShouldBeTrue();
        release.DeadlineAt(shortened, deployedAt).ShouldBe(deployedAt + shortened);

        // The grace is a floor and never a renewal: past the new duration
        // counted from the deployment, the release is over.
        release.IsValidAt(deployedAt + shortened, shortened, deployedAt).ShouldBeFalse();
    }

    [Fact]
    public void A_deployment_older_than_the_release_changes_nothing()
    {
        AttachmentRelease release = Granted();
        DateTimeOffset deployedAt = ReleasedAt - TimeSpan.FromDays(400);

        release.DeadlineAt(Validity, deployedAt).ShouldBe(ReleasedAt + Validity);
        release.DeadlineAt(Validity, null).ShouldBe(ReleasedAt + Validity);
    }

    private static AttachmentRelease Granted()
        => AttachmentRelease.Grant(Guid.NewGuid(), Guid.NewGuid(), ReleasedAt, Validity);
}
