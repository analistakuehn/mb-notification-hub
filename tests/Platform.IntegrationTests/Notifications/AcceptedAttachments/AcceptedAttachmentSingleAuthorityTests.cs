using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.AcceptedAttachments;

/// <summary>
/// The notification row is the only place the accepted set exists, and it
/// stays that way through everything that happens to a notification after the
/// acceptance.
/// <para>
/// One authority is not a preference. A copy on an attempt, in an outbox row
/// or in a queue message is free to disagree with the row for any path that
/// forgot to keep it in step, and the attempt is the worst of the three
/// because a terminal verdict deliberately throws away what the attempt no
/// longer needs. Two answers to which attachments were accepted is one answer
/// too many, and the wrong one reaches the recipient.
/// </para>
/// </summary>
[Collection(AcceptedAttachmentFlowCollectionDefinition.Name)]
public sealed class AcceptedAttachmentSingleAuthorityTests(AcceptedAttachmentFlowFixture fixture)
{
    private const string FcmAccepted = """{"name":"projects/test-project/messages/0:1"}""";

    private static readonly (string Channel, string? Timeout)[] EmailThenPush =
        [("email", "30s"), ("push", null)];

    private static readonly (string Channel, string? Timeout)[] EmailOnly = [("email", null)];

    /// <summary>
    /// The whole life of one notification, walked end to end, and then every
    /// attempt row, every outbox row and every queue body read back and
    /// searched for the values only the manifest carries.
    /// <para>
    /// The walk covers the first attempt, a failed step, the fallback that
    /// bought the next one and the fan-out that turned it into siblings,
    /// because each of those is a place where something could have decided the
    /// next writer needs a copy of the set to work from.
    /// </para>
    /// <para>
    /// Every absence below is asserted next to a presence. A capture that came
    /// back empty, a scan whose query stopped matching rows and a walk that
    /// never reached the fan-out would all satisfy "no copy anywhere" while
    /// proving nothing at all.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task No_attempt_no_outbox_row_and_no_queue_message_carries_the_accepted_set()
    {
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 2, EmailThenPush, deviceCount: 2);
        await AcceptedAttachmentFlow.DispatchAsync(fixture);

