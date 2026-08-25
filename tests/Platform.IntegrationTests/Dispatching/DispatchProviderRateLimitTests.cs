using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// The contracted rate of one provider, measured against a real Redis and a
/// clock the test moves. The bucket is shared by every instance of the fleet,
/// so the store is the subject here and no in-process double would prove
/// anything about it.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class DispatchProviderRateLimitTests(CorePipelineFixture fixture)
{
    private const string Accepted = """{"sid":"SM-rate-limit"}""";

    /// <summary>An endpoint nobody listens on, tuned to answer per operation instead of aborting.</summary>
    private const string UnreachableRedis =
        "127.0.0.1:1,connectTimeout=250,connectRetry=0,abortConnect=false,syncTimeout=250";

    [RequiresDockerFact]
    public async Task The_bucket_holds_one_second_of_the_contracted_rate_and_refills_by_the_clock()
    {
        await using RateLimitScenario scenario = await PrepareAsync(notifications: 3);
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using ServiceProvider dispatcher = BuildDispatcher(
            scenario, permitsPerSecond: 2, clock: clock);

        MessageDisposition first = await ProcessAsync(dispatcher, scenario.Dispatches[0]);
        MessageDisposition second = await ProcessAsync(dispatcher, scenario.Dispatches[1]);
        MessageDisposition third = await ProcessAsync(dispatcher, scenario.Dispatches[2]);

        // The refusal is the claim: a burst of one second is two sends here,
        // and the third one of the same second must not reach the provider.
        MessageDisposition.Postponed postponed = third.ShouldBeOfType<MessageDisposition.Postponed>(
            "o terceiro envio do mesmo segundo passou pelo limite contratado do provedor.");
        postponed.Reason.ShouldBe(DispatchMessageProcessor.ReasonRateLimited);
        postponed.Delay.ShouldNotBeNull();
        postponed.Delay!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
        RequestCount(scenario).ShouldBe(2);

        first.ShouldBeOfType<MessageDisposition.Processed>();
        second.ShouldBeOfType<MessageDisposition.Processed>();
        await AssertStatusAsync(scenario.Dispatches[0], NotificationAttemptStatuses.Sent);
        await AssertStatusAsync(scenario.Dispatches[1], NotificationAttemptStatuses.Sent);

        // Held back, never failed: the plan must not advance over congestion
        // of this hub's own making.
        await AssertStatusAsync(scenario.Dispatches[2], NotificationAttemptStatuses.Queued);

        // One second later the bucket is full again, which is what makes the
        // refusal a rate and not a quota.
        clock.Advance(TimeSpan.FromSeconds(1));
        (await ProcessAsync(dispatcher, scenario.Dispatches[2]))
            .ShouldBeOfType<MessageDisposition.Processed>();
        RequestCount(scenario).ShouldBe(3);
        await AssertStatusAsync(scenario.Dispatches[2], NotificationAttemptStatuses.Sent);
    }

    [RequiresDockerFact]
    public async Task A_send_whose_validity_ran_out_spends_no_budget()
    {
        await using RateLimitScenario scenario = await PrepareAsync(notifications: 2);
        await ExpireAsync(scenario.Dispatches[0].NotificationId);
        await using ServiceProvider dispatcher = BuildDispatcher(scenario, permitsPerSecond: 1);

        MessageDisposition expired = await ProcessAsync(dispatcher, scenario.Dispatches[0]);
        MessageDisposition valid = await ProcessAsync(dispatcher, scenario.Dispatches[1]);

        // The budget of this second is exactly one send. The expired message
        // was settled before the limiter, so the token was still there for the
        // message that could still be delivered: had the order been the other
        // way round, this send would come back postponed and the person would
        // wait for a queue the hub itself filled.
        valid.ShouldBeOfType<MessageDisposition.Processed>(
            "a validade vencida gastou o orçamento do provedor e barrou a mensagem seguinte, "
            + "que ainda valia.");
        await AssertStatusAsync(scenario.Dispatches[1], NotificationAttemptStatuses.Sent);
        RequestCount(scenario).ShouldBe(1);

        expired.ShouldBeOfType<MessageDisposition.Processed>();
        NotificationAttempt attempt = await AttemptAsync(scenario.Dispatches[0]);
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Failed);
        attempt.ErrorCode.ShouldBe(DispatchMessageProcessor.ErrorNotificationExpired);
    }

    [RequiresDockerFact]
    public async Task An_unreachable_bucket_lets_the_send_through_and_raises_the_alarm()
    {
        await using RateLimitScenario scenario = await PrepareAsync(notifications: 2);
        var logs = new CapturingLoggerProvider();
        await using ServiceProvider dispatcher = BuildDispatcher(
            scenario, permitsPerSecond: 1, redisConnectionString: UnreachableRedis, logs: logs);

        MessageDisposition first = await ProcessAsync(dispatcher, scenario.Dispatches[0]);
        MessageDisposition second = await ProcessAsync(dispatcher, scenario.Dispatches[1]);

        // One permit per second, two sends in the same second: with a reachable
        // store the second one is refused. Fail open means the control steps
        // aside, because a store nobody can reach must not stop a channel for
        // a reason the provider never gave.
        first.ShouldBeOfType<MessageDisposition.Processed>();
        second.ShouldBeOfType<MessageDisposition.Processed>();
        RequestCount(scenario).ShouldBe(2);

        logs.Lines.ShouldContain(
            line => line.Contains("limite de taxa por provedor está indisponível", StringComparison.Ordinal)
                && line.Contains("fail-open", StringComparison.Ordinal),
            "o fail-open passou em silêncio: sem alarme, ninguém sabe que o controle parou de medir.");
    }

    /// <summary>
    /// One application whose SMS notifications are queued and waiting on their
    /// dispatch queue, sharing one recipient number so what reached the
    /// provider can be counted apart from a neighbour's traffic.
    /// </summary>
    private async Task<RateLimitScenario> PrepareAsync(int notifications)
    {
        var application = DispatchApi.NewApplication();
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("sms", null));
        (var recipientId, var phoneNumber) = await DispatchApi.RegisterSmsRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("sms", "twilio"));

        FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(201, Accepted, null));

        List<QueuedDispatch> dispatches = [];
        for (var index = 0; index < notifications; index++)
        {
            (var templateKey, _) = await DispatchApi.CreatePublishedSmsTemplateAsync(
                fixture, application, "transactional", $"rate-limit-{index}");
            Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
                fixture, application, templateKey, "transactional", recipientId, "core-transactional");
            Guid attemptId = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
                .AsNoTracking()
                .Where(candidate => candidate.NotificationId == notificationId)
                .Select(candidate => candidate.Id)
                .SingleAsync());
            dispatches.Add(new QueuedDispatch(notificationId, attemptId));
        }

        return new RateLimitScenario(provider, phoneNumber, dispatches);
    }

    private ServiceProvider BuildDispatcher(
        RateLimitScenario scenario,
        int permitsPerSecond,
        string? redisConnectionString = null,
        MutableTimeProvider? clock = null,
        CapturingLoggerProvider? logs = null)
    {
        Dictionary<string, string?> settings = DispatchApi.ProviderSettings(
            scenario.Provider.BaseAddress,
            scenario.Provider.BaseAddress,
            twilioBase: scenario.Provider.BaseAddress);
        settings["Modules:Dispatch:RateLimits:PerProvider:twilio:PermitsPerSecond"] =
            permitsPerSecond.ToString(CultureInfo.InvariantCulture);

        // A bucket of its own per test: the collection shares one Redis, and a
        // key shared with a neighbour would make this measurement a coin toss.
        settings["Modules:Dispatch:RateLimits:KeyPrefix"] = $"it-rate-{Guid.NewGuid():N}:";
        if (redisConnectionString is not null)
        {
            settings["Modules:Dispatch:RateLimits:RedisConnectionString"] = redisConnectionString;
        }

        return fixture.BuildDispatcherWorkerProvider(
            settings,
            logs,
            clock is null ? null : services => services.AddSingleton<TimeProvider>(clock));
    }

    private static async Task<MessageDisposition> ProcessAsync(
        ServiceProvider dispatcher,
        QueuedDispatch dispatch)
    {
        await using AsyncServiceScope scope = dispatcher.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DispatchMessageProcessor>()
            .ProcessAsync(Envelope(dispatch), CancellationToken.None);
    }

    private static MessageEnvelope Envelope(QueuedDispatch dispatch)
        => new()
        {
            MessageId = Guid.CreateVersion7(),
            Type = DispatchMessages.AttemptQueuedType,
            SchemaVersion = DispatchMessages.SchemaVersion,
            SourceQueue = "dispatch-sms-transactional",
            Payload = JsonSerializer.SerializeToElement(new
            {
                notificationId = dispatch.NotificationId,
                attemptId = dispatch.AttemptId,
            }),
        };

    /// <summary>Requests carrying this scenario's number, apart from a neighbour's traffic.</summary>
    private static int RequestCount(RateLimitScenario scenario)
        => scenario.Provider.Requests.Count(request => request.Body.Contains(
            Uri.EscapeDataString(scenario.PhoneNumber), StringComparison.Ordinal));

    private async Task ExpireAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .Where(candidate => candidate.Id == notificationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                candidate => candidate.ExpiresAt,
                DateTimeOffset.UtcNow.AddMinutes(-1))));

    private async Task AssertStatusAsync(QueuedDispatch dispatch, string expected)
        => (await AttemptAsync(dispatch)).Status.ShouldBe(expected);

    private async Task<NotificationAttempt> AttemptAsync(QueuedDispatch dispatch)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == dispatch.AttemptId));

    private sealed record QueuedDispatch(Guid NotificationId, Guid AttemptId);

    private sealed record RateLimitScenario(
        FakeProviderServer Provider,
        string PhoneNumber,
        IReadOnlyList<QueuedDispatch> Dispatches) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
