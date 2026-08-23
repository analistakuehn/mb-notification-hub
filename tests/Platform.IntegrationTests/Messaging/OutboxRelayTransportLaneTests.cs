using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Messaging;

/// <summary>
/// The reason the claim became per transport lane. Draining a band as one unit
/// meant the first failing row stopped it, so a single unreachable bus
/// destination at the head of a band would hold back every internal queue row
/// of the same band, turning an integration outage into a delivery outage.
/// </summary>
[Collection(OutboxRelayCollectionDefinition.Name)]
public sealed class OutboxRelayTransportLaneTests(OutboxRelayFixture fixture)
{
    private const string BusTopic = "notifications.events.v1";

    private static readonly TimeSpan ReceiveBudget = TimeSpan.FromSeconds(15);

    /// <summary>A bootstrap address nothing answers on: the bus is composed and unreachable.</summary>
    private static readonly Dictionary<string, string?> UnreachableBus = new()
    {
        ["Platform:Messaging:Kafka:BootstrapServers"] = "127.0.0.1:9",
        ["Platform:Messaging:Kafka:DeliveryTimeoutMilliseconds"] = "2000",
        ["Platform:Messaging:Kafka:FlushTimeoutMilliseconds"] = "1000",
    };

    [RequiresDockerFact]
    public async Task An_unreachable_bus_does_not_hold_back_the_queue_rows_of_the_same_band()
    {
        await using ServiceProvider provider = fixture.BuildRelayProvider(UnreachableBus);

        // The bus row is the oldest of the band on purpose: under a per-band
        // drain it would be claimed first, fail, and stop the band.
        Guid busId = await fixture.AppendOutboxAsync(provider, BusEvent("operational"));
        Guid firstQueued = await fixture.AppendOutboxAsync(
            provider, OutboxEnvelopes.Envelope(OutboxRelayFixture.OperationalQueue, "operational"));
        Guid secondQueued = await fixture.AppendOutboxAsync(
            provider, OutboxEnvelopes.Envelope(OutboxRelayFixture.OperationalQueue, "operational"));

        try
        {
            OutboxRelayPassResult pass = await OutboxRelayFixture.RunRelayPassAsync(provider);

            pass.Published.ShouldBe(2);
            pass.Failed.ShouldBe(1);
            (await OutboxRelayFixture.SentAtAsync(provider, firstQueued)).ShouldNotBeNull();
            (await OutboxRelayFixture.SentAtAsync(provider, secondQueued)).ShouldNotBeNull();
            (await OutboxRelayFixture.SentAtAsync(provider, busId)).ShouldBeNull();
            (await fixture.ReceiveAllAsync(OutboxRelayFixture.OperationalQueue, 2, ReceiveBudget))
                .Count.ShouldBe(2);

            // The stuck lane is visible instead of silent: the backlog is
            // reported per transport, so nobody has to guess which side is behind.
            HealthReport report = await provider
                .GetRequiredService<HealthCheckService>()
                .CheckHealthAsync();
            report.Entries["outbox-relay"].Data.Keys
                .ShouldContain($"pending-count:{OutboxTransports.Kafka}:{BusTopic}");
        }
        finally
        {
            // The stuck row would otherwise fail every later pass of this collection.
            await OutboxRelayFixture.DeleteOutboxRowsAsync(provider, [busId]);
        }
    }

    [RequiresDockerFact]
    public async Task A_relay_without_a_composed_bus_leaves_the_bus_rows_pending_and_drains_the_queues()
    {
        // No Kafka configuration at all: the lane is not registered, so the
        // relay never claims it. Falsification of the test above, which needs
        // the lane composed to fail on the wire rather than by absence.
        await using ServiceProvider provider = fixture.BuildRelayProvider();
        Guid busId = await fixture.AppendOutboxAsync(provider, BusEvent("transactional"));
        Guid queued = await fixture.AppendOutboxAsync(
            provider, OutboxEnvelopes.Envelope(OutboxRelayFixture.TransactionalQueue, "transactional"));

        try
        {
            OutboxRelayPassResult pass = await OutboxRelayFixture.RunRelayPassAsync(provider);

            pass.Published.ShouldBe(1);
            pass.Failed.ShouldBe(0);
            (await OutboxRelayFixture.SentAtAsync(provider, queued)).ShouldNotBeNull();
            (await OutboxRelayFixture.SentAtAsync(provider, busId)).ShouldBeNull();
            (await fixture.ReceiveAllAsync(OutboxRelayFixture.TransactionalQueue, 1, ReceiveBudget))
                .ShouldHaveSingleItem();
        }
        finally
        {
            await OutboxRelayFixture.DeleteOutboxRowsAsync(provider, [busId]);
        }
    }

    private static OutboxAppend BusEvent(string priorityClass)
        => CloudEventOutbox.Build(new CloudEventAppend
        {
            Destination = BusTopic,
            Source = "urn:araia:notification-hub",
            Type = "araia.notification.rejected.v1",
            Subject = $"cus_{Guid.NewGuid():N}",
            Time = DateTimeOffset.UtcNow,
            PriorityClass = priorityClass,
            Data = JsonSerializer.SerializeToElement(new { reason = "no-consent" }),
        });
}
