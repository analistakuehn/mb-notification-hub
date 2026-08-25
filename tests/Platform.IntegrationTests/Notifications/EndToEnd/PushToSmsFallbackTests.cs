using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.Notifications.DeliveryTracking;
using NotificationHub.IntegrationTests.Notifications.Scheduling;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.EndToEnd;

/// <summary>
/// The whole path a one-time code takes when the first channel goes quiet:
/// accepted, routed to push, accepted by the provider with no delivery event,
/// the step's deadline claimed by the scheduler, the next step of the published
/// plan queued as SMS, sent, and the notification closed by the provider's own
/// callback.
/// <para>
/// Every stage of this path already has a test of its own, and none of them can
/// answer the question this one asks. The pieces agree in isolation and still
/// leave the person without a code if any joint between them is loose: a
/// trigger addressed to a queue nobody drains, a deadline nobody reaches, a
/// callback that finds no attempt. What is under test here is the joints.
/// </para>
/// </summary>
[Collection(EndToEndPipelineCollectionDefinition.Name)]
public sealed class PushToSmsFallbackTests(EndToEndPipelineFixture fixture)
{
    private const string FcmAccepted = """{"name":"projects/test-project/messages/0:1"}""";
    private const string TwilioMessagesPath = "/Messages.json";
    private const string CallbackBase = "https://hooks.example.com/webhooks/twilio";

    /// <summary>
    /// How far past the acceptance the scheduler's clock stands. Wider than the
    /// thirty seconds the published plan gives the push step, so the deadline is
    /// unquestionably behind it, and still narrow enough to be a plausible
    /// round of a scan whose interval is measured in seconds.
    /// </summary>
    private static readonly TimeSpan PastTheDeadline = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Validity of the notification in the expiry scenario, shorter than the
    /// distance the clock moves, so the code is worthless by the time the
    /// fallback is asked for.
    /// </summary>
    private const int ExpiringTtlSeconds = 40;

