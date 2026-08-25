using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

namespace NotificationHub.UnitTests.Notifications.DeliveryTracking;

public sealed class DeliveryStateMachineTests
{
    [Fact]
    public void A_confirmation_moves_a_sent_attempt_to_delivered()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Sent, DeliveryFeedbackKind.Delivered)
            .ShouldBe(NotificationAttemptStatuses.Delivered);

    [Fact]
    public void An_open_moves_a_delivered_attempt_to_read()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Delivered, DeliveryFeedbackKind.Read)
            .ShouldBe(NotificationAttemptStatuses.Read);

    [Fact]
    public void A_late_rejection_settles_an_attempt_still_owned_by_the_claim()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Sending, DeliveryFeedbackKind.Failed)
            .ShouldBe(NotificationAttemptStatuses.Failed);

    [Fact]
    public void A_late_bounce_settles_an_attempt_still_owned_by_the_claim()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Sending, DeliveryFeedbackKind.Bounced)
            .ShouldBe(NotificationAttemptStatuses.Bounced);

    [Fact]
    public void A_bounce_after_acceptance_moves_a_sent_attempt_to_bounced()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Sent, DeliveryFeedbackKind.Bounced)
            .ShouldBe(NotificationAttemptStatuses.Bounced);

    [Fact]
    public void A_confirmation_repeated_over_a_delivered_attempt_changes_nothing()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Delivered, DeliveryFeedbackKind.Delivered)
            .ShouldBeNull();

    [Fact]
    public void A_confirmation_never_walks_a_read_attempt_backwards()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Read, DeliveryFeedbackKind.Delivered)
            .ShouldBeNull();

    [Fact]
    public void An_open_reported_before_the_confirmation_is_not_a_transition()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Sent, DeliveryFeedbackKind.Read)
            .ShouldBeNull();

    [Fact]
    public void Progress_the_hub_already_knows_about_moves_nothing()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Sent, DeliveryFeedbackKind.Sent)
            .ShouldBeNull();

    [Fact]
    public void Feedback_about_an_attempt_that_never_left_the_queue_moves_nothing()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Queued, DeliveryFeedbackKind.Delivered)
            .ShouldBeNull();

    [Fact]
    public void A_confirmation_never_revives_a_failed_attempt()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Failed, DeliveryFeedbackKind.Delivered)
            .ShouldBeNull();

    [Fact]
    public void A_bounce_never_revives_a_failed_attempt()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Failed, DeliveryFeedbackKind.Bounced)
            .ShouldBeNull();

    [Fact]
    public void A_status_outside_the_vocabulary_of_the_machine_moves_nothing()
        => DeliveryStateMachine.NextStatus("a-status-no-writer-produces", DeliveryFeedbackKind.Delivered)
            .ShouldBeNull();

    [Fact]
    public void An_attempt_parked_on_unknown_is_left_to_reconciliation()
        => DeliveryStateMachine.NextStatus(
                NotificationAttemptStatuses.Unknown, DeliveryFeedbackKind.Delivered)
            .ShouldBeNull();
}
