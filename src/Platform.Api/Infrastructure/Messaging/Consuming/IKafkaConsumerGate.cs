namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>Whether a consuming role may subscribe, and why not when it may not.</summary>
public sealed record KafkaGateDecision(bool CanConsume, string? Reason)
{
    public static KafkaGateDecision Open { get; } = new(true, null);

    public static KafkaGateDecision Closed(string reason) => new(false, reason);
}

/// <summary>
/// Precondition a consuming role must satisfy before it subscribes. It exists
/// because subscribing without the data a role decides with is worse than not
/// subscribing at all: the records would be read, refused for the wrong
/// reason, and lost to the topic retention while the deployment looked
/// healthy. A role with no precondition leaves the default open gate in place.
/// </summary>
public interface IKafkaConsumerGate
{
    Task<KafkaGateDecision> EvaluateAsync(CancellationToken cancellationToken);
}

/// <summary>Default gate: nothing to wait for.</summary>
internal sealed class AlwaysOpenKafkaConsumerGate : IKafkaConsumerGate
{
    public Task<KafkaGateDecision> EvaluateAsync(CancellationToken cancellationToken)
        => Task.FromResult(KafkaGateDecision.Open);
}
