using NotificationHub.Api.Infrastructure.Messaging.Consuming;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;

/// <summary>
/// Keeps the bus ingress from subscribing while the producer registry has
/// nothing to decide with.
///
/// An empty table is indistinguishable from a materialization job that never
/// ran, and treating it as "nobody is authorized" would be the worst possible
/// reading: with a day of retention on the entry topic, an out-of-order deploy
/// would send a full day of legitimate traffic to the dead-letter topic while
/// every probe reported success. Refusing to consume keeps the records where
/// they are and makes the outage visible as an outage.
/// </summary>
internal sealed class ProducerRegistryConsumerGate(IProducerRegistry registry) : IKafkaConsumerGate
{
    public async Task<KafkaGateDecision> EvaluateAsync(CancellationToken cancellationToken)
    {
        ProducerGrants? grants = await registry.CurrentAsync(cancellationToken);
        if (grants is null)
        {
            return KafkaGateDecision.Closed("o registro de produtores não pôde ser lido");
        }

        return grants.IsEmpty
            ? KafkaGateDecision.Closed("o registro de produtores está vazio")
            : KafkaGateDecision.Open;
    }
}
