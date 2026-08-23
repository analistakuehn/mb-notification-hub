namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Connection settings of the SQS client used by the relay. Everything is
/// optional on purpose: without overrides the AWS SDK resolves region and
/// credentials from its default chain (instance profile, environment); tests
/// and local runs point <see cref="ServiceUrl"/> at LocalStack with static
/// keys.
/// </summary>
public sealed class OutboxSqsOptions
{
    public const string SectionName = "Platform:Messaging:Sqs";

    /// <summary>Custom endpoint (LocalStack); null uses the AWS default.</summary>
    public string? ServiceUrl { get; init; }

    public string? Region { get; init; }

    public string? AccessKey { get; init; }

    public string? SecretKey { get; init; }

    /// <summary>
    /// Explicit destination-to-queue-URL map. A mapped destination skips the
    /// <c>GetQueueUrl</c> lookup entirely; unmapped destinations resolve by
    /// name once and cache the URL. The relay never creates a queue either way.
    /// </summary>
    public Dictionary<string, string> QueueUrls { get; init; } = [];
}
