using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.SQS;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.LocalStack;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace NotificationHub.IntegrationTests.Notifications.DeliveryTracking;

/// <summary>
/// Full environment of delivery tracking: the API host that answers provider
/// callbacks over disposable Postgres, Redis and LocalStack containers, plus
/// builders for the worker side (the delivery-tracker consumer and the relay),
/// all pointing at the same stores.
/// <para>
/// The provider verification secrets are minted here, once, and the tests sign
/// their vectors with the same material: a signature is tested by a fixed
/// vector this suite computes, never by calling a provider.
/// </para>
/// </summary>
public sealed class DeliveryTrackingFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Issuer = "integration-tests";
    private const string Audience = "notification-hub";
    private const string EnvelopeKeyId = "integration-tests-envelope";

    public const string RedisKeyPrefix = "it-delivery:";

    /// <summary>Twilio account auth token this suite signs its callback vectors with.</summary>
    public const string TwilioAuthToken = "integration-tests-twilio-auth-token";

    /// <summary>Public base a derived host uses to prove the signed URL is configurable.</summary>
    public const string PublicBaseUrl = "https://hooks.notification-hub.test";

    /// <summary>
    /// Queues the fixture provisions; queue creation belongs to infrastructure,
    /// never to code under test. The core queue of the seeded class is here
    /// because feedback that exhausts a step asks the Core for the next one,
    /// and the relay of this fixture has to have somewhere to put that ask.
    /// The contact-invalidation queue is here for the same reason: a refused
    /// destination reaches the contact ledger, whose write announces the
    /// invalidation, and a relay with nowhere to put that row fails the pass
    /// for every test sharing this fixture.
    /// </summary>
    private static readonly string[] Queues =
        ["delivery-events", "core-transactional", "contacts-changed"];

    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly string _envelopeMasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly ECDsa _sendGridKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

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

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public string SqsEndpoint => _localStack.GetConnectionString();

    /// <summary>Signs a SendGrid event callback exactly as the provider does.</summary>
    public byte[] SignSendGrid(string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes(timestamp);
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        return _sendGridKey.SignData(
            signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public IAmazonSQS Sqs => _sqs
        ?? throw new InvalidOperationException("O LocalStack ainda não foi iniciado.");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration((_, configuration)
            => configuration.AddInMemoryCollection(HostSettings()));

    /// <summary>The settings the API host reads, with room for a derived host to override one.</summary>
    public Dictionary<string, string?> HostSettings(IDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Modules:Audit:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
            ["Modules:TemplateManagement:Cache:Redis:ConnectionString"] = _redis.GetConnectionString(),
            ["Modules:TemplateManagement:Cache:Redis:InstanceName"] = RedisKeyPrefix,
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
            ["Modules:Notifications:Redis:ConnectionString"] = _redis.GetConnectionString(),
            ["Modules:Notifications:Redis:KeyPrefix"] = RedisKeyPrefix,
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
            ["Modules:Dispatch:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
            ["Modules:Dispatch:Webhooks:Twilio:AuthToken"] = TwilioAuthToken,
            ["Modules:Dispatch:Webhooks:SendGrid:PublicKey"] =
                Convert.ToBase64String(_sendGridKey.ExportSubjectPublicKeyInfo()),
            ["Platform:Messaging:Ef:ConnectionString"] = _postgres.GetConnectionString(),
            ["Platform:Cryptography:Envelope:KeyId"] = EnvelopeKeyId,
            ["Platform:Cryptography:Envelope:MasterKey"] = _envelopeMasterKey,
            ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
            ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
            ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
            ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(_signingKey),
        };
        Apply(settings, overrides);
        return settings;
    }

    /// <summary>The settings the delivery-tracker composition reads.</summary>
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
            ["Modules:Audit:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = PostgresConnectionString,

            // The tracker role reports refused destinations to the contact
            // ledger through the published contract, and that write is the
            // contact context's own transaction over its own store.
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
        };
        Apply(settings, overrides);
        return settings;
    }

    /// <summary>The delivery-tracker role composed exactly as the worker host would compose it.</summary>
    public ServiceProvider BuildDeliveryTrackerProvider(
        IDictionary<string, string?>? overrides = null,
        ILoggerProvider? loggerProvider = null,
        Action<IServiceCollection>? replaceServices = null)
        => BuildProvider(
            DeliveryTrackerWorkerRole.ConfigureServices, overrides, loggerProvider, replaceServices);

    /// <summary>A relay composition against the containers, mirroring the relay fixture.</summary>
    public ServiceProvider BuildRelayProvider(IDictionary<string, string?>? overrides = null)
        => BuildProvider(
            static (services, configuration) =>
            {
                services.AddPlatformMessaging(configuration);
                services.AddOutboxRelay(configuration);
            },
            overrides);

    /// <summary>Runs one receive-and-settle pass of the delivery-feedback consumer.</summary>
    internal static async Task<SqsConsumePassResult> RunTrackerPassAsync(ServiceProvider provider)
        => await BuildConsumer<DeliveryEventMessageProcessor>(provider, "delivery-events")
            .RunPassAsync(CancellationToken.None);

    internal static async Task<OutboxRelayPassResult> RunRelayPassAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxRelay>().RunPassAsync(CancellationToken.None);
    }

    public async Task<T> QueryNotificationsDbAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    public async Task ExecuteNotificationsDbAsync(Func<NotificationsDbContext, Task> action)
    {
        using IServiceScope scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    public async Task<T> QueryAuditDbAsync<T>(Func<AuditDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuditDbContext>());
    }

    public async Task<T> QueryContactConsentDbAsync<T>(Func<ContactConsentDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>());
    }

    public async Task ExecuteContactConsentDbAsync(Func<ContactConsentDbContext, Task> action)
    {
        using IServiceScope scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>());
    }

    public async Task<T> QueryPlatformDbAsync<T>(Func<PlatformMessagingDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>());
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable) return;

        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _localStack.StartAsync());

        var config = new AmazonSQSConfig
        {
            ServiceURL = SqsEndpoint,
            AuthenticationRegion = "us-east-1",
        };
        _sqs = new AmazonSQSClient(new BasicAWSCredentials("test", "test"), config);
        foreach (var queue in Queues) await _sqs.CreateQueueAsync(queue);

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
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _sqs?.Dispose();
        _sendGridKey.Dispose();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _localStack.DisposeAsync();
    }

    private static void Apply(
        Dictionary<string, string?> settings,
        IDictionary<string, string?>? overrides)
    {
        if (overrides is null) return;

        foreach ((var key, var value) in overrides) settings[key] = value;
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
            if (loggerProvider is not null) logging.AddProvider(loggerProvider);
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
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("delivery-tracking-tests"));
}

[CollectionDefinition(Name)]
public sealed class DeliveryTrackingCollectionDefinition : ICollectionFixture<DeliveryTrackingFixture>
{
    public const string Name = "delivery-tracking";
}
