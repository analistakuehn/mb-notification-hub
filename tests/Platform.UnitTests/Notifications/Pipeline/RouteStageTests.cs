using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;
using NSubstitute;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

/// <summary>
/// What the route stage does with a notification that was accepted over a set
/// of attachments, and what it refuses to do with it.
/// <para>
/// The plan is read here and never rewritten. A channel that does not compose
/// the set into its call ends the notification with a published reason, and
/// the step behind it is not promoted: which channel a notification takes was
/// decided by policy and by who the recipient is reachable as, and letting the
/// presence of an attachment reorder that would deliver by a channel nobody
/// planned for, to a person whose plan said something else.
/// </para>
/// <para>
/// Every refusal below sits beside a route that goes through, over the same
/// stage and the same doubles. A stage that had stopped routing altogether
/// would satisfy each refusal on its own.
/// </para>
/// </summary>
public sealed class RouteStageTests
{
    private static readonly Guid SmsContactPoint = Guid.NewGuid();

    private static readonly Guid EmailContactPoint = Guid.NewGuid();

    [Fact]
    public async Task A_first_step_that_does_not_carry_the_accepted_set_rejects_the_notification()
    {
        (IChannelAttachmentSupport support, List<string> asked) = Support(carries: false);
        NotificationContext context = ContextWithPlan(WithASet(), "sms", "email");

        StageOutcome outcome = await new RouteStage(support).ExecuteAsync(
            context, CancellationToken.None);

        outcome.ShouldBe(StageOutcome.Reject);
        context.LastReason.ShouldBe(NotificationRejectionReasons.AttachmentsNotCarriedByChannel);
        NotificationRejectionReasons.IsCanonical(context.LastReason).ShouldBeTrue(
            "o motivo viaja no evento de rejeição e na consulta, e um valor fora do catálogo "
            + "fechado chega ao produtor como palavra que nenhum consumidor sabe interpretar.");

        // Nothing was routed: no destination, no contact point and no fallback
        // wait, so the commit has nothing to queue an attempt with.
        context.DispatchDestination.ShouldBeNull();
        context.SelectedContactPointId.ShouldBeNull();
        context.FallbackTimeout.ShouldBeNull();

        // The plan is exactly the plan the admission left, in the order it left
        // it. A stage that filtered the step out, or reordered it, would have
        // ended the same run on a channel the published plan never put first.
        context.DeliveryPlan!.Select(step => step.Channel.Value).ShouldBe(["sms", "email"]);

        // And the step behind the refused one was never even asked about: the
        // search stops at the step the plan takes.
        asked.ShouldBe(["sms"]);
    }

    [Fact]
    public async Task A_first_step_that_carries_the_accepted_set_routes_it_unchanged()
    {
        (IChannelAttachmentSupport support, List<string> asked) = Support(carries: true);
        NotificationContext context = ContextWithPlan(WithASet(), "email", "sms");

        StageOutcome outcome = await new RouteStage(support).ExecuteAsync(
            context, CancellationToken.None);

        outcome.ShouldBe(StageOutcome.Continue);
        context.LastReason.ShouldBeNull();
        context.DispatchDestination.ShouldBe("dispatch-email-transactional");
        context.SelectedContactPointId.ShouldBe(EmailContactPoint);
        context.DeliveryPlan!.Select(step => step.Channel.Value).ShouldBe(["email", "sms"]);
        asked.ShouldBe(["email"]);
    }

    /// <summary>
    /// A notification that named no attachments is routed onto a channel that
    /// carries none, and the question is never asked. The channel here is the
    /// very one refused above, so what separates the two runs is the set and
    /// nothing else.
    /// </summary>
    [Fact]
    public async Task A_notification_with_no_accepted_set_is_routed_without_asking_the_channel()
    {
        (IChannelAttachmentSupport support, List<string> asked) = Support(carries: false);
        NotificationContext context = ContextWithPlan(
            PipelineTestData.AcceptedNotification(), "sms", "email");

        StageOutcome outcome = await new RouteStage(support).ExecuteAsync(
            context, CancellationToken.None);

        outcome.ShouldBe(StageOutcome.Continue);
        context.DispatchDestination.ShouldBe("dispatch-sms-transactional");
        context.SelectedContactPointId.ShouldBe(SmsContactPoint);
        asked.ShouldBeEmpty(
            "uma notificação sem conjunto aceito não tem a pergunta a fazer, e fazê-la "
            + "colocaria a resolução de provedor no caminho de toda notificação do serviço.");
    }