    [RequiresDockerFact]
    public async Task An_accepted_push_with_no_delivery_event_falls_back_to_sms_and_the_callback_closes_it()
    {
        await using FakeProviderServer provider = await StartProviderAsync();
        Scenario scenario = await AcceptOverPushAsync(provider, ttlSeconds: 300);

        // Accepted by the provider and nothing more: the acceptance of a step
        // that has a later one is not a delivery, and the whole scenario rests
        // on the notification still being open at this point.
        NotificationAttempt push = (await AttemptsAsync(scenario.NotificationId)).ShouldHaveSingleItem();
        push.Channel.ShouldBe("push");
        push.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        push.DeliveredAt.ShouldBeNull();
        (await StatusOfAsync(scenario.NotificationId)).ShouldBe(NotificationStatuses.Dispatched);

        var clock = new MutableClock(DateTimeOffset.UtcNow + PastTheDeadline);
        await RunOverdueScanAsync(clock, expectedClaims: 1);
        await CarryTheFallbackToTheProviderAsync(provider, clock);

        // First, because it is the harm: without a second channel reaching the
        // provider the person never receives the code, and every row this test
        // could read afterwards is a detail of a delivery that did not happen.
        FakeProviderRequest smsRequest = SmsRequests(provider).ShouldHaveSingleItem(
            "o prazo do passo de push venceu sem evento de entrega e nenhum SMS saiu: "
            + "o código nunca chega ao destinatário.");

        NotificationAttempt sms = (await AttemptsAsync(scenario.NotificationId))
            .Single(attempt => attempt.Channel == "sms");
        sms.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        sms.ProviderKey.ShouldBe("twilio");
        sms.ProviderMessageId.ShouldNotBeNull();

        // The callback goes to the address the hub itself handed the provider,
        // carrying the correlation that address carries. Rebuilding the query
        // here from what the test already knows would prove that this test can
        // address the route, not that the hub can.
        await DeliverAsync(smsRequest, sms.ProviderMessageId!);

        (await StatusOfAsync(scenario.NotificationId)).ShouldBe(
            NotificationStatuses.Delivered,
            "a confirmação do provedor sobre o passo de SMS é o que encerra a notificação.");

        IReadOnlyList<NotificationAttempt> attempts = await AttemptsAsync(scenario.NotificationId);
        attempts.Count.ShouldBe(2, "o plano tem dois passos e cada um vale uma tentativa.");
        attempts.Single(attempt => attempt.Channel == "sms").Status
            .ShouldBe(NotificationAttemptStatuses.Delivered);
        attempts.Single(attempt => attempt.Channel == "push").Status.ShouldBe(
            NotificationAttemptStatuses.Sent,
            "o push nunca foi confirmado; encerrar a notificação não reescreve a tentativa "
            + "que ficou sem resposta.");
        (await CountTrailAsync(scenario.NotificationId, "fallback.attempt_queued")).ShouldBe(1);
        (await CountTrailAsync(scenario.NotificationId, "notification.delivered")).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_validity_that_ended_before_the_fallback_expires_the_notification_and_costs_no_sms()
    {
        await using FakeProviderServer provider = await StartProviderAsync();
        Scenario scenario = await AcceptOverPushAsync(provider, ExpiringTtlSeconds);

        // The clock stands past both instants the two decisions read: the
        // deadline of the push step, which is what the scan claims, and the
        // validity of the notification, which is what the handler weighs before
        // spending anything on the next step.
        var clock = new MutableClock(DateTimeOffset.UtcNow + PastTheDeadline);
        await RunOverdueScanAsync(clock, expectedClaims: 1);
        await CarryTheFallbackToTheProviderAsync(provider, clock);

        // The evidence is the count at the provider, not a flag inside the hub:
        // an expired code that still costs a message is money spent on a
        // delivery that could not be used.
        SmsRequests(provider).Count.ShouldBe(
            0,
            "a validade já tinha vencido quando o fallback foi pedido, e mesmo assim "
            + "um SMS foi enviado.");

        (await StatusOfAsync(scenario.NotificationId)).ShouldBe(NotificationStatuses.Expired);
        (await AttemptsAsync(scenario.NotificationId)).ShouldHaveSingleItem()
            .Channel.ShouldBe("push");
        (await CountTrailAsync(scenario.NotificationId, "notification.expired")).ShouldBe(1);
        (await CountTrailAsync(scenario.NotificationId, "fallback.attempt_queued")).ShouldBe(0);
    }

    /// <summary>
    /// The provider double every channel of this suite talks to: the token
    /// endpoint of the push provider, the send endpoint of the push provider,
    /// and the message endpoint of the SMS provider.
    /// </summary>
    private static async Task<FakeProviderServer> StartProviderAsync()
    {
        FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(
            request.Path == DispatchApi.FcmTokenPath
                ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
                : request.Path.EndsWith(TwilioMessagesPath, StringComparison.Ordinal)
                    ? new FakeProviderResponse(
                        201, $$"""{"sid":"SM{{Guid.NewGuid():N}}"}""", null)
                    : new FakeProviderResponse(200, FcmAccepted, null));
        return provider;
    }

    /// <summary>
    /// Publishes the governed catalog of one one-time code, accepts it and
    /// walks it to a push accepted by the provider: template with push and SMS
    /// content, policy whose plan gives push thirty seconds and then falls to
    /// SMS, recipient reachable both ways.
    /// </summary>
    private async Task<Scenario> AcceptOverPushAsync(FakeProviderServer provider, int ttlSeconds)
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, NotificationClasses.Critical, "authentication");
        await CorePipelineApi.CreatePublishedPolicyAsync(
            fixture, application, NotificationClasses.Critical);
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("push", "fcm"), ("sms", "twilio"));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture,
            application,
            templateKey,
            NotificationClasses.Critical,
            recipientId,
            "core-auth",
            ttlSeconds);

        await using ServiceProvider dispatcher = BuildDispatcher(provider);
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        return new Scenario(notificationId, recipientId);
    }

    /// <summary>
    /// One round of the deadline scan through the composed delivery-tracker
    /// role, on a clock standing past the deadline.
    /// <para>
    /// The claim count is asserted exactly, and it is the isolation of this
    /// environment that makes that legitimate: these stores hold only what
    /// these suites wrote, so a round that claims anything else is a round
    /// reaching rows it has no business in.
    /// </para>
    /// </summary>
    private async Task RunOverdueScanAsync(MutableClock clock, int expectedClaims)
    {
        await using ServiceProvider tracker = fixture.BuildDeliveryTrackerProvider(
            replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        using IServiceScope scope = tracker.CreateScope();
        OverdueFallbackScanResult result = await scope.ServiceProvider
            .GetRequiredService<OverdueFallbackScan>()
            .RunAsync(CancellationToken.None);
        result.DeadlineRequested.ShouldBe(
            expectedClaims,
            "a varredura por prazo precisa reivindicar exatamente a tentativa vencida deste cenário.");
    }

    /// <summary>
    /// Carries whatever the scan asked for through the relay, the Core and the
    /// SMS dispatcher, on the same clock the scan used, because the handler
    /// weighs the validity of the notification against it.
    /// <para>
    /// No disposition of these passes is asserted. A run in which the scheduler
    /// never asked settles every one of them as an empty pass, which is a
    /// legitimate disposition, and a counter here would stop the test one line
    /// before the assertion that says what actually went wrong.
    /// </para>
    /// </summary>
    private async Task CarryTheFallbackToTheProviderAsync(
        FakeProviderServer provider,
        MutableClock clock)
    {
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);

        await using ServiceProvider core = fixture.BuildCoreWorkerProvider(
            replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        await CorePipelineFixture.RunCorePassAsync(core, "core-auth");
        await CorePipelineFixture.RunRelayPassAsync(relay);

        await using ServiceProvider dispatcher = BuildDispatcher(provider);
        await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-sms-auth");
    }

    /// <summary>The dispatcher role with every provider pointed at the double.</summary>
    private ServiceProvider BuildDispatcher(FakeProviderServer provider)
        => fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(
                provider.BaseAddress,
                provider.BaseAddress,
                twilioBase: provider.BaseAddress,
                statusCallbackUrl: CallbackBase));

    /// <summary>
    /// Delivers the provider's confirmation back to the hub over the callback
    /// address the send carried, signed with the token this host verifies, and
    /// applies it through the relay and the delivery-tracker consumer exactly
    /// as the deployed roles would.
    /// </summary>
    private async Task DeliverAsync(FakeProviderRequest smsRequest, string providerMessageId)
    {
        var callbackUrl = new Uri(ParseForm(smsRequest)["StatusCallback"]);
        HttpResponseMessage accepted = await fixture.CreateClient().SendAsync(
            DeliveryTrackingApi.TwilioCallback(
                [
                    new KeyValuePair<string, string>("MessageSid", providerMessageId),
                    new KeyValuePair<string, string>("MessageStatus", "delivered"),
                ],
                callbackUrl.Query,
                authToken: EndToEndPipelineFixture.TwilioAuthToken));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await using ServiceProvider tracker = fixture.BuildDeliveryTrackerProvider();
        for (var pass = 0; pass < 3; pass++)
        {
            await CorePipelineFixture.RunRelayPassAsync(relay);
            await CorePipelineFixture.RunDeliveryEventPassAsync(tracker);
        }
    }

    /// <summary>Calls the SMS provider received, told apart from the push ones by their endpoint.</summary>
    private static List<FakeProviderRequest> SmsRequests(FakeProviderServer provider)
        => [.. provider.Requests.Where(request =>
            request.Path.EndsWith(TwilioMessagesPath, StringComparison.Ordinal))];

    private static Dictionary<string, string> ParseForm(FakeProviderRequest request)
        => request.Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => WebUtility.UrlDecode(parts[0]),
                parts => parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "",
                StringComparer.Ordinal);

    private async Task<IReadOnlyList<NotificationAttempt>> AttemptsAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.NotificationId == notificationId)
            .OrderBy(attempt => attempt.Sequence)
            .ToListAsync());

    private async Task<string> StatusOfAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.Status)
            .SingleAsync());

    private async Task<int> CountTrailAsync(Guid notificationId, string action)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == action
                && entry.EntityId == notificationId.ToString()));

    private sealed record Scenario(Guid NotificationId, string RecipientId);
}
