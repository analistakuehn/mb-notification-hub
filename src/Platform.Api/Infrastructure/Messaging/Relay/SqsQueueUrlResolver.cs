using System.Collections.Concurrent;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Resolves a destination to its queue URL: the configured map wins, then a
/// one-time <c>GetQueueUrl</c> lookup cached per destination. A missing queue
/// resolves to null and is never cached, so provisioning the queue heals the
/// relay without a restart. The resolver never creates a queue, in any
/// environment: queue provisioning belongs to infrastructure.
/// </summary>
internal sealed class SqsQueueUrlResolver(IAmazonSQS sqs, IOptions<OutboxSqsOptions> options)
{
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>The queue URL, or null when no queue exists for the destination.</summary>
    public async Task<string?> ResolveAsync(string destination, CancellationToken cancellationToken)
    {
        if (options.Value.QueueUrls.TryGetValue(destination, out var configured))
        {
            return configured;
        }

        if (_cache.TryGetValue(destination, out var cached))
        {
            return cached;
        }

        try
        {
            GetQueueUrlResponse response = await sqs.GetQueueUrlAsync(destination, cancellationToken);
            _cache[destination] = response.QueueUrl;
            return response.QueueUrl;
        }
        catch (QueueDoesNotExistException)
        {
            return null;
        }
    }
}
