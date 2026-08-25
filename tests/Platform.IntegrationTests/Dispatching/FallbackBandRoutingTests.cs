using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// Which band a fallback trigger drains in. The relay reads the band off the
/// destination, so the queue a trigger names decides how fast the second half
/// of an authentication code gets moving. The first step of such a code
/// already drains in the top band; a trigger addressed to the class queue
/// would silently demote the step that has to rescue it.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class FallbackBandRoutingTests(CorePipelineFixture fixture)
{
    private const string InvalidArgumentBody = """
        {"error":{"code":400,"message":"Invalid argument.","status":"INVALID_ARGUMENT",
        "details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"INVALID_ARGUMENT"}]}}
        """;

    [RequiresDockerFact]
    public async Task An_authentication_trigger_is_addressed_to_the_auth_queue_and_claimed_by_the_top_band()
    {
        var authRecipient = await FailedPushAsync("authentication");
        var ordinaryRecipient = await FailedPushAsync("order-updates");

        OutboxMessage authTrigger = await TriggerAsync(authRecipient);
        OutboxMessage ordinaryTrigger = await TriggerAsync(ordinaryRecipient);

        authTrigger.Destination.ShouldBe("core-auth");
        authTrigger.PriorityBand.ShouldBe((int)OutboxBand.Auth);
        authTrigger.PriorityClass.ShouldBe(
            "critical",
            "o destino muda, a classe armazenada não: a banda é classificação de leitura.");

        // The control proves the routing is the template's purpose and not the
        // class: same class, same plan, same failure, ordinary purpose.
        ordinaryTrigger.Destination.ShouldBe("core-critical");
        ordinaryTrigger.PriorityBand.ShouldBe((int)OutboxBand.Critical);

        // A relay restricted to the top band claims one and never the other.
        await using ServiceProvider relay = fixture.BuildRelayProvider(
            new Dictionary<string, string?> { ["Platform:Messaging:Relay:Bands:0"] = "auth" });
        while ((await CorePipelineFixture.RunRelayPassAsync(relay)).Published > 0)
        {
        }

        (await TriggerAsync(authRecipient)).SentAt.ShouldNotBeNull(
            "o gatilho de um fluxo de autenticação precisa ser reivindicado pela banda de topo.");
        (await TriggerAsync(ordinaryRecipient)).SentAt.ShouldBeNull();
    }

    /// <summary>
    /// Walks one notification of the given template purpose to a failed push
    /// step: the provider refuses the only device, and the plan advances in the
    /// same transaction as the failure. Answers with the recipient id, which is
    /// the key every message of that notification carries.
    /// </summary>
    private async Task<string> FailedPushAsync(string purpose)
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", purpose);
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", "30s"), ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(
            fixture, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        var isAuthentication = string.Equals(purpose, "authentication", StringComparison.Ordinal);
        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(400, InvalidArgumentBody, null));
        await DispatchApi.AcceptAndRouteAsync(
            fixture,
            application,
            templateKey,
            "critical",
            recipientId,
            isAuthentication ? "core-auth" : "core-critical");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(
                dispatcher, isAuthentication ? "dispatch-push-auth" : "dispatch-push-critical"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        return recipientId;
    }

    /// <summary>
    /// The single fallback trigger of one recipient. Keyed by the recipient,
    /// which is the record key of every message of the notification, because
    /// the payload column is jsonb and jsonb answers no pattern operator.
    /// </summary>
    private async Task<OutboxMessage> TriggerAsync(string recipientId)
        => await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.EventType == DispatchMessages.FallbackRequestedType
                && message.MessageKey == recipientId));
}
