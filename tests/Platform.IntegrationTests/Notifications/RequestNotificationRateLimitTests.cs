using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class RequestNotificationRateLimitTests(NotificationsApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Exceeding_the_recipient_limit_answers_429_with_retry_after_and_audits_the_rejection()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Modules:Notifications:RateLimits:PerRecipient:transactional:0:PermitLimit"] = "2",
                    ["Modules:Notifications:RateLimits:PerRecipient:transactional:0:WindowSeconds"] = "60",
                })));
        HttpClient producer = fixture.CreateProducerClient(
            host, "producer-recipient-limit", NotificationsApi.SendTransactional);
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var rejectedKey = $"rrl-3-{Guid.NewGuid():N}";

        (await NotificationsApi.PostNotificationAsync(
                producer,
                NotificationsApi.RequestBody(templateKey, recipientId: recipientId, correlationId: "rrl-1"),
                $"rrl-1-{Guid.NewGuid():N}"))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await NotificationsApi.PostNotificationAsync(
                producer,
                NotificationsApi.RequestBody(templateKey, recipientId: recipientId, correlationId: "rrl-2"),
                $"rrl-2-{Guid.NewGuid():N}"))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        HttpResponseMessage limited = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId, correlationId: "rrl-3"),
            rejectedKey);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        var retryAfter = int.Parse(limited.Headers.RetryAfter!.ToString(), CultureInfo.InvariantCulture);
        retryAfter.ShouldBeInRange(1, 60);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(limited);
        problem.GetProperty("type").GetString().ShouldBe("rate-limit-exceeded");

        var entityId = $"{NotificationsApi.Application}:{rejectedKey}";
        List<AuditEvent> rejections = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(candidate => candidate.Action == "notification.rejected_at_ingress"
                && candidate.EntityId == entityId)
            .ToListAsync());
        rejections.Count.ShouldBe(1);
        rejections[0].DetailsJson.ShouldContain("recipient-rate-limited");
    }

    [RequiresDockerFact]
    public async Task Exceeding_the_principal_limit_answers_429()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Modules:Notifications:RateLimits:PerPrincipal:transactional:PermitLimit"] = "1",
                    ["Modules:Notifications:RateLimits:PerPrincipal:transactional:WindowSeconds"] = "60",
                })));
        HttpClient producer = fixture.CreateProducerClient(
            host, "producer-principal-limit", NotificationsApi.SendTransactional);

        (await NotificationsApi.PostNotificationAsync(
                producer,
                NotificationsApi.RequestBody(templateKey, recipientId: $"cus_{Guid.NewGuid():N}"),
                $"prl-1-{Guid.NewGuid():N}"))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        HttpResponseMessage limited = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: $"cus_{Guid.NewGuid():N}"),
            $"prl-2-{Guid.NewGuid():N}");

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }

    [RequiresDockerFact]
    public async Task An_unreachable_redis_fails_open_with_an_alarm_and_the_request_is_accepted()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        var capturedLogs = new CapturedLogProvider();
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Modules:Notifications:Redis:ConnectionString"] =
                        "127.0.0.1:1,connectTimeout=200,connectRetry=0",
                }));
            builder.ConfigureLogging(logging => logging.AddProvider(capturedLogs));
        });
        HttpClient producer = fixture.CreateProducerClient(
            host, "producer-failopen", NotificationsApi.SendTransactional);
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId),
            $"failopen-{Guid.NewGuid():N}");

        // Availability prevails: the request is accepted and fully persisted
        // even with every Redis-backed control unreachable.
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var persisted = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.RecipientId == recipientId));
        persisted.ShouldBe(1);

        // The fail-open is alarmed, never silent: the idempotency fast path
        // and the rate limiter both reported the store as unavailable.
        capturedLogs.EventIds.ShouldContain(7020);
        capturedLogs.EventIds.ShouldContain(7010);
    }

    private sealed class CapturedLogProvider : ILoggerProvider
    {
        private readonly ConcurrentBag<int> _eventIds = [];

        public IReadOnlyCollection<int> EventIds => _eventIds;

        public ILogger CreateLogger(string categoryName) => new CapturedLogger(_eventIds);

        public void Dispose()
        {
        }

        private sealed class CapturedLogger(ConcurrentBag<int> eventIds) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => eventIds.Add(eventId.Id);
        }
    }
}
