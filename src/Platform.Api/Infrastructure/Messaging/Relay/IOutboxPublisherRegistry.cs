namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Resolves the publisher of one transport lane. The relay never picks a
/// client from the destination string: the stored transport is the key, so a
/// topic and a queue may share any naming convention without misrouting.
/// </summary>
internal interface IOutboxPublisherRegistry
{
    /// <summary>Transports this instance has a publisher for; a lane outside it is never claimed.</summary>
    IReadOnlyList<string> Transports { get; }

    IOutboxPublisher Resolve(string transport);
}

/// <summary>Transports the composition root registered a publisher for.</summary>
internal sealed record OutboxPublisherRegistrations(IReadOnlyList<string> Transports);

/// <summary>
/// Registry over the keyed registrations of the composition root. A transport
/// without a registered publisher refuses loudly instead of silently binding
/// the wrong client; its rows stay pending and surface on the per-transport
/// backlog of the relay health check.
/// </summary>
internal sealed class KeyedOutboxPublisherRegistry(
    IServiceProvider serviceProvider,
    OutboxPublisherRegistrations registrations) : IOutboxPublisherRegistry
{
    public IReadOnlyList<string> Transports => registrations.Transports;

    public IOutboxPublisher Resolve(string transport)
        => serviceProvider.GetKeyedService<IOutboxPublisher>(transport)
            ?? throw new InvalidOperationException(
                $"Nenhum publicador registrado para o transporte '{transport}' do outbox.");
}
