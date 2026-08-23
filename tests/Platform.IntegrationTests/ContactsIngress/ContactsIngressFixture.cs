using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace NotificationHub.IntegrationTests.ContactsIngress;

/// <summary>
/// Full environment of the contact ingestion: the API host over disposable
/// Postgres and Redis containers, used to seed contacts through the published
/// write routes, plus a Kafka broker with the entry and dead-letter topics
/// provisioned. Topic provisioning belongs to infrastructure, never to the
/// code under test, so the fixture creates them and the hub never does.
///
/// Both compositions share the envelope master key on purpose: a contact
/// declared over REST and one declared over the bus must hash and decrypt the
/// same way, and a divergence here would silently turn every reconciliation
/// into an insert.
/// </summary>
public sealed class ContactsIngressFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ContactsTopic = "contacts.events.v1";
    public const string DeadLetterTopic = "contacts.events.dlt";
    public const string ConsumerGroup = "notification-hub-contacts-ingress";
    public const string AcceptedSource = "urn:araia:cadastro";
    public const string RedisKeyPrefix = "it-contacts-ingress:";

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

    public string BootstrapServers => _kafka.GetBootstrapAddress();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration((_, configuration)
            => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Audit:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = _postgres.GetConnectionString(),
                ["Modules:TemplateManagement:Cache:Redis:ConnectionString"] = _redis.GetConnectionString(),
                ["Modules:TemplateManagement:Cache:Redis:InstanceName"] = RedisKeyPrefix,
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
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = PostgresConnectionString,
            ["Modules:ContactConsent:KafkaIngress:ContactsTopic"] = ContactsTopic,
            ["Modules:ContactConsent:KafkaIngress:DeadLetterTopic"] = DeadLetterTopic,
            ["Modules:ContactConsent:KafkaIngress:ConsumerGroup"] = ConsumerGroup,
            ["Modules:ContactConsent:KafkaIngress:AcceptedSources:0"] = AcceptedSource,
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
        ContactsIngressWorkerRole.ConfigureServices(services, configuration);
        replaceServices?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
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
    /// whole budget on every call, and it is generous on purpose: joining the
    /// group takes several polls on a loaded machine, and a reader that gave
    /// up during the join would report an empty topic that is not empty.
    /// </summary>
    public List<ConsumeResult<string, byte[]>> ReadAll(string topic, TimeSpan budget)
    {
        const int IdlePollsBeforeGivingUp = 12;
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
        while (DateTimeOffset.UtcNow < deadline && idlePolls < IdlePollsBeforeGivingUp)
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

    public HttpClient CreateWriterClient(string subject)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(subject, ["Contacts.Write"]));
        return client;
    }

    public async Task<T> QueryContactConsentDbAsync<T>(Func<ContactConsentDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>());
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

        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _kafka.StartAsync());

        using (IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = BootstrapServers }).Build())
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification { Name = ContactsTopic, NumPartitions = 1, ReplicationFactor = 1 },
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
        await scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>().Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _kafka.DisposeAsync();
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
public sealed class ContactsIngressCollectionDefinition : ICollectionFixture<ContactsIngressFixture>
{
    public const string Name = "contacts-ingress";
}