    /// <summary>
    /// A stored document this code cannot read is not this stage's question,
    /// and the run goes on. It is recorded here as the boundary it is: what
    /// stops a notification carrying a set nobody can name is the path that
    /// could still reach a provider, which refuses it before it claims
    /// anything, so nothing leaves incomplete either way.
    /// </summary>
    [Fact]
    public async Task A_stored_document_that_no_longer_reads_is_not_this_stage_s_question()
    {
        (IChannelAttachmentSupport support, List<string> asked) = Support(carries: false);
        Notification notification = PipelineTestData.AcceptedNotification();
        notification.FreezeAcceptedAttachments("""{"schemaVersion":9,"items":[]}""");
        NotificationContext context = ContextWithPlan(notification, "sms", "email");

        StageOutcome outcome = await new RouteStage(support).ExecuteAsync(
            context, CancellationToken.None);

        outcome.ShouldBe(StageOutcome.Continue);
        asked.ShouldBeEmpty();
    }

    /// <summary>
    /// A channel nothing can be resolved for is a deployment defect, so the
    /// run fails and the message returns with backoff. Answering it as a
    /// rejection would end a notification that is still perfectly deliverable,
    /// and would tell the producer its request was refused for a fault of
    /// ours.
    /// </summary>
    [Fact]
    public async Task A_channel_that_resolves_to_no_adapter_holds_the_notification_instead_of_rejecting_it()
    {
        IChannelAttachmentSupport support = Substitute.For<IChannelAttachmentSupport>();
        support
            .CarriesAttachmentsAsync(Arg.Any<Channel>(), Arg.Any<CancellationToken>())
            .Returns(Result.IntegrationFailure<bool>("nenhum adapter hospedado para o canal."));
        NotificationContext context = ContextWithPlan(WithASet(), "email", "sms");

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => new RouteStage(support).ExecuteAsync(context, CancellationToken.None));

        failure.Message.ShouldContain("adapter");
        context.LastReason.ShouldBeNull();
        context.DispatchDestination.ShouldBeNull();
    }

    /// <summary>
    /// The support double, plus the channels it was asked about, in order.
    /// The recording is what separates a stage that asked and accepted from a
    /// stage that never asked at all.
    /// </summary>
    private static (IChannelAttachmentSupport Support, List<string> Asked) Support(bool carries)
    {
        var asked = new List<string>();
        IChannelAttachmentSupport support = Substitute.For<IChannelAttachmentSupport>();
        support
            .CarriesAttachmentsAsync(
                Arg.Do<Channel>(channel => asked.Add(channel.Value)),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(carries));
        return (support, asked);
    }

    private static NotificationContext ContextWithPlan(
        Notification notification,
        params string[] channels)
    {
        NotificationContext context = PipelineTestData.Context(
            notification,
            PipelineTestData.Template(),
            PipelineTestData.Recipient(contactPoints:
            [
                new ContactPointSnapshot(SmsContactPoint, "sms", Verified: true),
                new ContactPointSnapshot(EmailContactPoint, "email", Verified: true),
            ]));
        context.DeliveryPlan =
            [.. channels.Select(channel => new DeliveryPlanStep(Channel.Create(channel).Value!, null))];
        return context;
    }

    /// <summary>A notification accepted over a set, written the way the acceptance writes it.</summary>
    private static Notification WithASet()
    {
        Notification notification = PipelineTestData.AcceptedNotification();
        notification.FreezeAcceptedAttachments(AcceptedAttachmentManifest.Serialize(
            AcceptedAttachmentSet.Of([
                new AcceptedAttachment
                {
                    Reference = "att_01K2R7",
                    ContentIdentity = "aci_01K2R7",
                    Name = "comprovante.pdf",
                    MediaType = "application/pdf",
                    Length = 2048,
                },
            ])));
        return notification;
    }
}
