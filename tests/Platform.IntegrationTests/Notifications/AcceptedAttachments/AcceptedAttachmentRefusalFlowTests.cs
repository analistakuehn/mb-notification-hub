using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.AcceptedAttachments;

/// <summary>
/// What the pipeline, the dispatch and the fallback do when the notification
/// they are acting on carries an accepted set nobody can read.
/// <para>
/// All three stop, and all three stop having written nothing. The alternative
/// is the failure this whole capability exists to prevent: a notification that
/// leaves without the attachments its producer was told had been accepted.
/// Turning the defect into a business answer would be almost as bad, because
/// the row can be repaired and the notification is then still deliverable, so
/// the refusal is worded as the operational defect it is and the message is
/// held rather than settled.
/// </para>
/// <para>
/// Each case is paired with its repair inside the same test, over the same
/// trigger. The pairing is what makes the negative assertions mean something:
/// zero attempts, zero provider calls and an unclaimed step are all satisfied
/// by a path that does nothing at all, and only the repaired run proves that
/// the path was able to do the work and declined to.
/// </para>
/// </summary>
[Collection(AcceptedAttachmentFlowCollectionDefinition.Name)]
public sealed class AcceptedAttachmentRefusalFlowTests(AcceptedAttachmentFlowFixture fixture)
{
    private const string SendGridAccepted = "sg-message-refusal";

    private static readonly (string Channel, string? Timeout)[] EmailThenPush =
        [("email", "30s"), ("push", null)];

    /// <summary>
    /// The pipeline reads the set before the first attempt of the notification
    /// exists, so a document that no longer reads leaves the notification
    /// exactly where the acceptance left it.
    /// <para>
    /// The repaired run reuses the very same trigger, which is the second
    /// thing this proves: had the refused run written its dedupe mark, the
    /// repaired one would resolve as a redelivery and the notification would
    /// stay accepted forever.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_pipeline_holds_a_notification_whose_accepted_set_no_longer_reads()
    {
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 2, EmailThenPush);
        await AcceptedAttachmentFlow.PlantAsync(
            fixture,
            accepted.NotificationId,
            AcceptedAttachmentFlow.UnknownVersionDocument(accepted));
        MessageEnvelope trigger = AcceptedAttachmentFlow.AcceptedTrigger(accepted.NotificationId);

        AcceptedAttachmentsUnreadableException refusal =
            await Should.ThrowAsync<AcceptedAttachmentsUnreadableException>(
                () => RunPipelineAsync(trigger));

