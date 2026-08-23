using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class CorePipelineMaintenanceTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_pipeline_tables_are_born_partitioned_with_the_initial_monthly_partitions()
    {
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        foreach (var parent in new[] { "notification_attempt", "policy_evaluation" })
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*)::int FROM pg_inherits WHERE inhparent = to_regclass($1)",
                connection);
            command.Parameters.AddWithValue($"notifications.{parent}");

            var partitions = (int)(await command.ExecuteScalarAsync())!;

            // The creation migration provisions the current month plus two.
            partitions.ShouldBeGreaterThanOrEqualTo(3, parent);
        }
    }

    [RequiresDockerFact]
    public async Task The_partition_coverage_health_checks_cover_the_two_new_tables()
    {
        HealthReport report = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync());

        report.Entries.ContainsKey("notifications-attempt-partitions").ShouldBeTrue();
        report.Entries["notifications-attempt-partitions"].Status.ShouldBe(HealthStatus.Healthy);
        report.Entries.ContainsKey("notifications-policy-evaluation-partitions").ShouldBeTrue();
        report.Entries["notifications-policy-evaluation-partitions"].Status.ShouldBe(HealthStatus.Healthy);
    }

    [RequiresDockerFact]
    public async Task The_processed_messages_purge_removes_old_marks_and_keeps_recent_ones()
    {
        var oldMark = $"purge-old-{Guid.NewGuid():N}";
        var recentMark = $"purge-recent-{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(fixture.PostgresConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO platform.processed_messages (message_id, consumer, processed_at) VALUES
                    ($1, 'purge-test', now() - interval '16 days'),
                    ($2, 'purge-test', now())
                """,
                connection);
            insert.Parameters.AddWithValue(oldMark);
            insert.Parameters.AddWithValue(recentMark);
            await insert.ExecuteNonQueryAsync();
        }

        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        using IServiceScope scope = worker.CreateScope();
        var removed = await scope.ServiceProvider
            .GetRequiredService<ProcessedMessagePurge>()
            .RunAsync(CancellationToken.None);

        removed.ShouldBeGreaterThanOrEqualTo(1);
        List<string> survivors = await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .Where(mark => mark.Consumer == "purge-test")
            .Select(mark => mark.MessageId)
            .ToListAsync());
        survivors.ShouldBe([recentMark]);
    }
}
