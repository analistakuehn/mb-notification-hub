using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

internal static class AttachmentObjectStoreSetup
{
    /// <summary>
    /// Deadline for one call to the store, pinned here instead of inherited
    /// from the client library. The compensation path is the reason: it runs
    /// exactly when the store is slow or stopped, and an inherited deadline
    /// decides for how long a failed upload keeps holding a request slot on an
    /// endpoint that is rate limited.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Deadline for opening the connection alone.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Repetitions the client library may add on its own. Measured against the
    /// provider: one write whose answer is lost on the wire is repeated by the
    /// library, and each repetition that finds the key free places one more
    /// durable generation. The conditional write caps that at one generation;
    /// this caps how long the caller waits for the library to give up.
    /// </summary>
    private const int MaxErrorRetries = 2;

    internal static IServiceCollection AddAttachmentObjectStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AttachmentObjectStoreOptions>()
            .Bind(configuration.GetSection(AttachmentObjectStoreOptions.SectionName));

        services.AddSingleton<IAttachmentObjectStore>(serviceProvider =>
        {
            AttachmentObjectStoreOptions options = serviceProvider
                .GetRequiredService<IOptions<AttachmentObjectStoreOptions>>()
                .Value;
            if (!options.IsUsable)
            {
                return new UnavailableAttachmentObjectStore();
            }

            try
            {
                // The two clients are the seam for splitting the principal.
                // Today they are the same instance, so the running credential
                // needs, in one principal: placing an object, reading an
                // object by generation, and removing an object by generation.
                // Removing by generation is the capability that ends a
                // generation permanently, and it is the one this module has no
                // way to keep away from the path that accepts producer bytes
                // while a single credential serves both. Handing removal its
                // own client is a change to this factory alone; no interface
                // and no caller moves.
                AmazonS3Client client = CreateClient(options);
                return new S3AttachmentObjectStore(client, client, options.Bucket!);
            }
            catch (AmazonClientException)
            {
                return new UnavailableAttachmentObjectStore();
            }
            catch (ArgumentException)
            {
                return new UnavailableAttachmentObjectStore();
            }
        });

        return services;
    }

    private static AmazonS3Client CreateClient(AttachmentObjectStoreOptions options)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            Timeout = RequestTimeout,
            ConnectTimeout = ConnectTimeout,
            MaxErrorRetry = MaxErrorRetries,
            RetryMode = RequestRetryMode.Standard,
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            if (!string.IsNullOrWhiteSpace(options.Region))
            {
                config.AuthenticationRegion = options.Region;
            }
        }
        else if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return options is { AccessKey: not null, SecretKey: not null }
            ? new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                config)
            : new AmazonS3Client(config);
    }
}
