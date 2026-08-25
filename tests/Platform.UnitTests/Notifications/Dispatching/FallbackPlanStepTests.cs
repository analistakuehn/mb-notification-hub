using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.UnitTests.Notifications.Pipeline;

namespace NotificationHub.UnitTests.Notifications.Dispatching;

public sealed class FallbackPlanStepTests
{
    [Fact]
    public void The_step_after_the_failed_channel_is_the_next_one()
    {
        DeliveryPlanStep? next = FallbackRequestHandler.NextStep(
            Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)), failedChannel: "push");

        next.ShouldNotBeNull();
        next.Channel.Value.ShouldBe("email");
        next.Timeout.ShouldBeNull();
    }

    [Fact]
    public void The_last_step_of_the_plan_has_no_successor()
        => FallbackRequestHandler.NextStep(
                Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)), failedChannel: "email")
            .ShouldBeNull();

    [Fact]
    public void A_channel_outside_the_published_plan_has_no_successor()
        => FallbackRequestHandler.NextStep(
                Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)), failedChannel: "sms")
            .ShouldBeNull();

    /// <summary>
    /// Eligibility is read when the step is chosen, not frozen with the plan.
    /// A destination the hub stopped addressing between the admission and this
    /// deadline is skipped, and the plan keeps going: ending the notification
    /// over a dead middle channel would turn a protection for the recipient
    /// into a delivery failure against them.
    /// </summary>
    [Fact]
    public void A_channel_suppressed_after_the_admission_is_skipped_for_the_one_behind_it()
    {
        var deadEmail = Guid.NewGuid();
        RecipientSnapshot recipient = PipelineTestData.Recipient(
            contactPoints:
            [
                new ContactPointSnapshot(deadEmail, "email", Verified: true),
                new ContactPointSnapshot(Guid.NewGuid(), "sms", Verified: true),
            ],
            suppressions: [Suppressed(deadEmail, "email")]);

        (DeliveryPlanStep? step, var reason) = FallbackRequestHandler.NextUsableStep(
            Plan(("push", TimeSpan.FromSeconds(30)), ("email", TimeSpan.FromMinutes(1)), ("sms", null)),
            failedChannel: "push",
            recipient,
            consentPurpose: null,
            DateTimeOffset.UtcNow);

        reason.ShouldBeNull();
        step.ShouldNotBeNull().Channel.Value.ShouldBe(
            "sms",
            "o e-mail estava suprimido no instante da escolha, então o plano tinha de seguir "
            + "para o passo seguinte em vez de endereçar um destino morto.");
    }

    /// <summary>
    /// A recipient who keeps a second live address on the channel is still
    /// reachable there, which is the same reading the policy rule makes.
    /// </summary>
    [Fact]
    public void A_channel_with_one_live_address_left_is_still_usable()
    {
        var deadEmail = Guid.NewGuid();
        RecipientSnapshot recipient = PipelineTestData.Recipient(
            contactPoints:
            [
                new ContactPointSnapshot(deadEmail, "email", Verified: true),
                new ContactPointSnapshot(Guid.NewGuid(), "email", Verified: true),
            ],
            suppressions: [Suppressed(deadEmail, "email")]);

        (DeliveryPlanStep? step, _) = FallbackRequestHandler.NextUsableStep(
            Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)),
            failedChannel: "push",
            recipient,
            consentPurpose: null,
            DateTimeOffset.UtcNow);

        step.ShouldNotBeNull().Channel.Value.ShouldBe("email");
    }

    /// <summary>
    /// When nothing later is usable the reason reported is the one that blocked
    /// the first later step, which is the channel the plan would have taken, so
    /// the trail names the decision that actually ended the notification.
    /// </summary>
    [Fact]
    public void A_plan_whose_later_steps_are_all_blocked_reports_the_first_reason()
    {
        var deadEmail = Guid.NewGuid();
        RecipientSnapshot recipient = PipelineTestData.Recipient(
            contactPoints: [new ContactPointSnapshot(deadEmail, "email", Verified: true)],
            suppressions: [Suppressed(deadEmail, "email")]);

        (DeliveryPlanStep? step, var reason) = FallbackRequestHandler.NextUsableStep(
            Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)),
            failedChannel: "push",
            recipient,
            consentPurpose: null,
            DateTimeOffset.UtcNow);

        step.ShouldBeNull();
        reason.ShouldBe(SuppressionGateRule.ReasonChannelSuppressed);
    }

    /// <summary>
    /// Consent withdrawn after the admission blocks the step for its own
    /// reason, and a class with no purpose consults nothing, which is what the
    /// consent rule does with the same evidence.
    /// </summary>
    [Fact]
    public void Consent_withdrawn_after_the_admission_blocks_the_step_with_its_own_reason()
    {
        RecipientSnapshot recipient = PipelineTestData.Recipient(
            contactPoints: [new ContactPointSnapshot(Guid.NewGuid(), "email", Verified: true)],
            consents: []);

        (DeliveryPlanStep? withPurpose, var reason) = FallbackRequestHandler.NextUsableStep(
            Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)),
            failedChannel: "push",
            recipient,
            consentPurpose: "marketing-updates",
            DateTimeOffset.UtcNow);

        withPurpose.ShouldBeNull();
        reason.ShouldBe(ConsentGateRule.ReasonNoConsent);

        (DeliveryPlanStep? withoutPurpose, _) = FallbackRequestHandler.NextUsableStep(
            Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)),
            failedChannel: "push",
            recipient,
            consentPurpose: null,
            DateTimeOffset.UtcNow);

        withoutPurpose.ShouldNotBeNull().Channel.Value.ShouldBe(
            "email",
            "sem finalidade de consentimento a classe opera em base contratual ou legal "
            + "e não consulta o ledger, igual à regra do estágio Policy.");
    }

    /// <summary>An expired suppression is not a suppression anymore.</summary>
    [Fact]
    public void A_suppression_that_already_lapsed_does_not_block_the_step()
    {
        var email = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecipientSnapshot recipient = PipelineTestData.Recipient(
            contactPoints: [new ContactPointSnapshot(email, "email", Verified: true)],
            suppressions: [Suppressed(email, "email", until: now.AddMinutes(-1))]);

        (DeliveryPlanStep? step, _) = FallbackRequestHandler.NextUsableStep(
            Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)),
            failedChannel: "push",
            recipient,
            consentPurpose: null,
            now);

        step.ShouldNotBeNull().Channel.Value.ShouldBe("email");
    }

    /// <summary>One suppression of one address, open ended unless told otherwise.</summary>
    private static SuppressionState Suppressed(Guid contactPointId, string channel, DateTimeOffset? until = null)
        => new(contactPointId, channel, "hard-bounce", DateTimeOffset.UnixEpoch, until);

    private static DeliveryPlanStep[] Plan(params (string Channel, TimeSpan? Timeout)[] steps)
        => [.. steps.Select(step =>
            new DeliveryPlanStep(Channel.Create(step.Channel).Value!, step.Timeout))];
}