        refusal.Reason.ShouldBe(AcceptedAttachmentManifest.RefusedUnknownSchemaVersion);
        refusal.NotificationId.ShouldBe(accepted.NotificationId);
        (await AcceptedAttachmentFlow.StatusAsync(fixture, accepted.NotificationId))
            .ShouldBe(NotificationStatuses.Accepted);
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, accepted.NotificationId))
            .ShouldBeEmpty();

        await AcceptedAttachmentFlow.PlantAsync(
            fixture, accepted.NotificationId, AcceptedAttachmentFlow.WholeDocument(accepted));

        (await RunPipelineAsync(trigger)).ShouldBeOfType<MessageDisposition.Processed>();

        (await AcceptedAttachmentFlow.StatusAsync(fixture, accepted.NotificationId))
            .ShouldBe(NotificationStatuses.Dispatched);
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, accepted.NotificationId))
            .ShouldHaveSingleItem()
            .Channel.ShouldBe("email");
    }

    /// <summary>
    /// The refused run reaches the consumer loop as a failure, which returns
    /// the message to the queue, and never as a message the poison sink
    /// discards. The difference is the whole design: a discard would drop a
    /// notification whose row a person can still repair, while a return holds
    /// it until somebody does.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_refused_run_returns_its_message_to_the_queue_instead_of_discarding_it()
    {
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 1, EmailThenPush);
        await AcceptedAttachmentFlow.PlantAsync(
            fixture,
            accepted.NotificationId,
            AcceptedAttachmentFlow.UnknownVersionDocument(accepted));
        await AcceptedAttachmentFlow.RelayAsync(fixture);

        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        SqsConsumePassResult refused = await CorePipelineFixture.RunCorePassAsync(
            core, AcceptedAttachmentFlow.CoreQueue);

        refused.Failed.ShouldBe(1);
        refused.Processed.ShouldBe(0);
        refused.Discarded.ShouldBe(
            0,
            "um documento ilegível é defeito operacional reparável, e descartar a mensagem "
            + "jogaria fora uma notificação que ainda pode ser entregue depois do reparo.");
        (await AcceptedAttachmentFlow.StatusAsync(fixture, accepted.NotificationId))
            .ShouldBe(NotificationStatuses.Accepted);

        // The repair, and then the message the queue was holding all along.
        // The passes loop because the refusal put the message back with the
        // consumer's own backoff, so it is invisible for a moment.
        await AcceptedAttachmentFlow.PlantAsync(
            fixture, accepted.NotificationId, AcceptedAttachmentFlow.WholeDocument(accepted));
        var processed = 0;
        for (var pass = 0; pass < 12 && processed == 0; pass++)
        {
            processed = (await CorePipelineFixture.RunCorePassAsync(
                core, AcceptedAttachmentFlow.CoreQueue)).Processed;
        }

        processed.ShouldBe(1);
        (await AcceptedAttachmentFlow.StatusAsync(fixture, accepted.NotificationId))
            .ShouldBe(NotificationStatuses.Dispatched);
    }

    /// <summary>
    /// The dispatch reads the set before it claims the attempt, so a document
    /// that no longer reads costs the provider nothing and leaves the attempt
    /// exactly as claimable as it was.
    /// <para>
    /// The claim is what makes the ordering matter. Taken first, the attempt
    /// would sit on sending with nobody left to settle it, and the stored
    /// status is the authority that decides a redelivery, so the repair would
    /// arrive to find the attempt permanently unsendable.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_dispatch_calls_no_provider_when_the_accepted_set_no_longer_reads()
    {
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 2, EmailThenPush);
        await AcceptedAttachmentFlow.DispatchAsync(fixture);
        Guid attemptId = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem().Id;
        await AcceptedAttachmentFlow.PlantAsync(
            fixture,
            accepted.NotificationId,
            AcceptedAttachmentFlow.UnknownVersionDocument(accepted));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = SendGridAccepted }));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        MessageEnvelope trigger = AcceptedAttachmentFlow.DispatchTrigger(
            accepted.NotificationId, attemptId);

        AcceptedAttachmentsUnreadableException refusal =
            await Should.ThrowAsync<AcceptedAttachmentsUnreadableException>(
                () => RunDispatchAsync(dispatcher, trigger));

        refusal.Reason.ShouldBe(AcceptedAttachmentManifest.RefusedUnknownSchemaVersion);
        provider.RequestCount.ShouldBe(0);
        NotificationAttempt held = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem();
        held.Status.ShouldBe(NotificationAttemptStatuses.Queued);
        held.ProviderKey.ShouldBeNull();

        await AcceptedAttachmentFlow.PlantAsync(
            fixture, accepted.NotificationId, AcceptedAttachmentFlow.WholeDocument(accepted));

        (await RunDispatchAsync(dispatcher, trigger)).ShouldBeOfType<MessageDisposition.Processed>();

        provider.RequestCount.ShouldBe(
            1,
            "a chamada única depois do reparo é o que prova que o zero acima foi recusa, "
            + "e não um caminho incapaz de chamar o provedor.");
        NotificationAttempt sent = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem();
        sent.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        sent.ProviderMessageId.ShouldBe(SendGridAccepted);
    }

    /// <summary>
    /// The fallback reads the set off the notification it already loaded,
    /// while it is about to queue the next step, so a document that no longer
    /// reads stops the step before the advance is bought.
    /// <para>
    /// The unclaimed advance is the point. The claim is what tells two
    /// triggers of one step apart, and a step bought by a run that queued
    /// nothing would leave the notification with no later step and no attempt
    /// to reach it by: the repair would arrive too late.
    /// </para>
    /// <para>
    /// The repaired run settles the notification instead of queueing the push
    /// step, and that is the correct conclusion rather than a second defect:
    /// push composes no attachment into its call, so a notification carrying a
    /// set ends on that step. What the pairing shows is unchanged: the refused
    /// run wrote nothing and left the trigger replayable, and the repaired one,
    /// over the very same trigger, read the set and reached a conclusion.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_fallback_queues_no_next_attempt_when_the_accepted_set_no_longer_reads()
    {
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 2, EmailThenPush);
        await AcceptedAttachmentFlow.DispatchAsync(fixture);
        Guid failedAttemptId = await FailTheFirstStepAsync(accepted);
        await AcceptedAttachmentFlow.PlantAsync(
            fixture,
            accepted.NotificationId,
            AcceptedAttachmentFlow.UnknownVersionDocument(accepted));
        MessageEnvelope trigger = AcceptedAttachmentFlow.FallbackTrigger(
            accepted.NotificationId, failedAttemptId);

        AcceptedAttachmentsUnreadableException refusal =
            await Should.ThrowAsync<AcceptedAttachmentsUnreadableException>(
                () => RunFallbackAsync(trigger));

        refusal.Reason.ShouldBe(AcceptedAttachmentManifest.RefusedUnknownSchemaVersion);
        NotificationAttempt held = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem();
        held.Id.ShouldBe(failedAttemptId);
        held.PlanAdvancedAt.ShouldBeNull();
        (await AcceptedAttachmentFlow.StatusAsync(fixture, accepted.NotificationId))
            .ShouldBe(NotificationStatuses.Dispatched);

        await AcceptedAttachmentFlow.PlantAsync(
            fixture, accepted.NotificationId, AcceptedAttachmentFlow.WholeDocument(accepted));

        (await RunFallbackAsync(trigger)).ShouldBeOfType<MessageDisposition.Processed>();

        (await AcceptedAttachmentFlow.StatusAsync(fixture, accepted.NotificationId))
            .ShouldBe(NotificationStatuses.Failed);
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, accepted.NotificationId))
            .ShouldHaveSingleItem()
            .Id.ShouldBe(failedAttemptId);
        await AcceptedAttachmentFlow.RelayAsync(fixture);
        (await AcceptedAttachmentFlow.PublishedFailureReasonAsync(fixture, accepted.RecipientId))
            .ShouldBe(NotificationRejectionReasons.AttachmentsNotCarriedByChannel);
    }

    /// <summary>
    /// Runs the first step against a provider that refuses it, which is what
    /// produces a fallback trigger in the ordinary way. The set still reads
    /// here: what is being arranged is a notification with a step to advance.
    /// </summary>
    private async Task<Guid> FailTheFirstStepAsync(AttachedNotification accepted)
    {
        Guid attemptId = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem().Id;

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            400, """{"errors":[{"message":"invalid","field":"to"}]}""", null));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));

        (await RunDispatchAsync(
                dispatcher, AcceptedAttachmentFlow.DispatchTrigger(accepted.NotificationId, attemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, accepted.NotificationId))
            .ShouldHaveSingleItem()
            .Status.ShouldBe(NotificationAttemptStatuses.Failed);
        return attemptId;
    }

    private async Task<MessageDisposition> RunPipelineAsync(MessageEnvelope envelope)
    {
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        using IServiceScope scope = core.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<CoreMessageProcessor>()
            .ProcessAsync(envelope, CancellationToken.None);
    }

    private static async Task<MessageDisposition> RunDispatchAsync(
        ServiceProvider dispatcher,
        MessageEnvelope envelope)
    {
        using IServiceScope scope = dispatcher.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<DispatchMessageProcessor>()
            .ProcessAsync(envelope, CancellationToken.None);
    }

    private async Task<MessageDisposition> RunFallbackAsync(MessageEnvelope envelope)
    {
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        using IServiceScope scope = core.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<FallbackRequestHandler>()
            .ProcessAsync(envelope, CancellationToken.None);
    }
}
