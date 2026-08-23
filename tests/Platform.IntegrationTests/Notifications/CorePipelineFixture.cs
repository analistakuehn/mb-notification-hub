using System.Net.Http.Headers;
using System.Security.Cryptography;
using Amazon.Runtime;
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
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
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
/// </summary>
public sealed class CorePipelineFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Issuer = "integration-tests";
    private const string Audience = "notification-hub";
    private const string EnvelopeKeyId = "integration-tests-envelope";

    public const string RedisKeyPrefix = "it-core:";

    /// <summary>Queues the fixture provisions; queue creation belongs to infrastructure, never to code under test.</summary>
    private static readonly string[] Queues =
    [
        "core-auth", "core-critical", "core-transactional", "core-operational",
        "contacts-changed",
        "dispatch-push-auth", "dispatch-sms-auth",
        "dispatch-push-critical", "dispatch-sms-critical",
        "dispatch-push-transactional", "dispatch-sms-transactional", "dispatch-email-transactional",
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

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public string SqsEndpoint => _localStack.GetConnectionString();

    /// <summary>Client the tests use to seed queues and read messages back.</summary>
    public IAmazonSQS Sqs => _sqs
        ?? throw new InvalidOperationException("O LocalStack ainda não foi iniciado.");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration((_, configuration)
            => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Audit:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:TemplateManagement:Cache:Redis:ConnectionString"] = _redis.GetConnectionString(),
                ["Modules:TemplateManagement:Cache:Redis:InstanceName"] = "it-core:",
                ["Modules:Notifications:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:Notifications:Redis:ConnectionString"] = _redis.GetConnectionString(),
                ["Modules:Notifications:Redis:KeyPrefix"] = RedisKeyPrefix,
                ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
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
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
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
    public ServiceProvider BuildCoreWorkerProvider(IDictionary<string, string?>? overrides = null)
        => BuildProvider(CoreWorkerRole.ConfigureServices, overrides);

    /// <summary>The contact-consent role composed exactly as the worker host would compose it.</summary>
    public ServiceProvider BuildContactConsentWorkerProvider(IDictionary<string, string?>? overrides = null)
        => BuildProvider(ContactConsentWorkerRole.ConfigureServices, overrides);

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

    internal static async Task<OutboxRelayPassResult> RunRelayPassAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxRelay>().RunPassAsync(CancellationToken.None);
    }

    /// <summary>Client authenticated as a producer carrying the given send roles.</summary>
    public HttpClient CreateProducerClient(string subject, params string[] sendRoles)
        => CreateClientWithToken(subject, sendRoles);

    public HttpClient CreateAuthorClient(string subject)
        => CreateClientWithToken(subject, ["Templates.Author"]);

    public HttpClient CreatePublisherClient(string subject)
        => CreateClientWithToken(subject, ["Templates.Publish"]);

    public HttpClient CreateContactsClient(string subject)
        => CreateClientWithToken(subject, ["Contacts.Write"]);

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
        _sqs = new AmazonSQSClient(new BasicAWSCredentials("test", "test"), config);
        foreach (var queue in Queues)
        {
            await _sqs.CreateQueueAsync(queue);
        }

        using IServiceScope scope = Services.CreateScope();

        // TemplateManagement first on purpose: its history creates the audit
        // trail tables the Audit adoption migration takes over. The other
        // histories are independent.
        await scope.ServiceProvider.GetRequiredService<TemplateManagementDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _sqs?.Dispose();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _localStack.DisposeAsync();
    }

    private ServiceProvider BuildProvider(
        Action<IServiceCollection, IConfiguration> configure,
        IDictionary<string, string?>? overrides)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(WorkerSettings(overrides))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services, configuration);
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
