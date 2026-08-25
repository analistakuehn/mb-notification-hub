using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// Which sender pool an SMS leaves through. The pool carries the brand the
/// recipient reads, so what selects it has to be the application the
/// notification belongs to and not a setting of the process that happens to
/// send it.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class DispatchSmsSenderPoolTests(CorePipelineFixture fixture)
{
    private const string Accepted = """{"sid":"SM-pool"}""";

    [RequiresDockerFact]
    public async Task The_messaging_service_follows_the_application_of_the_notification()
    {
        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(201, Accepted, null));

        // Two applications in one process, one with a pool of its own and one
        // without: a deployment-wide setting cannot answer both.
        (var withPool, var pooledNumber) = await QueueSmsAsync();
        (_, var unpooledNumber) = await QueueSmsAsync();

        Dictionary<string, string?> settings = DispatchApi.ProviderSettings(
            provider.BaseAddress, provider.BaseAddress, twilioBase: provider.BaseAddress);
        settings[$"Modules:Dispatch:Providers:Twilio:MessagingServiceSids:{withPool}"] = "MG-marca";

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(settings);
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-sms-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-sms-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(0);

        FormOf(provider, pooledNumber)["MessagingServiceSid"].ShouldBe(
            "MG-marca",
            "o Messaging Service não seguiu a aplicação da notificação; o destinatário leria a "
            + "marca de outro remetente.");

        // Falsification: the map is read per application, and an application
        // outside it keeps the pool of the deployment.
        FormOf(provider, unpooledNumber)["MessagingServiceSid"].ShouldBe("MG-test");
    }

    /// <summary>One SMS notification of a brand-new application, queued and waiting on its queue.</summary>
    private async Task<(string Application, string PhoneNumber)> QueueSmsAsync()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedSmsTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("sms", null));
        (var recipientId, var phoneNumber) = await DispatchApi.RegisterSmsRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("sms", "twilio"));
        await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");
        return (application, phoneNumber);
    }

    private static Dictionary<string, string> FormOf(FakeProviderServer provider, string phoneNumber)
    {
        FakeProviderRequest request = provider.Requests
            .Where(candidate => candidate.Body.Contains(
                Uri.EscapeDataString(phoneNumber), StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        return request.Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => WebUtility.UrlDecode(parts[0]),
                parts => parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "",
                StringComparer.Ordinal);
    }
}
