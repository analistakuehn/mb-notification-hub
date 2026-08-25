using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// What the remaining validity of a notification buys on the SMS channel: the
/// provider learns how long the message is still worth queueing, and a
/// notification whose validity already ended costs no message at all.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class DispatchSmsValidityTests(CorePipelineFixture fixture)
{
    private const string CallbackBase = "https://hooks.example.com/webhooks/twilio";

    [RequiresDockerFact]
    public async Task An_sms_still_valid_reaches_the_provider_with_the_time_it_has_left()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedSmsTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("sms", null));
        (var recipientId, var phoneNumber) = await DispatchApi.RegisterSmsRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("sms", "twilio"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            201, """{"sid":"SM-valid-1"}""", null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(
                provider.BaseAddress,
                provider.BaseAddress,
                twilioBase: provider.BaseAddress,
                statusCallbackUrl: CallbackBase));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-sms-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        NotificationAttempt attempt = await SingleAttemptAsync(notificationId);
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        attempt.ProviderKey.ShouldBe("twilio");
        attempt.ProviderMessageId.ShouldBe("SM-valid-1");

        // Scoped to this recipient's number: the collection is shared and a
        // pass may also drain attempts seeded by a neighbouring test.
        Dictionary<string, string> form = ParseForm(RequestsFor(provider, phoneNumber).ShouldHaveSingleItem());
        form["MessagingServiceSid"].ShouldBe("MG-test");
        form.ShouldNotContainKey("From");

        // The default TTL of the seeded policy is five minutes, and the send
        // happens inside it, so what the provider hears is a positive number
        // no larger than the whole window.
        var validity = int.Parse(form["ValidityPeriod"], CultureInfo.InvariantCulture);
        validity.ShouldBeGreaterThan(0);
        validity.ShouldBeLessThanOrEqualTo(300);

        var callback = new Uri(form["StatusCallback"]);
        callback.GetLeftPart(UriPartial.Path).ShouldBe(CallbackBase);
        callback.Query.ShouldContain($"notificationId={notificationId}");
        callback.Query.ShouldContain($"attemptId={attempt.Id}");
    }

    [RequiresDockerFact]
    public async Task An_sms_whose_validity_ran_out_costs_no_message()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedSmsTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("sms", null));
        (var recipientId, var phoneNumber) = await DispatchApi.RegisterSmsRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("sms", "twilio"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            201, """{"sid":"SM-expired-1"}""", null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        // The attempt is queued and healthy; what ends is the window in which
        // delivering it still means anything. Moved in the store rather than
        // waited out, so the proof does not depend on a clock racing the
        // containers.
        await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .Where(candidate => candidate.Id == notificationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                candidate => candidate.ExpiresAt,
                DateTimeOffset.UtcNow.AddMinutes(-1))));

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(
                provider.BaseAddress,
                provider.BaseAddress,
                twilioBase: provider.BaseAddress,
                statusCallbackUrl: CallbackBase));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-sms-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The evidence is the count at the provider, not a flag inside the
        // hub: no request carrying this number ever arrived.
        RequestsFor(provider, phoneNumber).Count.ShouldBe(0);

        NotificationAttempt attempt = await SingleAttemptAsync(notificationId);
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Failed);
        attempt.ErrorCode.ShouldBe("notification-expired");
        attempt.ProviderMessageId.ShouldBeNull();
        attempt.SentAt.ShouldBeNull();
    }

    private async Task<NotificationAttempt> SingleAttemptAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.NotificationId == notificationId));

    private static List<FakeProviderRequest> RequestsFor(FakeProviderServer provider, string phoneNumber)
        => [.. provider.Requests.Where(candidate =>
            candidate.Body.Contains(Uri.EscapeDataString(phoneNumber), StringComparison.Ordinal))];

    private static Dictionary<string, string> ParseForm(FakeProviderRequest request)
        => request.Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => WebUtility.UrlDecode(parts[0]),
                parts => parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "",
                StringComparer.Ordinal);
}
