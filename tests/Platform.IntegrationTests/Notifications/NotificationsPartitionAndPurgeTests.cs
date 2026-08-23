using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;
using NotificationHub.IntegrationTests.TemplateManagement;
using StackExchange.Redis;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class NotificationsPartitionAndPurgeTests(NotificationsApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_notification_table_is_born_partitioned_with_the_initial_monthly_partitions()
    {
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::int FROM pg_inherits WHERE inhparent = 'notifications.notification'::regclass",
            connection);

        var partitions = (int)(await command.ExecuteScalarAsync())!;

        // The creation migration provisions the current month plus two.
        partitions.ShouldBeGreaterThanOrEqualTo(3);
    }

    [RequiresDockerFact]
    public async Task The_partition_coverage_health_check_reports_the_notifications_entry_healthy()
    {
        HealthReport report = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync());

        report.Entries.ContainsKey("notifications-partitions").ShouldBeTrue();
        report.Entries["notifications-partitions"].Status.ShouldBe(HealthStatus.Healthy);
    }

    [RequiresDockerFact]
    public async Task A_registration_past_the_window_is_purged_and_a_later_replay_creates_a_new_notification()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-purge", NotificationsApi.SendTransactional);
        var idempotencyKey = $"purge-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var body = NotificationsApi.RequestBody(templateKey, recipientId: recipientId);

        HttpResponseMessage first = await NotificationsApi.PostNotificationAsync(producer, body, idempotencyKey);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var firstId = (await NotificationsApi.ReadJsonAsync(first)).GetProperty("notificationId").GetString();

        // Age the registration past the 24 h window and forget the fast path,
        // exactly what the passage of a day does.
        await fixture.ExecuteNotificationsDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""
             UPDATE notifications.idempotency_key
             SET created_at = created_at - interval '25 hours'
             WHERE idempotency_key = {idempotencyKey}
             """));
        await RemoveFastPathEntryAsync(idempotencyKey);

        var removed = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IdempotencyPurge>()
            .RunAsync(CancellationToken.None));
        removed.ShouldBeGreaterThanOrEqualTo(1);
        var remaining = await fixture.QueryNotificationsDbAsync(db => db.IdempotencyRegistrations
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey));
        remaining.ShouldBe(0);

        // Beyond the idempotency window the contract is a new notification.
        HttpResponseMessage replay = await NotificationsApi.PostNotificationAsync(producer, body, idempotencyKey);
        replay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var replayId = (await NotificationsApi.ReadJsonAsync(replay)).GetProperty("notificationId").GetString();
        replayId.ShouldNotBe(firstId);
        var notifications = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey));
        notifications.ShouldBe(2);
    }

    private async Task RemoveFastPathEntryAsync(string idempotencyKey)
    {
        ConfigurationOptions options = ConfigurationOptions.Parse(fixture.RedisConnectionString);
        options.AbortOnConnectFail = false;
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(options);
        await connection.GetDatabase().KeyDeleteAsync(
            $"{NotificationsApiFixture.RedisKeyPrefix}idem:{NotificationsApi.Application}:{idempotencyKey}");
    }
}
