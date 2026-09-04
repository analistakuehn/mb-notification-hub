using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capability;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.AcceptedAttachments;

/// <summary>
/// The pipeline of a notification that was already accepted with a set, run by
/// hosts that take no new attachments at all.
/// <para>
/// The claim and the registration are the two doors the deployment switch
/// closes. Nothing downstream of an acceptance asks that question, and these
/// cases are what says so by running the downstream with the switch off rather
/// than by reading the call graph: the attempt goes out with every member of
/// the set, the fallback still decides over the frozen document, and a request
/// that names no attachment is accepted exactly as before.
/// </para>
/// <para>
/// What they do not prove: that a set may still be accepted while the switch
/// is off. It may not, and the arm that measures that refusal is here beside
/// the acceptance it stands against.
/// </para>
/// </summary>
[Collection(AcceptedAttachmentFlowCollectionDefinition.Name)]
public sealed class AcceptedAttachmentCapabilityTests(AcceptedAttachmentFlowFixture fixture)
{
    private const string SendGridAccepted = "sg-message-capability";

    private static readonly (string Channel, string? Timeout)[] EmailOnly = [("email", null)];

    private static readonly (string Channel, string? Timeout)[] EmailThenPush =
        [("email", "30s"), ("push", null)];

    /// <summary>
    /// The switch names the deployment state of the capability for a worker
    /// composition, stated rather than inherited: the settings a worker role is
    /// built from carry no such key, and an arm that relied on that silence
    /// would be measuring an omission instead of the closed state.
    /// </summary>
    private static Dictionary<string, string?> CapabilityOff(
        IDictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{AttachmentCapabilityOptions.SectionName}:AcceptsNewAttachments"] = "false",
        };
        if (extra is not null)
        {
            foreach ((var key, var value) in extra)
            {
                settings[key] = value;
            }
        }

