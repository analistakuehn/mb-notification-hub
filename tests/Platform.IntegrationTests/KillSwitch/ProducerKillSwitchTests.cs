using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.KillSwitch;

[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class ProducerKillSwitchTests(NotificationsApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_blocked_known_producer_receives_producer_disabled_without_acceptance()
    {
        var producerId = $"producer-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        HttpClient admin = fixture.CreatePlatformAdminClient("platform-admin");
        HttpResponseMessage activated = await admin.PutAsJsonAsync(
            $"/v1/notifications/kill-switch/producer/{producerId}",
            new { active = true });
        activated.EnsureSuccessStatusCode();
        HttpClient producer = fixture.CreateProducerClient(
            producerId,
            NotificationsApi.SendTransactional);

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody("template-is-never-read", recipientId: recipientId),
            $"blocked-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        Dictionary<string, object?>? problem =
            await response.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
        problem!["type"]!.ToString().ShouldBe("producer-disabled");
        var accepted = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.RequestedBy == producerId));
        accepted.ShouldBe(0);
        (await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId
                && message.EventType == "notification.accepted")))
            .ShouldBe(0);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "notification.accepted"
                && audit.ActorId == producerId)))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_cold_postgres_failure_returns_service_unavailable_without_acceptance()
    {
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IKillSwitchSnapshotSource>();
                services.AddSingleton<IKillSwitchSnapshotSource, ThrowingSnapshotSource>();
            }));
        var producerId = $"producer-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        HttpClient producer = fixture.CreateProducerClient(
            host,
            producerId,
            NotificationsApi.SendTransactional);

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody("template-is-never-read", recipientId: recipientId),
            $"unavailable-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var accepted = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.RequestedBy == producerId));
        accepted.ShouldBe(0);
        (await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId
                && message.EventType == "notification.accepted")))
            .ShouldBe(0);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "notification.accepted"
                && audit.ActorId == producerId)))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_safe_replay_bypasses_a_newly_active_producer_switch()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        var producerId = $"producer-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = $"replay-{Guid.NewGuid():N}";
        var body = NotificationsApi.RequestBody(templateKey, recipientId: recipientId);
        HttpClient producer = fixture.CreateProducerClient(
            producerId,
            NotificationsApi.SendTransactional);

        HttpResponseMessage first = await NotificationsApi.PostNotificationAsync(
            producer,
            body,
            idempotencyKey);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var notificationId = (await NotificationsApi.ReadJsonAsync(first))
            .GetProperty("notificationId")
            .GetString();

        HttpClient admin = fixture.CreatePlatformAdminClient("platform-admin");
        (await admin.PutAsJsonAsync(
                $"/v1/notifications/kill-switch/producer/{producerId}",
                new { active = true }))
            .EnsureSuccessStatusCode();

        HttpResponseMessage replay = await NotificationsApi.PostNotificationAsync(
            producer,
            body,
            idempotencyKey);

        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NotificationsApi.ReadJsonAsync(replay))
            .GetProperty("notificationId")
            .GetString()
            .ShouldBe(notificationId);
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.IdempotencyKey == idempotencyKey)))
            .ShouldBe(1);
        (await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId
                && message.EventType == "notification.accepted")))
            .ShouldBe(1);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "notification.accepted"
                && audit.ActorId == producerId)))
            .ShouldBe(1);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "notification.duplicate"
                && audit.ActorId == producerId)))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_malformed_request_is_rejected_before_the_producer_switch_is_consulted()
    {
        var producerId = $"producer-{Guid.NewGuid():N}";
        HttpClient admin = fixture.CreatePlatformAdminClient("platform-admin");
        (await admin.PutAsJsonAsync(
                $"/v1/notifications/kill-switch/producer/{producerId}",
                new { active = true }))
            .EnsureSuccessStatusCode();
        HttpClient producer = fixture.CreateProducerClient(
            producerId,
            NotificationsApi.SendTransactional);
        var idempotencyKey = $"malformed-{Guid.NewGuid():N}";

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody("template-is-never-read", @class: "banana", ttlSeconds: 0),
            idempotencyKey);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("payload-invalid");
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "notification.rejected_at_ingress"
                && audit.EntityId == $"{NotificationsApi.Application}:{idempotencyKey}")))
            .ShouldBe(1);
    }

    private sealed class ThrowingSnapshotSource : IKillSwitchSnapshotSource
    {
        public Task<IReadOnlySet<KillSwitchAddress>> LoadActiveAsync(
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromException<IReadOnlySet<KillSwitchAddress>>(
                new InvalidOperationException("postgres unavailable"));
        }
    }
}
