using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Messaging;

[Collection(OutboxRelayCollectionDefinition.Name)]
public sealed class OutboxRelayPublishTests(OutboxRelayFixture fixture)
{
    private static readonly TimeSpan ReceiveBudget = TimeSpan.FromSeconds(15);

    [RequiresDockerFact]
    public async Task Publishes_pending_rows_with_the_stored_payload_as_the_exact_message_body()
    {
        await using ServiceProvider provider = fixture.BuildRelayProvider();
        OutboxAppend criticalRow = OutboxEnvelopes.Envelope(OutboxRelayFixture.CriticalQueue, "critical");
        OutboxAppend transactionalRow = OutboxEnvelopes.Envelope(OutboxRelayFixture.TransactionalQueue, "transactional");
        Guid criticalId = await fixture.AppendOutboxAsync(provider, criticalRow);
        Guid transactionalId = await fixture.AppendOutboxAsync(provider, transactionalRow);

        OutboxRelayPassResult result = await OutboxRelayFixture.RunRelayPassAsync(provider);

        result.Published.ShouldBe(2);
        result.Failed.ShouldBe(0);

        // The body is the payload as the jsonb column stores it, byte for
        // byte: any re-wrapping or re-serialization by the relay would break
        // this equality, because jsonb formatting differs from the compact
        // JSON the producer wrote.
        List<Message> critical = await fixture.ReceiveAllAsync(OutboxRelayFixture.CriticalQueue, 1, ReceiveBudget);
        Message criticalMessage = critical.ShouldHaveSingleItem();
        criticalMessage.Body.ShouldBe(await OutboxRelayFixture.StoredPayloadTextAsync(provider, criticalId));
        criticalMessage.MessageAttributes["messageKey"].StringValue.ShouldBe(criticalRow.MessageKey);
        criticalMessage.MessageAttributes["eventType"].StringValue.ShouldBe(OutboxEnvelopes.EventType);
        criticalMessage.MessageAttributes["traceparent"].StringValue.ShouldBe(OutboxEnvelopes.Traceparent);

        List<Message> transactional =
            await fixture.ReceiveAllAsync(OutboxRelayFixture.TransactionalQueue, 1, ReceiveBudget);
        transactional.ShouldHaveSingleItem().Body
            .ShouldBe(await OutboxRelayFixture.StoredPayloadTextAsync(provider, transactionalId));

        (await OutboxRelayFixture.SentAtAsync(provider, criticalId)).ShouldNotBeNull();
        (await OutboxRelayFixture.SentAtAsync(provider, transactionalId)).ShouldNotBeNull();

        // Nothing left: a second pass finds no pending row to publish.
        OutboxRelayPassResult second = await OutboxRelayFixture.RunRelayPassAsync(provider);
        second.Published.ShouldBe(0);
        second.Failed.ShouldBe(0);
    }
}
