using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.UnitTests.Notifications.Dispatching;

/// <summary>
/// When the provider taking the message is all this hub will ever learn about
/// it. Push is the channel with no later report, so its acceptance is the only
/// acceptance that can stand for a delivery, and even there only when no step
/// exists to rescue an undelivered message.
/// </summary>
public sealed class AcceptanceDeliveryTests
{
    [Fact]
    public void A_push_attempt_on_the_last_step_delivers_on_acceptance()
        => DispatchMessageProcessor.DeliversOnAcceptance(Attempt("push", fallbackTimeout: null))
            .ShouldBeTrue();

    [Fact]
    public void A_push_attempt_with_a_fallback_deadline_does_not_deliver_on_acceptance()
        => DispatchMessageProcessor.DeliversOnAcceptance(
                Attempt("push", TimeSpan.FromSeconds(30)))
            .ShouldBeFalse(
                "um prazo carimbado é a prova de que existe passo posterior, e declarar "
                + "entrega ali encerraria a notificação antes do passo que a socorreria.");

    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    public void A_channel_that_reports_delivery_never_delivers_on_acceptance(string channel)
    {
        DispatchMessageProcessor.DeliversOnAcceptance(Attempt(channel, fallbackTimeout: null))
            .ShouldBeFalse();
        DispatchMessageProcessor.DeliversOnAcceptance(Attempt(channel, TimeSpan.FromSeconds(30)))
            .ShouldBeFalse();
    }

    [Fact]
    public void A_push_sibling_inherits_the_step_deadline_and_the_same_answer()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var sibling = NotificationAttempt.Queue(new NotificationAttemptDraft
        {
            NotificationId = Guid.CreateVersion7(),
            Sequence = 2,
            Channel = "push",
            DeviceTokenId = Guid.CreateVersion7(),
            RenderedContentEncrypted = [1, 2, 3],
            ContentHashFull = "full",
            ContentHashMasked = "masked",
            FallbackDeadline = deadline,
            QueuedAt = DateTimeOffset.UtcNow,
        });

        DispatchMessageProcessor.DeliversOnAcceptance(sibling).ShouldBeFalse();
    }

    private static NotificationAttempt Attempt(string channel, TimeSpan? fallbackTimeout)
        => NotificationAttempt.Queue(new NotificationAttemptDraft
        {
            NotificationId = Guid.CreateVersion7(),
            Sequence = 1,
            Channel = channel,
            RenderedContentEncrypted = [1, 2, 3],
            ContentHashFull = "full",
            ContentHashMasked = "masked",
            FallbackTimeout = fallbackTimeout,
            QueuedAt = DateTimeOffset.UtcNow,
        });
}

/// <summary>
/// The core queue a fallback trigger names. The relay reads the drain band off
/// the destination, so this one line decides whether the second half of an
/// authentication code keeps the band the first half had.
/// </summary>
public sealed class FallbackDestinationTests
{
    [Theory]
    [InlineData("critical")]
    [InlineData("transactional")]
    [InlineData("operational")]
    public void An_authentication_flow_names_the_auth_queue_whatever_its_class(string priorityClass)
        => DispatchMessages.FallbackDestination(priorityClass, authFlow: true).ShouldBe("core-auth");

    [Theory]
    [InlineData("critical", "core-critical")]
    [InlineData("transactional", "core-transactional")]
    [InlineData("operational", "core-operational")]
    public void Any_other_flow_names_the_queue_of_its_class(string priorityClass, string expected)
        => DispatchMessages.FallbackDestination(priorityClass, authFlow: false).ShouldBe(expected);
}
