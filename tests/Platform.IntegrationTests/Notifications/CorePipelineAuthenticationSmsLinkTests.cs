using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// The second gate against a link in an authentication SMS. Publication
/// refuses the content a human wrote; this one refuses the message a variable
/// value produced at request time, which is the only way a link still reaches
/// a rendered authentication body.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class CorePipelineAuthenticationSmsLinkTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_variable_that_renders_a_link_into_an_authentication_sms_is_rejected()
    {
        var application = DispatchApi.NewApplication();

        // The template declares the domain the value carries on purpose. The
        // allowlist runs first, at the door, and refuses a host it does not
        // know before the request becomes a notification; a template that
        // declared nothing would move the refusal to that earlier gate and
        // this test would stop proving which gate answers. The value stays
        // something a reader can tap, which is what the ban below reads.
        (var templateKey, _) = await DispatchApi.CreatePublishedSmsTemplateAsync(
            fixture, application, "transactional", "authentication",
            linkDomainsAllowed: ["montebravo.com.br"]);
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("sms", null));
        (var recipientId, _) = await DispatchApi.RegisterSmsRecipientAsync(fixture);

        Guid notificationId = await ProcessAsync(
            application, templateKey, recipientId, code: "montebravo.com.br/x");

        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Rejected);

        // No attempt exists, so nothing was ever addressed to the person.
        (await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .CountAsync(attempt => attempt.NotificationId == notificationId)))
            .ShouldBe(0);

        // The reason is the security one, kept apart from a render failure:
        // the template is fine and the content was refused.
        var details = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(auditEvent => auditEvent.Action == "notification.rejected"
                && auditEvent.EntityId == notificationId.ToString())
            .Select(auditEvent => auditEvent.DetailsJson)
            .SingleAsync());
        details.ShouldContain("authentication-sms-link");
        details.ShouldNotContain("template-render-failed");
    }

    [RequiresDockerFact]
    public async Task The_same_template_with_an_ordinary_code_dispatches()
    {
        // Falsification: what stops the notification above is the link the
        // value renders, not the template, the channel or the purpose.
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedSmsTemplateAsync(
            fixture, application, "transactional", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("sms", null));
        (var recipientId, _) = await DispatchApi.RegisterSmsRecipientAsync(fixture);

        Guid notificationId = await ProcessAsync(
            application, templateKey, recipientId, code: "123456");

        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notificationId)
            .Select(candidate => candidate.Status)
            .SingleAsync()))
            .ShouldBe(NotificationStatuses.Dispatched);
    }

    /// <summary>
    /// Accepts one notification with the given code value and runs it through
    /// the authentication lane, which is where a template of this purpose
    /// travels.
    /// </summary>
    private async Task<Guid> ProcessAsync(
        string application,
        string templateKey,
        string recipientId,
        string code)
    {
        HttpClient producer = fixture.CreateProducerClient(
            "auth-service", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            new
            {
                application,
                recipientId,
                @class = "transactional",
                templateKey,
                locale = "pt-BR",
                variables = new { code },
                ttlSeconds = 300,
            },
            Guid.NewGuid().ToString("N"));
        accepted.EnsureSuccessStatusCode();
        JsonElement body = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString(), out Guid id).ShouldBeTrue();

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(worker, "core-auth")).Processed
            .ShouldBeGreaterThanOrEqualTo(1);
        return id;
    }
}
