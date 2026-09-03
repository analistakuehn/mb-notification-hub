using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.AcceptedAttachments;

/// <summary>
/// What happens to a notification whose accepted set the plan's channel cannot
/// carry.
/// <para>
/// It ends, with a published reason, and it ends where the plan says rather
/// than somewhere else. Nothing is dropped from the set, nothing becomes a
/// link, and no other channel is tried: carrying the set is a property of the
/// message, so a channel behind the refused one would put the same incomplete
/// message in front of the same person. The alternative this suite exists to
/// forbid is the quiet one, where the notification leaves without the
/// documents its producer was told had been accepted and nothing anywhere
/// says so.
/// </para>
/// <para>
/// Every refusal below is measured beside a notification that goes through:
/// the same arrangement, the same plan, the same channels and the same
/// worker, differing only in whether a set was accepted. Without the
/// neighbour, each zero would be satisfied by a plan, a recipient or a
/// template that could not have worked anyway.
/// </para>
/// </summary>
[Collection(AcceptedAttachmentFlowCollectionDefinition.Name)]
public sealed class AcceptedAttachmentRoutingTests(AcceptedAttachmentFlowFixture fixture)
{
    private const string SendGridAccepted = "sg-message-routing";

    /// <summary>Push first, e-mail behind it: the plan a walk-forward would rescue.</summary>
    private static readonly (string Channel, string? Timeout)[] PushThenEmail =
        [("push", "30s"), ("email", null)];

    private static readonly (string Channel, string? Timeout)[] EmailThenPush =
        [("email", "30s"), ("push", null)];

    private static readonly (string Channel, string? Timeout)[] EmailOnly = [("email", null)];

