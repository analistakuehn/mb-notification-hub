using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications.Dispatching;

/// <summary>
/// Which plan one fallback decision runs on, and what that costs when the
/// stored document no longer reads. The stored plan exists so that a
/// republication cannot move a notification already in flight onto a channel
/// its admission had removed; a document this code cannot make sense of
/// reaches the published order anyway, and this is where that is named instead
/// of left as an unasserted consequence.
/// </summary>
public sealed class FallbackPlanSourceTests
{
    /// <summary>
    /// The published order and the admitted order diverge after the failed
    /// channel on purpose: the admission removed push and kept email. Reading
    /// the stored document takes the notification to email; failing to read it
    /// takes the same notification to push, the channel the admission refused.
    /// </summary>
    [Theory]
    [InlineData("[{\"channel\":\"sms\"},{\"channel\":\"telegram\"}]")]
    [InlineData("[{\"channel\":\"sms\",")]
    public void A_stored_plan_that_no_longer_reads_advances_to_the_channel_the_admission_removed(
        string unreadable)
    {
        DeliveryPlanStep[] published = Plan(("sms", TimeSpan.FromMinutes(1)), ("push", null));
        var admitted = AdmittedDeliveryPlan.Serialize(
            Plan(("sms", TimeSpan.FromMinutes(1)), ("email", null)));

        DeliveryPlanStep? afterUnreadable = FallbackRequestHandler.NextStep(
            FallbackRequestHandler.PlanFor(AdmittedDeliveryPlan.Read(unreadable), published),
            failedChannel: "sms");
        DeliveryPlanStep? afterAdmitted = FallbackRequestHandler.NextStep(
            FallbackRequestHandler.PlanFor(AdmittedDeliveryPlan.Read(admitted), published),
            failedChannel: "sms");

        afterUnreadable.ShouldNotBeNull(
                "o documento ilegível cai na ordem publicada e ela ainda nomeia um passo depois "
                + "de sms; um nulo aqui significa que a decisão parou de avançar, que é conserto "
                + "deliberado do eixo e não estado atual.")
            .Channel.Value.ShouldBe(
            "push",
            "um documento ilegível segue pela ordem publicada e alcança push, o canal que a "
            + "admissão havia removido; é o dano que o plano armazenado existe para impedir e "
            + "ele fica preso aqui, para que tratá-lo de outro jeito seja mudança deliberada.");
        afterAdmitted.ShouldNotBeNull().Channel.Value.ShouldBe(
            "email",
            "com o documento legível a decisão corre pela ordem admitida, e o contraste com o "
            + "caso acima é o que mede o custo de não conseguir lê-lo.");
    }

    /// <summary>
    /// A row older than the column carries no plan and continues on the
    /// published order by design: refusing a fallback the plan owes, over a
    /// column the row could not have had, would lose an authentication code to
    /// a migration.
    /// </summary>
    [Fact]
    public void A_notification_without_a_stored_plan_runs_on_the_published_order()
    {
        DeliveryPlanStep[] published = Plan(("sms", TimeSpan.FromMinutes(1)), ("push", null));

        FallbackRequestHandler.PlanFor(AdmittedDeliveryPlan.Read(null), published)
            .ShouldBeSameAs(published);
    }

    /// <summary>
    /// The admitted plan wins whenever it reads, which is what keeps a
    /// republication from moving a notification already in flight.
    /// </summary>
    [Fact]
    public void A_stored_plan_that_reads_wins_over_the_published_one()
    {
        DeliveryPlanStep[] published = Plan(("sms", TimeSpan.FromMinutes(1)), ("push", null));
        var admitted = AdmittedDeliveryPlan.Serialize(
            Plan(("sms", TimeSpan.FromMinutes(1)), ("email", null)));

        FallbackRequestHandler.PlanFor(AdmittedDeliveryPlan.Read(admitted), published)
            .Select(step => step.Channel.Value)
            .ShouldBe(["sms", "email"]);
    }

    private static DeliveryPlanStep[] Plan(params (string Channel, TimeSpan? Timeout)[] steps)
        => [.. steps.Select(step =>
            new DeliveryPlanStep(Channel.Create(step.Channel).Value!, step.Timeout))];
}