        return settings;
    }

    /// <summary>
    /// The attempt of a notification accepted before the reversal. The provider
    /// is called with every member of the set, and the document on the row is
    /// the one the acceptance wrote: the switch reached neither.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attempt_of_an_accepted_notification_still_carries_the_whole_set_while_the_capability_is_off()
    {
        AttachedNotification carrying = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 2, EmailOnly);
        await AcceptedAttachmentFlow.DispatchAsync(fixture);
        var frozen = await AcceptedAttachmentFlow.StoredDocumentAsync(
            fixture, carrying.NotificationId);

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = SendGridAccepted }));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            CapabilityOff(DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress)));

        (await RunDispatchAsync(dispatcher, carrying)).ShouldBeOfType<MessageDisposition.Processed>();

        provider.Requests.TryDequeue(out FakeProviderRequest? call).ShouldBeTrue();
        JsonElement body = JsonDocument.Parse(call!.Body).RootElement;
        JsonElement[] attachments = [.. body.GetProperty("attachments").EnumerateArray()];
        attachments.Select(item => item.GetProperty("filename").GetString())
            .ShouldBe(carrying.Attachments.Select(attachment => attachment.Name));

        NotificationAttempt attempt = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, carrying.NotificationId)).ShouldHaveSingleItem();
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Sent);

        // The reversal preserves the document, read back off the row rather
        // than asserted from the absence of a statement that would change it.
        (await AcceptedAttachmentFlow.StoredDocumentAsync(fixture, carrying.NotificationId))
            .ShouldBe(frozen);
    }

    /// <summary>
    /// The fallback of a notification accepted before the reversal. It reads
    /// the frozen document, decides over it and publishes the reason, and the
    /// neighbour that carries nothing walks to the step behind: both halves of
    /// the handler run under a host that takes no new attachments.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_fallback_still_decides_over_the_frozen_set_while_the_capability_is_off()
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
            CapabilityOff(DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress)));

        Guid carryingAttemptId = await FailTheEmailStepAsync(dispatcher, carrying);
        Guid plainAttemptId = await FailTheEmailStepAsync(dispatcher, plain);

        (await RunFallbackAsync(
                AcceptedAttachmentFlow.FallbackTrigger(carrying.NotificationId, carryingAttemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();
        (await RunFallbackAsync(
                AcceptedAttachmentFlow.FallbackTrigger(plain.NotificationId, plainAttemptId)))
            .ShouldBeOfType<MessageDisposition.Processed>();
        await AcceptedAttachmentFlow.RelayAsync(fixture);

        // The set is read, the plan is walked and the notification is settled
        // with the published reason: the fallback reached a decision instead of
        // stalling on an item accepted before the reversal.
        (await AcceptedAttachmentFlow.StatusAsync(fixture, carrying.NotificationId))
            .ShouldBe(NotificationStatuses.Failed);
        (await AcceptedAttachmentFlow.PublishedFailureReasonAsync(fixture, carrying.RecipientId))
            .ShouldBe(NotificationRejectionReasons.AttachmentsNotCarriedByChannel);
        (await AcceptedAttachmentFlow.StoredSetAsync(fixture, carrying.NotificationId))
            .Select(member => member.Reference)
            .ShouldBe(carrying.Attachments.Select(attachment => attachment.Reference));

        // The other half of the same handler, under the same closed host: the
        // notification that carries nothing walks to the step behind. Without
        // it, the settlement above would be satisfied by a fallback that could
        // not move anything anywhere.
        (await AcceptedAttachmentFlow.AttemptsAsync(fixture, plain.NotificationId))
            .Select(attempt => attempt.Channel)
            .ShouldBe(["email", "push"]);
    }

    /// <summary>
    /// The path that carries no attachment, over the very host that refuses a
    /// set. It is accepted exactly as before, and the request that names a
    /// released set on that same host is refused: the door is on the set and
    /// not on the ingestion.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_request_that_names_no_attachment_is_accepted_while_the_capability_is_off()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);
        SeededAttachment released = await ClaimableAttachments.ReleasedWithContentAsync(
            fixture, arrangement.Application);
        using WebApplicationFactory<Program> closed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(CapabilityOff())));
        using HttpClient producer = fixture.CreateProducerClient(
            closed, "capability-producer", NotificationsApi.SendTransactional);

        using HttpResponseMessage plain = await PostAsync(producer, arrangement, references: []);
        var accepted = await plain.Content.ReadAsStringAsync();

        plain.StatusCode.ShouldBe(HttpStatusCode.Accepted, accepted);

        // The same host, the same producer and the same arrangement, differing
        // only in the set the request names. Without it the acceptance above
        // would be satisfied by a switch that was never in force.
        using HttpResponseMessage carrying = await PostAsync(
            producer, arrangement, [released.Reference]);
        var refused = await carrying.Content.ReadAsStringAsync();

        carrying.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, refused);
        refused.ShouldContain("attachments-not-claimable");
    }

    /// <summary>
    /// One request through the closed host, with a recipient of its own so the
    /// deduplication window of the policy never reads two requests of one
    /// arrangement as one request asked for twice.
    /// </summary>
    private async Task<HttpResponseMessage> PostAsync(
        HttpClient producer,
        AttachmentArrangement arrangement,
        string[] references)
    {
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        return await NotificationsApi.PostNotificationAsync(
            producer,
            Body(arrangement, recipientId, references),
            Guid.NewGuid().ToString("N"));
    }

    private static Dictionary<string, object?> Body(
        AttachmentArrangement arrangement,
        string recipientId,
        string[] references)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["application"] = arrangement.Application,
            ["recipientId"] = recipientId,
            ["class"] = AcceptedAttachmentFlow.NotificationClass,
            ["templateKey"] = arrangement.TemplateKey,
            ["locale"] = "pt-BR",
            ["variables"] = new { code = "123456" },
            ["ttlSeconds"] = 300,
        };
        if (references.Length > 0)
        {
            body["attachments"] = references;
        }

        return body;
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

    /// <summary>
    /// One fallback pass, over a core composition that takes no new
    /// attachments. The role composes the claim, so the switch is in force in
    /// the very host that runs the handler.
    /// </summary>
    private async Task<MessageDisposition> RunFallbackAsync(MessageEnvelope envelope)
    {
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider(CapabilityOff());
        using IServiceScope scope = core.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<FallbackRequestHandler>()
            .ProcessAsync(envelope, CancellationToken.None);
    }
}
