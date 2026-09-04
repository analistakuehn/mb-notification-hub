using System.Net.Http.Headers;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Authorization;
using NotificationHub.Api.Modules.ContactConsent;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Dispatch.Domain;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.LocalStack;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// Full environment of the Core pipeline: the API host over disposable
/// Postgres, Redis and LocalStack containers, plus builders for the worker
/// side (core consumer, contacts-changed consumer, relay), all pointing at
/// the same stores and sharing one envelope master key so what one side
/// seals the other opens.
/// <para>
/// Open for derivation rather than sealed, and only for that: a suite whose
/// scans read the whole table cannot share these stores with anyone, and
/// deriving gives it a set of containers of its own without a second copy of
/// this composition. Every collection that names a distinct fixture type gets
/// a distinct instance, which is the isolation such a suite needs.
/// </para>
/// </summary>
public class CorePipelineFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Issuer = "integration-tests";
    private const string Audience = "notification-hub";
    private const string EnvelopeKeyId = "integration-tests-envelope";

    public const string RedisKeyPrefix = "it-core:";

    /// <summary>
    /// Bucket the custody of attachments runs on in this environment.
    /// <para>
    /// It exists because a send that carries an accepted set opens the bytes of
    /// every member on its way to the provider. Without a store behind the
    /// handle, every such send would fail on custody, and a suite about routing
    /// or about a verdict would be reporting an environment that has no bytes.
    /// </para>
    /// </summary>
    public const string AttachmentBucket = "core-pipeline-attachments";

    private const string AwsAccessKey = "test";

    private const string AwsSecretKey = "test";

    /// <summary>
    /// Queues the fixture provisions; queue creation belongs to infrastructure,
    /// never to code under test. The delivery feedback queue is here because a
    /// provider callback announces its evidence through the outbox, and a relay
    /// pass with nowhere to put that row fails for every test sharing this
    /// environment, including the ones that never touched a callback.
    /// </summary>
    private static readonly string[] Queues =
    [
        "core-auth", "core-critical", "core-transactional", "core-operational",
        "contacts-changed", "delivery-events",
        "dispatch-push-auth", "dispatch-sms-auth", "dispatch-email-auth",
        "dispatch-push-critical", "dispatch-sms-critical", "dispatch-email-critical",
        "dispatch-push-transactional", "dispatch-sms-transactional", "dispatch-email-transactional",
        "dispatch-push-operational", "dispatch-email-operational",
    ];

    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly string _envelopeMasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private readonly LocalStackContainer _localStack = new LocalStackBuilder()
        .WithImage("localstack/localstack:4.4")
        .Build();

    private AmazonSQSClient? _sqs;
    private AmazonS3Client? _s3;

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public string SqsEndpoint => _localStack.GetConnectionString();

    /// <summary>Client the tests use to seed queues and read messages back.</summary>
    public IAmazonSQS Sqs => _sqs
        ?? throw new InvalidOperationException("O LocalStack ainda não foi iniciado.");

    /// <summary>Client the arrangements use to put attachment bytes in custody.</summary>
    public IAmazonS3 S3 => _s3
        ?? throw new InvalidOperationException("O LocalStack ainda não foi iniciado.");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration((_, configuration)
            => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Audit:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:Notifications:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),

                // Attachments share the physical database of the ingestion,
                // because the claim of a set runs its statements on the
                // connection the acceptance transaction already holds. Pointed
                // anywhere else, the schema the claim writes to would not be
                // the schema the module's own endpoints write to.
                ["Modules:AttachmentManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),

                // The capability ships switched off, so the acceptance path
                // that claims a set only exists in a host that says otherwise.
                // Stated here so every suite over this fixture measures the
                // enabled deployment on purpose instead of by inheritance.
                ["Modules:AttachmentManagement:Capability:AcceptsNewAttachments"] = "true",
                ["Modules:AttachmentManagement:Storage:S3:Bucket"] = AttachmentBucket,
                ["Modules:AttachmentManagement:Storage:S3:ServiceUrl"] = _localStack.GetConnectionString(),
                ["Modules:AttachmentManagement:Storage:S3:Region"] = "us-east-1",
                ["Modules:AttachmentManagement:Storage:S3:AccessKey"] = AwsAccessKey,
                ["Modules:AttachmentManagement:Storage:S3:SecretKey"] = AwsSecretKey,
                ["Modules:AttachmentManagement:Storage:S3:ForcePathStyle"] = "true",
                ["Modules:Notifications:Redis:ConnectionString"] = _redis.GetConnectionString(),
                ["Modules:Notifications:Redis:KeyPrefix"] = RedisKeyPrefix,
                ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:Dispatch:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Platform:Messaging:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Platform:Cryptography:Envelope:KeyId"] = EnvelopeKeyId,
                ["Platform:Cryptography:Envelope:MasterKey"] = _envelopeMasterKey,
                ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(_signingKey),
            }));

    /// <summary>The settings every worker-side composition reads.</summary>
    public Dictionary<string, string?> WorkerSettings(IDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Platform:Messaging:Ef:ConnectionString"] = PostgresConnectionString,
            ["Platform:Messaging:Sqs:ServiceUrl"] = SqsEndpoint,
            ["Platform:Messaging:Sqs:Region"] = "us-east-1",
            ["Platform:Messaging:Sqs:AccessKey"] = "test",
            ["Platform:Messaging:Sqs:SecretKey"] = "test",
            ["Platform:Messaging:Consumer:WaitTimeSeconds"] = "1",
            ["Platform:Messaging:Consumer:BackoffBaseSeconds"] = "1",
            ["Platform:Messaging:Consumer:BackoffMaxSeconds"] = "2",
            ["Platform:Cryptography:Envelope:KeyId"] = EnvelopeKeyId,
            ["Platform:Cryptography:Envelope:MasterKey"] = _envelopeMasterKey,
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
            ["Modules:Notifications:Redis:ConnectionString"] = RedisConnectionString,
            ["Modules:Notifications:Redis:KeyPrefix"] = RedisKeyPrefix,
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
            ["Modules:ContactConsent:Redis:ConnectionString"] = RedisConnectionString,
            ["Modules:ContactConsent:Redis:KeyPrefix"] = RedisKeyPrefix,
            ["Modules:Dispatch:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
            ["Modules:Dispatch:RateLimits:RedisConnectionString"] = RedisConnectionString,
            ["Modules:Dispatch:RateLimits:KeyPrefix"] = RedisKeyPrefix,
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = PostgresConnectionString,

            // The dispatcher revalidates the accepted set against the owning
            // module's own record before it calls a provider, so a worker-side
            // composition needs that store and the capacity in force, exactly
            // as the API host has them.
            ["Modules:AttachmentManagement:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
            ["Modules:AttachmentManagement:Capacity:MaxAttachmentBytes"] = "7340032",
            ["Modules:AttachmentManagement:Capacity:MaxEnvelopeBytes"] = "7340032",
            ["Modules:AttachmentManagement:Capacity:MaxAttachmentsPerNotification"] = "10",

            // And the custody itself, because the send composes the message out
            // of the bytes: the worker side reads them through the owning
            // module's port exactly as the host writes them through it.
            ["Modules:AttachmentManagement:Storage:S3:Bucket"] = AttachmentBucket,
            ["Modules:AttachmentManagement:Storage:S3:ServiceUrl"] = SqsEndpoint,
            ["Modules:AttachmentManagement:Storage:S3:Region"] = "us-east-1",
            ["Modules:AttachmentManagement:Storage:S3:AccessKey"] = AwsAccessKey,
            ["Modules:AttachmentManagement:Storage:S3:SecretKey"] = AwsSecretKey,
            ["Modules:AttachmentManagement:Storage:S3:ForcePathStyle"] = "true",
        };
        if (overrides is not null)
        {
            foreach ((var key, var value) in overrides)
            {
                settings[key] = value;
            }
        }

        return settings;
    }

    /// <summary>The core role composed exactly as the worker host would compose it.</summary>
    public ServiceProvider BuildCoreWorkerProvider(
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? replaceServices = null)
        => BuildProvider(CoreWorkerRole.ConfigureServices, overrides, replaceServices: replaceServices);

    /// <summary>The contact-consent role composed exactly as the worker host would compose it.</summary>
    public ServiceProvider BuildContactConsentWorkerProvider(IDictionary<string, string?>? overrides = null)
        => BuildProvider(ContactConsentWorkerRole.ConfigureServices, overrides);

    /// <summary>The dispatcher role composed exactly as the worker host would compose it.</summary>
    public ServiceProvider BuildDispatcherWorkerProvider(
        IDictionary<string, string?>? overrides = null,
        ILoggerProvider? loggerProvider = null,
        Action<IServiceCollection>? replaceServices = null)
        => BuildProvider(DispatcherWorkerRole.ConfigureServices, overrides, loggerProvider, replaceServices);

    /// <summary>The delivery-tracker role composed exactly as the worker host would compose it.</summary>
    public ServiceProvider BuildDeliveryTrackerProvider(
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? replaceServices = null)
        => BuildProvider(DeliveryTrackerWorkerRole.ConfigureServices, overrides, replaceServices: replaceServices);

    /// <summary>The notifications-maintenance role composed exactly as the worker host would compose it.</summary>
    public ServiceProvider BuildMaintenanceWorkerProvider(
        IDictionary<string, string?>? overrides = null,
        ILoggerProvider? loggerProvider = null,
        Action<IServiceCollection>? replaceServices = null)
        => BuildProvider(
            NotificationsMaintenanceWorkerRole.ConfigureServices, overrides, loggerProvider, replaceServices);

    /// <summary>A relay composition against the containers, mirroring the relay fixture.</summary>
    public ServiceProvider BuildRelayProvider(IDictionary<string, string?>? overrides = null)
        => BuildProvider(
            static (services, configuration) =>
            {
                services.AddPlatformMessaging(configuration);
                services.AddOutboxRelay(configuration);
            },
            overrides);

    /// <summary>Runs one receive-and-settle pass of the core consumer over one queue.</summary>
    internal static async Task<SqsConsumePassResult> RunCorePassAsync(ServiceProvider provider, string queue)
        => await BuildConsumer<CoreMessageProcessor>(provider, queue).RunPassAsync(CancellationToken.None);

    /// <summary>Runs one receive-and-settle pass of the contacts-changed consumer.</summary>
    internal static async Task<SqsConsumePassResult> RunContactsChangedPassAsync(ServiceProvider provider)
        => await BuildConsumer<ContactsChangedProcessor>(provider, "contacts-changed")
            .RunPassAsync(CancellationToken.None);

    /// <summary>Runs one receive-and-settle pass of the dispatch consumer over one queue.</summary>
    internal static async Task<SqsConsumePassResult> RunDispatchPassAsync(ServiceProvider provider, string queue)
        => await BuildConsumer<DispatchMessageProcessor>(provider, queue).RunPassAsync(CancellationToken.None);

    /// <summary>Runs one receive-and-settle pass of the delivery-feedback consumer.</summary>
    internal static async Task<SqsConsumePassResult> RunDeliveryEventPassAsync(ServiceProvider provider)
        => await BuildConsumer<DeliveryEventMessageProcessor>(provider, "delivery-events")
            .RunPassAsync(CancellationToken.None);

    internal static async Task<OutboxRelayPassResult> RunRelayPassAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxRelay>().RunPassAsync(CancellationToken.None);
    }

    /// <summary>Client authenticated as a producer carrying the given send roles.</summary>
    public HttpClient CreateProducerClient(string subject, params string[] sendRoles)
        => CreateClientWithToken(subject, sendRoles);

    /// <summary>Producer client for a host derived with <c>WithWebHostBuilder</c>.</summary>
    public HttpClient CreateProducerClient(
        WebApplicationFactory<Program> host,
        string subject,
        params string[] sendRoles)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(subject, sendRoles));
        return client;
    }

    public HttpClient CreateAuthorClient(string subject)
        => CreateClientWithToken(subject, ["Templates.Author"]);

    public HttpClient CreatePublisherClient(string subject)
        => CreateClientWithToken(subject, ["Templates.Publish"]);

    public HttpClient CreateContactsClient(string subject)
        => CreateClientWithToken(subject, ["Contacts.Write"]);

    /// <summary>Client authenticated as support or internal tooling: the read role, nothing else.</summary>
    public HttpClient CreateReaderClient(string subject)
        => CreateClientWithToken(subject, [NotificationsAuthorizationSetup.ReadRole]);

    /// <summary>Reader client for a host derived with <c>WithWebHostBuilder</c>.</summary>
    public HttpClient CreateReaderClient(WebApplicationFactory<Program> host, string subject)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateToken(subject, [NotificationsAuthorizationSetup.ReadRole]));
        return client;
    }

    /// <summary>Client authenticated as Compliance or Internal Audit: the audit role, nothing else.</summary>
    public HttpClient CreateAuditorClient(string subject)
        => CreateClientWithToken(subject, [ComplianceAuthorizationSetup.AuditRole]);

    /// <summary>Auditor client for a host derived with <c>WithWebHostBuilder</c>.</summary>
    public HttpClient CreateAuditorClient(WebApplicationFactory<Program> host, string subject)
    {
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateToken(subject, [ComplianceAuthorizationSetup.AuditRole]));
        return client;
    }

    public async Task<T> QueryNotificationsDbAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    public async Task<T> QueryAuditDbAsync<T>(Func<AuditDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuditDbContext>());
    }

    public async Task<T> QueryPlatformDbAsync<T>(Func<PlatformMessagingDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>());
    }

    public async Task<T> QueryContactConsentDbAsync<T>(Func<ContactConsentDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>());
    }

    /// <summary>
    /// Materializes the provider configuration rows the resolver reads, the
    /// job a deployment performs in a real environment. Idempotent: existing
    /// pairs stay.
    /// </summary>
    public async Task SeedProviderConfigAsync(params (string Channel, string ProviderKey)[] rows)
    {
        using IServiceScope scope = Services.CreateScope();
        DispatchDbContext db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
        foreach ((var channel, var providerKey) in rows)
        {
            var exists = await db.ProviderSelections.AnyAsync(
                selection => selection.ChannelValue == channel && selection.ProviderKey == providerKey);
            if (!exists)
            {
                db.ProviderSelections.Add(ProviderSelection.Create(
                    channel, providerKey, priority: 0, DateTimeOffset.UtcNow).Value!);
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task<T> UsingScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using IServiceScope scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _localStack.StartAsync());

        var config = new AmazonSQSConfig
        {
            ServiceURL = SqsEndpoint,
            AuthenticationRegion = "us-east-1",
        };
        var credentials = new BasicAWSCredentials(AwsAccessKey, AwsSecretKey);
        _sqs = new AmazonSQSClient(credentials, config);
        foreach (var queue in Queues)
        {
            await _sqs.CreateQueueAsync(queue);
        }

        // Versioned on purpose: the custody pins a generation by version, so a
        // bucket without versioning hands back no version and the capture is
        // refused as unidentified.
        _s3 = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = SqsEndpoint,
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
        });
        await _s3.PutBucketAsync(new PutBucketRequest { BucketName = AttachmentBucket });
        await _s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = AttachmentBucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        using IServiceScope scope = Services.CreateScope();

        // TemplateManagement first on purpose: its history creates the audit
        // trail tables the Audit adoption migration takes over. The other
        // histories are independent.
        await scope.ServiceProvider.GetRequiredService<TemplateManagementDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<DispatchDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>().Database.MigrateAsync();

        await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _sqs?.Dispose();
        _s3?.Dispose();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _localStack.DisposeAsync();
    }

    private ServiceProvider BuildProvider(
        Action<IServiceCollection, IConfiguration> configure,
        IDictionary<string, string?>? overrides,
        ILoggerProvider? loggerProvider = null,
        Action<IServiceCollection>? replaceServices = null)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(WorkerSettings(overrides))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (loggerProvider is not null)
            {
                logging.AddProvider(loggerProvider);
            }
        });
        configure(services, configuration);
        replaceServices?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static SqsQueueConsumer<TProcessor> BuildConsumer<TProcessor>(
        ServiceProvider provider,
        string queue)
        where TProcessor : ISqsMessageProcessor
        => new(
            new SqsQueueBinding(queue, PriorityRank: 0),
            provider.GetRequiredService<IAmazonSQS>(),
            provider.GetRequiredService<SqsQueueUrlResolver>(),
            new PrioritySlotAllocator(8),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<SqsConsumerOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("core-pipeline-tests"));

    private HttpClient CreateClientWithToken(string subject, IReadOnlyList<string> roles)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(subject, roles));
        return client;
    }

    private string CreateToken(string subject, IReadOnlyList<string> roles)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["role"] = roles,
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_signingKey),
                SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

[CollectionDefinition(Name)]
public sealed class CorePipelineCollectionDefinition : ICollectionFixture<CorePipelineFixture>
{
    public const string Name = "core-pipeline";
}