        List<string> firstAttemptQueue = await AcceptedAttachmentFlow.PeekAsync(
            fixture, AcceptedAttachmentFlow.EmailQueue);
        Guid emailAttemptId = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem().Id;

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : request.Path.Contains("mail", StringComparison.Ordinal)
                ? new FakeProviderResponse(400, """{"errors":[{"message":"invalid"}]}""", null)
                : new FakeProviderResponse(200, FcmAccepted, null));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));

        (await RunDispatchAsync(
                dispatcher,
                AcceptedAttachmentFlow.DispatchTrigger(accepted.NotificationId, emailAttemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();
        (await RunFallbackAsync(
                AcceptedAttachmentFlow.FallbackTrigger(accepted.NotificationId, emailAttemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();
        await AcceptedAttachmentFlow.RelayAsync(fixture);

        Guid pushAttemptId = (await AcceptedAttachmentFlow.AttemptsAsync(
                fixture, accepted.NotificationId))
            .Single(attempt => attempt.Channel == "push")
            .Id;
        (await RunDispatchAsync(
                dispatcher,
                AcceptedAttachmentFlow.DispatchTrigger(accepted.NotificationId, pushAttemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();
        await AcceptedAttachmentFlow.RelayAsync(fixture);

        // The walk reached what it had to reach: two channels and the fan-out
        // sibling the second device produced. Without this, everything below
        // would be a scan over a notification that never left its first step.
        List<NotificationAttempt> attempts = await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId);
        attempts.Count.ShouldBe(3);
        attempts.Count(attempt => attempt.Channel == "push").ShouldBe(2);

        List<string> attemptRows = await AcceptedAttachmentFlow.AttemptRowsAsync(
            fixture, accepted.NotificationId);
        List<string> outboxRows = await AcceptedAttachmentFlow.OutboxRowsAsync(fixture);
        List<string> queueBodies =
        [
            .. firstAttemptQueue,
            .. await AcceptedAttachmentFlow.PeekAsync(fixture, AcceptedAttachmentFlow.PushQueue),
            .. await AcceptedAttachmentFlow.PeekAsync(fixture, AcceptedAttachmentFlow.CoreQueue),
        ];

        // Each capture is proved non-empty by something that has to be in it,
        // and the notification identifier is exactly that: every one of these
        // surfaces carries identifiers, and only identifiers.
        attemptRows.Count.ShouldBe(3);
        outboxRows.Count(row => row.Contains(accepted.NotificationId.ToString(), StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(3);
        queueBodies.Count(body => body.Contains(accepted.NotificationId.ToString(), StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(1);

        ShouldCarryNoManifest(accepted, "notification_attempt", attemptRows);
        ShouldCarryNoManifest(accepted, "outbox", outboxRows);
        ShouldCarryNoManifest(accepted, "queue", queueBodies);

        // And the row still answers with the set, unchanged, after all of it.
        (await AcceptedAttachmentFlow.StoredSetAsync(fixture, accepted.NotificationId))
            .Select(item => item.Reference)
            .ShouldBe(accepted.Attachments.Select(attachment => attachment.Reference));
    }

    /// <summary>
    /// The terminal verdict of an attempt settles what that attempt keeps, and
    /// the snapshot of the notification is not part of it.
    /// <para>
    /// The verdict really does throw something away here: the sealed envelope
    /// of the attempt is rewritten with its masked form alone, which is why the
    /// template declares a sensitive variable. That rewrite is the neighbour of
    /// this assertion. Without it the row would have been left untouched by a
    /// verdict that discarded nothing, and "the snapshot survived" would be a
    /// sentence about a statement that never ran.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_snapshot_survives_a_verdict_that_discards_what_the_attempt_carried()
    {
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 2, EmailOnly, sensitiveVariables: ["code"]);
        await AcceptedAttachmentFlow.DispatchAsync(fixture);
        NotificationAttempt queued = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem();
        var documentBefore = await AcceptedAttachmentFlow.StoredDocumentAsync(
            fixture, accepted.NotificationId);

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "sg-terminal" }));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));

        (await RunDispatchAsync(
                dispatcher,
                AcceptedAttachmentFlow.DispatchTrigger(accepted.NotificationId, queued.Id)))
            .ShouldBeOfType<MessageDisposition.Processed>();

        NotificationAttempt settled = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem();
        settled.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        settled.RenderedContentEncrypted.ShouldNotBe(
            queued.RenderedContentEncrypted,
            "o veredito precisa ter descartado a forma completa do conteúdo; sem isso não há "
            + "descarte nenhum para o snapshot sobreviver.");

        (await AcceptedAttachmentFlow.StoredDocumentAsync(fixture, accepted.NotificationId))
            .ShouldBe(documentBefore);
        (await AcceptedAttachmentFlow.StoredSetAsync(fixture, accepted.NotificationId))
            .Select(item => item.Reference)
            .ShouldBe(accepted.Attachments.Select(attachment => attachment.Reference));
    }

    /// <summary>
    /// The owning module moving underneath a notification moves nothing in the
    /// set that notification was accepted over.
    /// <para>
    /// The revocation between the acceptance and the fallback is the whole
    /// arrangement, and the state assertion on the module is what proves it
    /// happened: a revocation that silently did nothing would leave this test
    /// asserting that an unchanged world stayed unchanged.
    /// </para>
    /// <para>
    /// The fallback goes on and queues the next step, and that is deliberate.
    /// The snapshot answers what was accepted and never whether it may still be
    /// used, so a revoked release has to be caught by the reading that happens
    /// immediately before the call to the provider, not by a document frozen
    /// weeks earlier. Ending the notification here would be this code deciding
    /// eligibility from a place that cannot see it.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task Revoking_an_attachment_after_the_acceptance_moves_nothing_in_the_stored_set()
    {
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 2, EmailThenPush);
        await AcceptedAttachmentFlow.DispatchAsync(fixture);
        Guid emailAttemptId = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem().Id;
        AcceptedAttachmentSet beforeRevocation = await AcceptedAttachmentFlow.StoredSetAsync(
            fixture, accepted.NotificationId);

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            400, """{"errors":[{"message":"invalid"}]}""", null));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await RunDispatchAsync(
                dispatcher,
                AcceptedAttachmentFlow.DispatchTrigger(accepted.NotificationId, emailAttemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();

        SeededAttachment revoked = accepted.Attachments[0];
        await AcceptedAttachmentFlow.RevokeAsync(fixture, revoked.Id);
        (await AcceptedAttachmentFlow.AttachmentStateAsync(fixture, revoked.Id))
            .ShouldBe(AttachmentStates.Revoked);

        (await RunFallbackAsync(
                AcceptedAttachmentFlow.FallbackTrigger(accepted.NotificationId, emailAttemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();

        AcceptedAttachmentSet afterFallback = await AcceptedAttachmentFlow.StoredSetAsync(
            fixture, accepted.NotificationId);
        afterFallback.Count.ShouldBe(beforeRevocation.Count);
        for (var index = 0; index < afterFallback.Count; index++)
        {
            afterFallback[index].Reference.ShouldBe(beforeRevocation[index].Reference);
            afterFallback[index].ContentIdentity.ShouldBe(beforeRevocation[index].ContentIdentity);
            afterFallback[index].Name.ShouldBe(beforeRevocation[index].Name);
            afterFallback[index].MediaType.ShouldBe(beforeRevocation[index].MediaType);
            afterFallback[index].Length.ShouldBe(beforeRevocation[index].Length);
        }

        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, accepted.NotificationId))
            .Select(attempt => attempt.Channel)
            .ShouldBe(["email", "push"]);
    }

    /// <summary>
    /// The values only the manifest carries, looked for one by one so the
    /// failure names the surface and the value instead of reporting that
    /// something somewhere matched.
    /// </summary>
    private static void ShouldCarryNoManifest(
        AttachedNotification accepted,
        string surface,
        IReadOnlyList<string> captured)
    {
        foreach (var sentinel in accepted.Sentinels)
        {
            captured.Any(text => text.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
                .ShouldBeFalse(
                    $"a superfície '{surface}' carrega '{sentinel}', que só existe no manifesto "
                    + "aceito; uma segunda autoridade do conjunto pode divergir da linha da "
                    + "notificação em qualquer caminho que esqueça de mantê-la em dia.");
        }
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
