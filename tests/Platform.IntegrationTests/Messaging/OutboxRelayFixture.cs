using System.Data.Common;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.IntegrationTests.TemplateManagement;
using Npgsql;
using Testcontainers.LocalStack;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Messaging;

/// <summary>
/// Disposable Postgres with the platform outbox migrated plus LocalStack with
/// the internal queues pre-created. The fixture creates the queues on
/// purpose: queue provisioning belongs to infrastructure, never to the
/// relay, and the missing-queue tests rely on that split.
/// </summary>
public sealed class OutboxRelayFixture : IAsyncLifetime, IDisposable
{
    public const string AuthQueue = "core-auth";
    public const string CriticalQueue = "core-critical";
    public const string TransactionalQueue = "core-transactional";
    public const string OperationalQueue = "core-operational";

    private static readonly string[] Queues =
        [AuthQueue, CriticalQueue, TransactionalQueue, OperationalQueue];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private readonly LocalStackContainer _localStack = new LocalStackBuilder()
        .WithImage("localstack/localstack:4.4")
        .Build();

    private AmazonSQSClient? _sqs;

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string SqsEndpoint => _localStack.GetConnectionString();

    /// <summary>Client the tests use to create queues and read messages back.</summary>
    public IAmazonSQS Sqs => _sqs
        ?? throw new InvalidOperationException("O LocalStack ainda não foi iniciado.");

    /// <summary>
    /// A relay composition against the containers, built through the same
    /// public entries the worker host uses. Overrides tune one instance
    /// (bands, batch) without touching the shared containers.
    /// </summary>
    public ServiceProvider BuildRelayProvider(IDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Platform:Messaging:Ef:ConnectionString"] = PostgresConnectionString,
            ["Platform:Messaging:Sqs:ServiceUrl"] = SqsEndpoint,
            ["Platform:Messaging:Sqs:Region"] = "us-east-1",
            ["Platform:Messaging:Sqs:AccessKey"] = "test",
            ["Platform:Messaging:Sqs:SecretKey"] = "test",
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
        services.AddPlatformMessaging(configuration);
        services.AddOutboxRelay(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Appends one row through the production writer contract, in its own committed transaction.</summary>
    public async Task<Guid> AppendOutboxAsync(ServiceProvider provider, OutboxAppend message)
    {
        IOutboxWriter writer = provider.GetRequiredService<IOutboxWriter>();
        await using var connection = new NpgsqlConnection(PostgresConnectionString);
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        Guid id = await writer.AppendAsync(transaction, message, CancellationToken.None);
        await transaction.CommitAsync();
        return id;
    }

    internal static async Task<OutboxRelayPassResult> RunRelayPassAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<OutboxRelay>()
            .RunPassAsync(CancellationToken.None);
    }

    /// <summary>The payload exactly as the jsonb column returns it: the byte-for-byte oracle of the message body.</summary>
    public static async Task<string> StoredPayloadTextAsync(ServiceProvider provider, Guid id)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>()
            .Database
            .SqlQuery<string>(
                $"""SELECT payload::text AS "Value" FROM platform.outbox WHERE id = {id}""")
            .SingleAsync();
    }

    public static async Task<DateTimeOffset?> SentAtAsync(ServiceProvider provider, Guid id)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>()
            .OutboxMessages
            .AsNoTracking()
            .Where(message => message.Id == id)
            .Select(message => message.SentAt)
            .SingleAsync();
    }

    /// <summary>Removes leftover rows so one test's dead destinations never bleed into the next.</summary>
    public static async Task DeleteOutboxRowsAsync(ServiceProvider provider, IReadOnlyList<Guid> ids)
    {
        using IServiceScope scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>()
            .OutboxMessages
            .Where(message => ids.Contains(message.Id))
            .ExecuteDeleteAsync();
    }

    /// <summary>Drains up to <paramref name="expected"/> messages, deleting them so queues stay clean between tests.</summary>
    public async Task<List<Message>> ReceiveAllAsync(string queueName, int expected, TimeSpan budget)
    {
        var queueUrl = (await Sqs.GetQueueUrlAsync(queueName)).QueueUrl;
        var received = new List<Message>();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + budget;
        while (received.Count < expected && DateTimeOffset.UtcNow < deadline)
        {
            ReceiveMessageResponse response = await Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                MessageAttributeNames = ["All"],
            });
            foreach (Message message in response.Messages ?? [])
            {
                received.Add(message);
                await Sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
            }
        }

        return received;
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await Task.WhenAll(_postgres.StartAsync(), _localStack.StartAsync());

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

        DbContextOptions<PlatformMessagingDbContext> options =
            new DbContextOptionsBuilder<PlatformMessagingDbContext>()
                .UseNpgsql(PostgresConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "platform"))
                .Options;
        await using var context = new PlatformMessagingDbContext(options);
        await context.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _localStack.DisposeAsync();
    }

    public void Dispose() => _sqs?.Dispose();
}

[CollectionDefinition(Name)]
public sealed class OutboxRelayCollectionDefinition : ICollectionFixture<OutboxRelayFixture>
{
    public const string Name = "outbox-relay";
}
