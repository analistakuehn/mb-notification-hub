using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// The refusal of a link inside an authentication SMS, reached through the
/// fallback instead of the ingestion. It is the path that matters most for
/// this rule: SMS is where an authentication plan falls back to, so this is
/// where a security refusal would be filed as a broken template and nobody
/// would look for it again.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class FallbackAuthenticationSmsLinkTests(CorePipelineFixture fixture)
{
    private const string EmailRefused = """
        {"errors":[{"message":"The from address does not match a verified Sender Identity."}]}
        """;

    [RequiresDockerFact]
    public async Task A_link_rendered_into_the_authentication_sms_ends_the_notification_on_the_security_reason()
    {
        Guid notificationId = await FallBackToSmsAsync(code: "montebravo.com.br/x");

        Notification notification = await NotificationAsync(notificationId);
        notification.Status.ShouldBe(NotificationStatuses.Failed);

        var details = await FailureDetailsAsync(notificationId);
        details.ShouldContain(
            FallbackRequestHandler.ReasonAuthenticationSmsLink,
            Case.Sensitive,
            "a recusa de segurança chegou ao fallback como falha de template: o motivo diria "
            + "que o template está quebrado, e ninguém procuraria o bloqueio de segurança.");
        details.ShouldNotContain(FallbackRequestHandler.ReasonRenderFailed);

        // Nothing was addressed to the person on the refused step.
        (await SmsAttemptCountAsync(notificationId)).ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task The_same_plan_with_an_ordinary_code_queues_the_sms_step()
    {
        // Falsification: what ends the notification above is the link the
        // value renders, not the plan, the template or the fallback path.
        Guid notificationId = await FallBackToSmsAsync(code: "123456");

        (await SmsAttemptCountAsync(notificationId)).ShouldBe(1);
        (await NotificationAsync(notificationId)).Status.ShouldBe(NotificationStatuses.Dispatched);
    }

    /// <summary>
    /// Walks one authentication notification to the moment its e-mail step has
    /// failed and the trigger of the next step is in hand, then hands that
    /// trigger to the handler that owns the plan.
    /// </summary>
    private async Task<Guid> FallBackToSmsAsync(string code)
    {
        var application = DispatchApi.NewApplication();

        // The template declares the domain the value carries on purpose. The
        // allowlist runs first, at the door, and refuses a host it does not
        // know before the request becomes a notification; a template that
        // declared nothing would move the refusal to that earlier gate and
        // this test would stop proving which gate answers. The value stays
        // something a reader can tap, which is what the ban below reads.
        (var templateKey, _) = await DispatchApi.CreatePublishedEmailAndSmsTemplateAsync(
            fixture, application, "critical", "authentication",
            linkDomainsAllowed: ["montebravo.com.br"]);
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("email", "30s"), ("sms", null));
        (var recipientId, _, _) = await DispatchApi.RegisterEmailAndSmsRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("sms", "twilio"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(400, EmailRefused, null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth",
            variables: new { code });

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        using IServiceScope scope = core.CreateScope();
        MessageDisposition disposition = await scope.ServiceProvider
            .GetRequiredService<FallbackRequestHandler>()
            .ProcessAsync(await TriggerAsync(notificationId), CancellationToken.None);
        disposition.ShouldBeOfType<MessageDisposition.Processed>();
        return notificationId;
    }

    /// <summary>The trigger the failed e-mail step wrote, read back from the outbox row it committed.</summary>
    private async Task<MessageEnvelope> TriggerAsync(Guid notificationId)
    {
        List<string> payloads = await DispatchApi.ReadOutboxPayloadsAsync(
            fixture, "core-auth", notificationId);
        var trigger = payloads.Single(payload => payload.Contains(
            DispatchMessages.FallbackRequestedType, StringComparison.Ordinal));
        MessageEnvelopeParse parsed = MessageEnvelopeParser.Parse(trigger);
        parsed.InvalidReason.ShouldBeNull();
        return parsed.Envelope! with { SourceQueue = "core-auth" };
    }

    private async Task<Notification> NotificationAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));

    private async Task<int> SmsAttemptCountAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .CountAsync(candidate => candidate.NotificationId == notificationId
                && candidate.Channel == "sms"));

    private async Task<string> FailureDetailsAsync(Guid notificationId)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == notificationId.ToString()
                && entry.Action == "notification.failed")
            .Select(entry => entry.DetailsJson)
            .SingleAsync());
}
