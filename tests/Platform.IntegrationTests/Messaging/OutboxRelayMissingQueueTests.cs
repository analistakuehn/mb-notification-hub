using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Messaging;

[Collection(OutboxRelayCollectionDefinition.Name)]
public sealed class OutboxRelayMissingQueueTests(OutboxRelayFixture fixture)
{
    private const string MissingDestination = "core-missing";

    private static readonly TimeSpan ReceiveBudget = TimeSpan.FromSeconds(15);

    [RequiresDockerFact]
    public async Task A_missing_queue_keeps_rows_pending_degrades_health_and_never_creates_the_queue()
    {
        await using ServiceProvider provider = fixture.BuildRelayProvider();
        Guid missingId = await fixture.AppendOutboxAsync(
            provider, OutboxEnvelopes.Envelope(MissingDestination, "operational"));
        Guid healthyId = await fixture.AppendOutboxAsync(
            provider, OutboxEnvelopes.Envelope(OutboxRelayFixture.OperationalQueue, "operational"));
        try
        {
            OutboxRelayPassResult first = await OutboxRelayFixture.RunRelayPassAsync(provider);

            // Failure is isolated per destination: the healthy row of the same
            // band publishes, the orphan stays pending, nothing is deleted.
            first.Published.ShouldBe(1);
            first.Failed.ShouldBe(1);
            (await OutboxRelayFixture.SentAtAsync(provider, missingId)).ShouldBeNull();
            (await OutboxRelayFixture.SentAtAsync(provider, healthyId)).ShouldNotBeNull();
            (await fixture.ReceiveAllAsync(OutboxRelayFixture.OperationalQueue, 1, ReceiveBudget))
                .ShouldHaveSingleItem();

            // A second pass retries and fails again: still pending, never lost.
            OutboxRelayPassResult second = await OutboxRelayFixture.RunRelayPassAsync(provider);
            second.Failed.ShouldBe(1);
            (await OutboxRelayFixture.SentAtAsync(provider, missingId)).ShouldBeNull();

            // The relay never created the queue, in any pass.
            ListQueuesResponse queues = await fixture.Sqs.ListQueuesAsync(new ListQueuesRequest());
            (queues.QueueUrls ?? []).ShouldAllBe(url => !url.Contains(MissingDestination, StringComparison.Ordinal));

            // Health degrades and exposes the pending backlog per destination.
            HealthReport report = await provider
                .GetRequiredService<HealthCheckService>()
                .CheckHealthAsync();
            HealthReportEntry entry = report.Entries["outbox-relay"];
            entry.Status.ShouldBe(HealthStatus.Degraded);
            entry.Description.ShouldNotBeNull();
            entry.Description.ShouldContain(MissingDestination);
            entry.Data.Keys.ShouldContain($"pending-count:{MissingDestination}");
            entry.Data.Keys.ShouldContain($"pending-oldest-age-seconds:{MissingDestination}");
        }
        finally
        {
            // The orphan row would otherwise fail every later test's passes.
            await OutboxRelayFixture.DeleteOutboxRowsAsync(provider, [missingId]);
        }
    }
}
