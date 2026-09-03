using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace NotificationHub.IntegrationTests.Ingress;

/// <summary>
/// Full environment of the bus ingress: the API host over disposable Postgres
/// and Redis containers for seeding the governed catalog, plus a Kafka broker
/// with the entry and dead-letter topics provisioned. Topic provisioning
/// belongs to infrastructure, never to the code under test, so the fixture
/// creates them and the hub never does.
/// </summary>
public sealed class KafkaIngressFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string RequestedTopic = "notifications.requested.kyc.v1";
    public const string SecondaryRequestedTopic = "notifications.requested.billing.v1";
    public const string RequestedProducer = "kyc-service";
    public const string SecondaryRequestedProducer = "billing-service";
    public const string DeadLetterTopic = "notifications.requested.dlt";
    public const string ConsumerGroup = "notification-hub-ingress";
    public const string RedisKeyPrefix = "it-ingress:";

    private const string Issuer = "integration-tests";
    private const string Audience = "notification-hub";
    private const string EnvelopeKeyId = "integration-tests-envelope";

    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly string _envelopeMasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder()
        .WithImage("confluentinc/cp-kafka:7.5.0")
        .Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public string BootstrapServers => _kafka.GetBootstrapAddress();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration((_, configuration)
            => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Audit:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:Notifications:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),

                // The claim writes on the connection the acceptance already
                // holds, so the attachment schema has to live in the same
                // physical database as the ingestion.
                ["Modules:AttachmentManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:Notifications:Redis:ConnectionString"] = _redis.GetConnectionString(),
                ["Modules:Notifications:Redis:KeyPrefix"] = RedisKeyPrefix,
                ["Platform:Messaging:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Platform:Cryptography:Envelope:KeyId"] = EnvelopeKeyId,
                ["Platform:Cryptography:Envelope:MasterKey"] = _envelopeMasterKey,
                ["Authentication:Schemes:Bearer:ValidIssuer"] = Issuer,
                ["Authentication:Schemes:Bearer:ValidAudiences:0"] = Audience,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = Issuer,
                ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] = Convert.ToBase64String(_signingKey),
            }));

    /// <summary>The ingress role composed exactly as the worker host would compose it.</summary>
    public ServiceProvider BuildIngressProvider(
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? replaceServices = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Platform:Messaging:Ef:ConnectionString"] = PostgresConnectionString,
            ["Platform:Messaging:KafkaConsumer:BootstrapServers"] = BootstrapServers,
            ["Platform:Messaging:KafkaConsumer:PollTimeoutMilliseconds"] = "500",
            ["Platform:Messaging:KafkaConsumer:BatchSize"] = "10",
            ["Platform:Messaging:KafkaConsumer:PauseSeconds"] = "2",
            ["Platform:Messaging:KafkaConsumer:TransientRetryAttempts"] = "1",
            ["Platform:Messaging:KafkaConsumer:TransientRetryBaseMilliseconds"] = "50",
            ["Platform:Cryptography:Envelope:KeyId"] = EnvelopeKeyId,
            ["Platform:Cryptography:Envelope:MasterKey"] = _envelopeMasterKey,
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
            ["Modules:Notifications:Redis:ConnectionString"] = RedisConnectionString,
            ["Modules:Notifications:Redis:KeyPrefix"] = RedisKeyPrefix,
            ["Modules:Notifications:ProducerRegistry:CacheTtlSeconds"] = "1",
            ["Modules:Notifications:KafkaIngress:DeadLetterTopic"] = DeadLetterTopic,
            ["Modules:Notifications:KafkaIngress:ConsumerGroup"] = ConsumerGroup,
            ["Modules:Notifications:KafkaIngress:Bindings:0:Topic"] = RequestedTopic,
            ["Modules:Notifications:KafkaIngress:Bindings:0:LogicalProducer"] = RequestedProducer,
            ["Modules:Notifications:KafkaIngress:Bindings:1:Topic"] = SecondaryRequestedTopic,
            ["Modules:Notifications:KafkaIngress:Bindings:1:LogicalProducer"] = SecondaryRequestedProducer,
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
        };
        if (overrides is not null)
        {
            foreach ((var key, var value) in overrides)
            {
                settings[key] = value;
            }
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        KafkaIngressWorkerRole.ConfigureServices(services, configuration);
        replaceServices?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>
    /// Materializes producer grants, the job a deploy performs in a real
    /// environment. Idempotent: an existing grant stays.
    /// </summary>
    public async Task SeedProducerGrantsAsync(
        params (string Principal, string Application, string Class)[] grants)
    {
        using IServiceScope scope = Services.CreateScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        foreach ((var principal, var application, var notificationClass) in grants)
        {
            var exists = await db.ProducerRegistrations.AnyAsync(
                registration => registration.Principal == principal
                    && registration.Application == application
                    && registration.Class == notificationClass);
            if (!exists)
            {
                db.ProducerRegistrations.Add(ProducerRegistration.Create(
                    principal, application, notificationClass, DateTimeOffset.UtcNow).Value!);
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task ClearProducerGrantsAsync()
    {
        using IServiceScope scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>()
            .ProducerRegistrations
            .ExecuteDeleteAsync();
    }

    /// <summary>Publishes one raw record and returns the partition and offset the broker assigned.</summary>
    public async Task<TopicPartitionOffset> ProduceAsync(
        string topic,
        string key,
        string body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using IProducer<string, byte[]> producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = BootstrapServers, Acks = Acks.All }).Build();
        var recordHeaders = new Headers();
        foreach ((var name, var value) in headers ?? new Dictionary<string, string>())
        {
            recordHeaders.Add(name, Encoding.UTF8.GetBytes(value));
        }

        DeliveryResult<string, byte[]> result = await producer.ProduceAsync(
            topic,
            new Message<string, byte[]>
            {
                Key = key,
                Value = Encoding.UTF8.GetBytes(body),
                Headers = recordHeaders,
            });
        return result.TopicPartitionOffset;
    }

    /// <summary>
    /// Reads a topic from the beginning until it goes quiet. The idle cutoff
    /// is what keeps a read of "everything published so far" from paying the
    /// whole budget on every call.
    /// </summary>
    public List<ConsumeResult<string, byte[]>> ReadAll(string topic, TimeSpan budget)
    {
        using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(
            new ConsumerConfig
            {
                BootstrapServers = BootstrapServers,
                GroupId = $"reader-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();
        consumer.Subscribe(topic);
        var received = new List<ConsumeResult<string, byte[]>>();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + budget;
        var idlePolls = 0;
        while (DateTimeOffset.UtcNow < deadline && idlePolls < 6)
        {
            ConsumeResult<string, byte[]>? result = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (result is not null && !result.IsPartitionEOF)
            {
                received.Add(result);
                idlePolls = 0;
                continue;
            }

            idlePolls++;
        }

        consumer.Close();
        return received;
    }

    /// <summary>The offset the ingress group committed for one partition, or null when it committed none.</summary>
    public long? CommittedOffset(string topic, int partition)
    {
        using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(
            new ConsumerConfig
            {
                BootstrapServers = BootstrapServers,
                GroupId = ConsumerGroup,
                EnableAutoCommit = false,
            }).Build();
        List<TopicPartitionOffset> committed = consumer.Committed(
            [new TopicPartition(topic, new Partition(partition))], TimeSpan.FromSeconds(10));
        consumer.Close();
        Offset offset = committed[0].Offset;
        return offset == Offset.Unset ? null : offset.Value;
    }

    public HttpClient CreateAuthorClient(string subject) => CreateClientWithToken(subject, ["Templates.Author"]);

    public HttpClient CreatePublisherClient(string subject) => CreateClientWithToken(subject, ["Templates.Publish"]);

    public HttpClient CreateProducerClient(string subject, params string[] sendRoles)
        => CreateClientWithToken(subject, sendRoles);

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

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _kafka.StartAsync());

        using (IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = BootstrapServers }).Build())
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification { Name = RequestedTopic, NumPartitions = 1, ReplicationFactor = 1 },
                new TopicSpecification
                {
                    Name = SecondaryRequestedTopic,
                    NumPartitions = 1,
                    ReplicationFactor = 1,
                },
                new TopicSpecification { Name = DeadLetterTopic, NumPartitions = 1, ReplicationFactor = 1 },
            ]);
        }

        using IServiceScope scope = Services.CreateScope();

        // TemplateManagement first on purpose: its history creates the audit
        // trail tables the Audit adoption migration takes over.
        await scope.ServiceProvider.GetRequiredService<TemplateManagementDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>().Database.MigrateAsync();

        // Attachments have no migration history of their own yet, so the
        // schema is created from the model.
        await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>()
            .CreateTablesAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _kafka.DisposeAsync();
    }

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
public sealed class KafkaIngressCollectionDefinition : ICollectionFixture<KafkaIngressFixture>
{
    public const string Name = "kafka-ingress";
}
