using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using NotificationHub.Api.Infrastructure.Messaging.Relay;

namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>
/// Builds the SQS client both platform sides share: the relay publishes with
/// it and the consumers poll with it, from the same connection options.
/// </summary>
internal static class SqsClientFactory
{
    internal static AmazonSQSClient Create(OutboxSqsOptions options)
    {
        var config = new AmazonSQSConfig();
        if (options.ServiceUrl is not null)
        {
            config.ServiceURL = options.ServiceUrl;
            if (options.Region is not null)
            {
                config.AuthenticationRegion = options.Region;
            }
        }
        else if (options.Region is not null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        // Without static keys the SDK falls back to its default credential
        // chain (instance profile, environment), which is the production path.
        return options is { AccessKey: not null, SecretKey: not null }
            ? new AmazonSQSClient(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonSQSClient(config);
    }
}
