using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.UnitTests.Notifications.Scheduling;

/// <summary>
/// The transition a released notification takes. It exists to answer one
/// question the Core asks of every message it reads: is this notification
/// still accepted. Anything else there is a redelivery, so a release that did
/// not land the notification back on accepted would be discarded as a
/// duplicate and the notification would wait forever.
/// </summary>
public sealed class NotificationReleaseTests
{
    [Fact]
    public void A_deferred_notification_goes_back_to_accepted()
    {
        Notification notification = Deferred();

        notification.MarkReleased();

        notification.Status.ShouldBe(
            NotificationStatuses.Accepted,
            "a retomada precisa devolver a notificação ao único estado que os estágios do "
            + "pipeline aceitam; em qualquer outro o Core a trata como reentrega.");
    }

    /// <summary>
    /// The release instant survives the release. It is the evidence of why the
    /// notification waited, and nothing reads it to decide anything after the
    /// transition: the index the scan reads is filtered by the deferred state,
    /// so the row leaves that index by moving, never by losing its instant.
    /// </summary>
    [Fact]
    public void The_release_instant_survives_as_evidence()
    {
        DateTimeOffset releaseAt = DateTimeOffset.UtcNow.AddHours(8);
        Notification notification = Deferred(releaseAt);

        notification.MarkReleased();

        notification.ReleaseAt.ShouldBe(releaseAt);
    }

    /// <summary>
    /// A released notification runs the stage list again, and the stages own
    /// every later transition. The guard is what keeps a second release, or a
    /// release racing a conclusion, from resurrecting a notification that
    /// already ended.
    /// </summary>
    [Fact]
    public void A_notification_that_is_not_deferred_refuses_the_release()
    {
        Notification notification = Deferred();
        notification.MarkReleased();

        Should.Throw<InvalidOperationException>(notification.MarkReleased);
    }

    [Fact]
    public void An_accepted_notification_refuses_the_release()
        => Should.Throw<InvalidOperationException>(Accepted().MarkReleased);

    private static Notification Deferred(DateTimeOffset? releaseAt = null)
    {
        Notification notification = Accepted();
        notification.MarkDeferred(releaseAt ?? DateTimeOffset.UtcNow.AddHours(8), policyVersion: 3);
        return notification;
    }

    private static Notification Accepted()
        => Notification.Accept(new NotificationDraft
        {
            Application = "app-release",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RecipientId = $"cus_{Guid.NewGuid():N}",
            Class = NotificationClasses.Transactional,
            TemplateKey = "tpl-release",
            TemplateVersion = 1,
            VariablesMaskedJson = "{}",
            RequestedBy = "unit-tests",
            TtlSeconds = 3600,
            AcceptedAt = DateTimeOffset.UtcNow,
        });
}
