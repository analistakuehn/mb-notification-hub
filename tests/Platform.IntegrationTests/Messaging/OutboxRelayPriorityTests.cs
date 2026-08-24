using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Messaging;

[Collection(OutboxRelayCollectionDefinition.Name)]
public sealed class OutboxRelayPriorityTests(OutboxRelayFixture fixture)
{
    private static readonly TimeSpan ReceiveBudget = TimeSpan.FromSeconds(15);

    [RequiresDockerFact]
    public async Task A_critical_instance_drains_auth_by_destination_and_leaves_the_lower_bands_pending()
    {
        await using ServiceProvider restricted = fixture.BuildRelayProvider(new Dictionary<string, string?>
        {
            ["Platform:Messaging:Relay:Bands:0"] = "auth",
            ["Platform:Messaging:Relay:Bands:1"] = "critical",
        });
        // The auth row carries a transactional stored class on purpose: the
        // reader classifies core-auth into the top band by destination alone.
        Guid authId = await fixture.AppendOutboxAsync(
            restricted, OutboxEnvelopes.Envelope(OutboxRelayFixture.AuthQueue, "transactional"));
        Guid criticalId = await fixture.AppendOutboxAsync(
            restricted, OutboxEnvelopes.Envelope(OutboxRelayFixture.CriticalQueue, "critical"));
        Guid transactionalId = await fixture.AppendOutboxAsync(
            restricted, OutboxEnvelopes.Envelope(OutboxRelayFixture.TransactionalQueue, "transactional"));
        Guid operationalId = await fixture.AppendOutboxAsync(
            restricted, OutboxEnvelopes.Envelope(OutboxRelayFixture.OperationalQueue, "operational"));

        OutboxRelayPassResult result = await OutboxRelayFixture.RunRelayPassAsync(restricted);

        result.Published.ShouldBe(2);
        result.Failed.ShouldBe(0);
        (await OutboxRelayFixture.SentAtAsync(restricted, authId)).ShouldNotBeNull();
        (await OutboxRelayFixture.SentAtAsync(restricted, criticalId)).ShouldNotBeNull();
        (await OutboxRelayFixture.SentAtAsync(restricted, transactionalId)).ShouldBeNull();
        (await OutboxRelayFixture.SentAtAsync(restricted, operationalId)).ShouldBeNull();
        (await fixture.ReceiveAllAsync(OutboxRelayFixture.AuthQueue, 1, ReceiveBudget))
            .ShouldHaveSingleItem();
        (await fixture.ReceiveAllAsync(OutboxRelayFixture.CriticalQueue, 1, ReceiveBudget))
            .ShouldHaveSingleItem();

        // A full instance then drains what the dedicated one left behind.
        await using ServiceProvider full = fixture.BuildRelayProvider();
        OutboxRelayPassResult drain = await OutboxRelayFixture.RunRelayPassAsync(full);

        drain.Published.ShouldBe(2);
        (await OutboxRelayFixture.SentAtAsync(full, transactionalId)).ShouldNotBeNull();
        (await OutboxRelayFixture.SentAtAsync(full, operationalId)).ShouldNotBeNull();
        (await fixture.ReceiveAllAsync(OutboxRelayFixture.TransactionalQueue, 1, ReceiveBudget))
            .ShouldHaveSingleItem();
        (await fixture.ReceiveAllAsync(OutboxRelayFixture.OperationalQueue, 1, ReceiveBudget))
            .ShouldHaveSingleItem();
    }

    [RequiresDockerFact]
    public async Task A_row_written_without_a_band_still_drains_in_the_band_of_its_destination()
    {
        await using ServiceProvider authOnly = fixture.BuildRelayProvider(new Dictionary<string, string?>
        {
            ["Platform:Messaging:Relay:Bands:0"] = "auth",
        });

        // The row arrives the way every producer wrote before the band column
        // existed, and with a stored class that is not the one the band would
        // suggest. Nothing in C# classified it; if the database had not, the
        // claim of the auth band would never see it and an authentication code
        // would sit pending behind ordinary traffic.
        Guid id = await fixture.AppendWithoutBandAsync(OutboxRelayFixture.AuthQueue, "transactional");

        OutboxRelayPassResult result = await OutboxRelayFixture.RunRelayPassAsync(authOnly);

        result.Published.ShouldBe(1);
        result.Failed.ShouldBe(0);
        (await OutboxRelayFixture.SentAtAsync(authOnly, id)).ShouldNotBeNull();
        List<Message> received = await fixture.ReceiveAllAsync(
            OutboxRelayFixture.AuthQueue, 1, ReceiveBudget);
        received.ShouldHaveSingleItem();
        received[0].Body.ShouldContain(id.ToString());
    }
}