    /// <summary>
    /// The first step of the plan is push, and push composes no attachment
    /// into its call. The notification is rejected before an attempt exists,
    /// and the step behind it is not promoted: an e-mail attempt here would be
    /// the presence of an attachment reordering a published plan.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_first_step_that_carries_no_attachment_ends_the_notification_before_any_attempt()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, PushThenEmail);
        AttachedNotification carrying = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 2);
        AttachedNotification plain = await AcceptedAttachmentFlow.AcceptWithoutAttachmentsAsync(
            fixture, arrangement);

        await AcceptedAttachmentFlow.RelayAsync(fixture);
        await using (ServiceProvider core = fixture.BuildCoreWorkerProvider())
        {
            for (var pass = 0; pass < 8; pass++)
            {
                await CorePipelineFixture.RunCorePassAsync(core, AcceptedAttachmentFlow.CoreQueue);
            }
        }

        await AcceptedAttachmentFlow.RelayAsync(fixture);

        (await AcceptedAttachmentFlow.StatusAsync(fixture, carrying.NotificationId))
            .ShouldBe(NotificationStatuses.Rejected);
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, carrying.NotificationId))
            .ShouldBeEmpty(
                "uma tentativa em qualquer canal seria a degradação que esta recusa existe "
                + "para impedir: o conjunto aceito não sairia com a mensagem.");
        (await AcceptedAttachmentFlow.PublishedRejectionReasonAsync(fixture, carrying.RecipientId))
            .ShouldBe(NotificationRejectionReasons.AttachmentsNotCarriedByChannel);
        (await AuditedAsync(carrying.NotificationId, "notification.rejected")).ShouldBe(1);

        // The set is exactly the set it was accepted over: nothing was removed
        // to make the plan work, which is the other half of the refusal.
        (await AcceptedAttachmentFlow.StoredSetAsync(fixture, carrying.NotificationId))
            .Select(item => item.Reference)
            .ShouldBe(carrying.Attachments.Select(attachment => attachment.Reference));

        // The neighbour: same plan, same channels, same worker, no set. It is
        // routed onto push, which is what says the refusal above was about the
        // attachments and not about a plan nobody could have used.
        (await AcceptedAttachmentFlow.StatusAsync(fixture, plain.NotificationId))
            .ShouldBe(NotificationStatuses.Dispatched);
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, plain.NotificationId))
            .ShouldHaveSingleItem()
            .Channel.ShouldBe("push");
    }

    /// <summary>
    /// The e-mail step failed and the next step of the plan is push. The
    /// fallback ends the notification there instead of walking forward looking
    /// for a channel that carries the set.
    /// <para>
    /// Walking forward is what the same method does for an ineligible step,
    /// and it is right there: eligibility is about the recipient, so a
    /// destination that died is a reason to try the next one. Carrying the set
    /// is about the message, so skipping is not a rescue, it is the plan being
    /// rewritten by the presence of an attachment.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_fallback_ends_the_notification_instead_of_walking_to_another_channel()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailThenPush);
        AttachedNotification carrying = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 1);
        AttachedNotification plain = await AcceptedAttachmentFlow.AcceptWithoutAttachmentsAsync(
            fixture, arrangement);
        await AcceptedAttachmentFlow.DispatchAllAsync(
            fixture, carrying.NotificationId, plain.NotificationId);

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(
            new FakeProviderResponse(400, """{"errors":[{"message":"invalid"}]}""", null));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));

        Guid carryingAttemptId = await FailTheEmailStepAsync(dispatcher, carrying);
        Guid plainAttemptId = await FailTheEmailStepAsync(dispatcher, plain);

        (await RunFallbackAsync(
                AcceptedAttachmentFlow.FallbackTrigger(carrying.NotificationId, carryingAttemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();
        (await RunFallbackAsync(
                AcceptedAttachmentFlow.FallbackTrigger(plain.NotificationId, plainAttemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();
        await AcceptedAttachmentFlow.RelayAsync(fixture);

        (await AcceptedAttachmentFlow.StatusAsync(fixture, carrying.NotificationId))
            .ShouldBe(NotificationStatuses.Failed);
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, carrying.NotificationId))
            .Select(attempt => attempt.Channel)
            .ShouldBe(
                ["email"],
                "nenhuma tentativa degradada em outro canal: o passo seguinte não transporta "
                + "o conjunto e a notificação termina no passo que o plano nomeou.");
        (await AcceptedAttachmentFlow.PublishedFailureReasonAsync(fixture, carrying.RecipientId))
            .ShouldBe(NotificationRejectionReasons.AttachmentsNotCarriedByChannel);

        // The neighbour walked the very step the refusal above stopped at.
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, plain.NotificationId))
            .Select(attempt => attempt.Channel)
            .ShouldBe(["email", "push"]);
    }

    /// <summary>
    /// The set reaches the provider. A send of a notification carrying one
    /// puts every member of it in the call, in the order it was claimed in,
    /// with the file name and the media type the release was granted over.
    /// <para>
    /// It is the claim the whole capability rests on, and until it is measured
    /// end to end the refusals above are rules about a path that carries
    /// nothing. What this does not measure is the exact bytes of each member:
    /// the decoded length is compared here, and the identity of the bytes is
    /// settled by the witness the adapter records over the very pass that
    /// writes them.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_send_puts_every_member_of_the_accepted_set_in_the_provider_call()
    {
        AttachedNotification carrying = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 2, EmailOnly);
        await AcceptedAttachmentFlow.DispatchAsync(fixture);

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = SendGridAccepted }));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));

        (await RunDispatchAsync(dispatcher, carrying)).ShouldBeOfType<MessageDisposition.Processed>();

        provider.Requests.TryDequeue(out FakeProviderRequest? call).ShouldBeTrue();
        JsonElement body = JsonDocument.Parse(call!.Body).RootElement;
        JsonElement[] attachments = [.. body.GetProperty("attachments").EnumerateArray()];

        attachments.Length.ShouldBe(
            carrying.Attachments.Count,
            "o conjunto vai inteiro ou não vai: um membro a menos é a mensagem incompleta "
            + "que o produtor foi informado que carregaria os documentos.");
        attachments.Select(item => item.GetProperty("filename").GetString())
            .ShouldBe(carrying.Attachments.Select(attachment => attachment.Name));
        attachments.Select(item => item.GetProperty("type").GetString())
            .ShouldBe(carrying.Attachments.Select(attachment => attachment.MediaType));
        attachments.Select(item => (long)Convert.FromBase64String(
                item.GetProperty("content").GetString()!).Length)
            .ShouldBe(carrying.Attachments.Select(attachment => attachment.Length));
    }

    /// <summary>
    /// The last line, measured where it matters: an adapter that does not
    /// carry the set is not called at all, even when the attempt reached the
    /// send.
    /// <para>
    /// The route refuses such a plan, so reaching here means the plan was
    /// already wrong: a channel repointed at another adapter after the
    /// admission, or a defect in the planning. The arrangement reproduces
    /// exactly that by pointing the configured e-mail key at an adapter that
    /// answers no, and what the guard buys is that the property holds on the
    /// object that makes the call rather than on the one that planned it.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_send_refuses_an_adapter_that_would_drop_the_set_instead_of_calling_it()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);
        AttachedNotification refused = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 1);
        AttachedNotification sent = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 1);
        await AcceptedAttachmentFlow.DispatchAllAsync(
            fixture, refused.NotificationId, sent.NotificationId);

        var dropping = new CountingEmailProvider(carriesAttachments: false);
        await using (ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            replaceServices: services => Replace(services, dropping)))
        {
            (await RunDispatchAsync(dispatcher, refused)).ShouldBeOfType<MessageDisposition.Processed>();
        }

        NotificationAttempt attempt = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, refused.NotificationId)).ShouldHaveSingleItem();
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Failed);
        attempt.ErrorCode.ShouldBe(DispatchMessageProcessor.ErrorAttachmentsNotCarried);
        dropping.CallCount.ShouldBe(
            0,
            "o adaptador que não transporta o conjunto não pode ser chamado: a mensagem "
            + "chegaria ao destinatário sem os anexos aceitos.");

        // The same worker, the same notification shape and the same set, with
        // an adapter that carries: the zero above is a refusal and not a path
        // that could never have called anything.
        var carrying = new CountingEmailProvider(carriesAttachments: true);
        await using (ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            replaceServices: services => Replace(services, carrying)))
        {
            (await RunDispatchAsync(dispatcher, sent)).ShouldBeOfType<MessageDisposition.Processed>();
        }

        carrying.CallCount.ShouldBe(1);
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, sent.NotificationId))
            .ShouldHaveSingleItem()
            .Status.ShouldBe(NotificationAttemptStatuses.Sent);
    }

    /// <summary>
    /// Replaces the hosted adapters with one that answers for the configured
    /// e-mail key, which is how a deployment whose channel points at an
    /// adapter with other properties is reproduced.
    /// </summary>
    private static void Replace(IServiceCollection services, IChannelProvider provider)
    {
        services.RemoveAll<IChannelProvider>();
        services.AddSingleton(provider);
    }

    private async Task<Guid> FailTheEmailStepAsync(
        ServiceProvider dispatcher,
        AttachedNotification notification)
    {
        Guid attemptId = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, notification.NotificationId)).ShouldHaveSingleItem().Id;
        using IServiceScope scope = dispatcher.CreateScope();
        (await scope.ServiceProvider
                .GetRequiredService<DispatchMessageProcessor>()
                .ProcessAsync(
                    AcceptedAttachmentFlow.DispatchTrigger(notification.NotificationId, attemptId),
                    CancellationToken.None))
            .ShouldBeOfType<MessageDisposition.Processed>();
        return attemptId;
    }

    private async Task<MessageDisposition> RunDispatchAsync(
        ServiceProvider dispatcher,
        AttachedNotification notification)
    {
        Guid attemptId = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, notification.NotificationId)).ShouldHaveSingleItem().Id;
        using IServiceScope scope = dispatcher.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<DispatchMessageProcessor>()
            .ProcessAsync(
                AcceptedAttachmentFlow.DispatchTrigger(notification.NotificationId, attemptId),
                CancellationToken.None);
    }

    private async Task<MessageDisposition> RunFallbackAsync(MessageEnvelope envelope)
    {
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        using IServiceScope scope = core.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<FallbackRequestHandler>()
            .ProcessAsync(envelope, CancellationToken.None);
    }

    private async Task<int> AuditedAsync(Guid notificationId, string action)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == action
                && auditEvent.EntityId == notificationId.ToString()));

    /// <summary>
    /// An adapter registered under the configured e-mail key whose attachment
    /// answer the test chooses, counting every call it is asked to make.
    /// </summary>
    private sealed class CountingEmailProvider(bool carriesAttachments) : IChannelProvider
    {
        private int _callCount;

        public Channel Channel => Channel.Email;

        public string ProviderKey => "sendgrid";

        public bool CarriesAttachments => carriesAttachments;

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<ProviderResult> SendAsync(
            DispatchRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(ProviderResult.Accepted(SendGridAccepted));
        }
    }
}
