using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

public sealed class NotificationTransitionTests
{
    [Fact]
    public void Dispatching_stamps_the_policy_version_and_keeps_the_encrypted_variables()
    {
        Notification notification = AcceptedWithVariables();

        notification.MarkDispatched(policyVersion: 4);

        notification.Status.ShouldBe(NotificationStatuses.Dispatched);
        notification.PolicyVersion.ShouldBe(4);
        notification.VariablesEncrypted.ShouldNotBeNull();
    }

    [Fact]
    public void Rejecting_purges_the_encrypted_variables()
    {
        Notification notification = AcceptedWithVariables();

        notification.MarkRejected(policyVersion: 4);

        notification.Status.ShouldBe(NotificationStatuses.Rejected);
        notification.PolicyVersion.ShouldBe(4);
        notification.VariablesEncrypted.ShouldBeNull();
    }

    [Fact]
    public void Rejecting_before_the_policy_stage_leaves_the_policy_version_empty()
    {
        Notification notification = AcceptedWithVariables();

        notification.MarkRejected(policyVersion: null);

        notification.PolicyVersion.ShouldBeNull();
        notification.VariablesEncrypted.ShouldBeNull();
    }

    [Fact]
    public void Expiring_purges_the_encrypted_variables()
    {
        Notification notification = AcceptedWithVariables();

        notification.MarkExpired();

        notification.Status.ShouldBe(NotificationStatuses.Expired);
        notification.VariablesEncrypted.ShouldBeNull();
    }

    [Fact]
    public void Deferring_records_the_release_instant_and_keeps_the_encrypted_variables()
    {
        Notification notification = AcceptedWithVariables();
        DateTimeOffset releaseAt = DateTimeOffset.UtcNow.AddHours(8);

        notification.MarkDeferred(releaseAt, policyVersion: 4);

        notification.Status.ShouldBe(NotificationStatuses.Deferred);
        notification.ReleaseAt.ShouldBe(releaseAt);
        notification.VariablesEncrypted.ShouldNotBeNull();
    }

    [Fact]
    public void A_notification_past_its_accepted_state_refuses_a_second_pipeline_transition()
    {
        Notification notification = AcceptedWithVariables();
        notification.MarkDispatched(policyVersion: 4);

        Should.Throw<InvalidOperationException>(() => notification.MarkRejected(4));
        Should.Throw<InvalidOperationException>(() => notification.MarkExpired());
        Should.Throw<InvalidOperationException>(
            () => notification.MarkDeferred(DateTimeOffset.UtcNow, 4));
    }

    [Fact]
    public void The_attempt_stamps_the_fallback_deadline_at_enqueue_time()
    {
        var queuedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

        NotificationAttempt attempt = NotificationAttempt.Queue(Draft(queuedAt, TimeSpan.FromSeconds(30)));

        attempt.Status.ShouldBe(NotificationAttemptStatuses.Queued);
        attempt.FallbackDeadline.ShouldBe(queuedAt.AddSeconds(30));
        attempt.CreatedAt.ShouldBe(queuedAt);
        attempt.ProviderKey.ShouldBeNull();
    }

    [Fact]
    public void A_plan_step_without_timeout_leaves_the_fallback_deadline_empty()
        => NotificationAttempt.Queue(Draft(DateTimeOffset.UtcNow, fallbackTimeout: null))
            .FallbackDeadline.ShouldBeNull();

    private static NotificationAttemptDraft Draft(DateTimeOffset queuedAt, TimeSpan? fallbackTimeout)
        => new()
        {
            NotificationId = Guid.NewGuid(),
            Sequence = 1,
            Channel = "push",
            RenderedContentEncrypted = [1, 2, 3],
            ContentHashFull = "full",
            ContentHashMasked = "masked",
            FallbackTimeout = fallbackTimeout,
            QueuedAt = queuedAt,
        };

    private static Notification AcceptedWithVariables()
        => Notification.Accept(new NotificationDraft
        {
            Application = "araia-cambio",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RecipientId = "recipient-1",
            Class = NotificationClasses.Critical,
            TemplateKey = "auth.otp.login",
            TemplateVersion = 1,
            VariablesMaskedJson = """{"code":"***"}""",
            VariablesEncrypted = [1, 2, 3],
            RequestedBy = "producer-app",
            TtlSeconds = 300,
            AcceptedAt = DateTimeOffset.UtcNow,
        });
}
