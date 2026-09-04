using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.SQS.Model;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.IntegrationTests.Ingress;
using NotificationHub.IntegrationTests.Messaging;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;
using Npgsql;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

[Collection(SentinelSurfaceFixtureGroup.Name)]
public sealed class SentinelSurfaceScanTests(
    KafkaIngressFixture ingressFixture,
    OutboxRelayFixture queueFixture)
{
    internal const string ContentSentinel = "attachment-content-45c80774e3744f5fb75bb942bc765813";
    private const string NameSentinel = "attachment-name-55017beef7064df89fc8b5e8b7d76275.bin";
    private const string OutgoingTopic = "notifications.events.v1";

    internal static readonly string[] ExpectedSurfaces =
        ["audit", "broker", "dead-letter", "log", "outbox", "queue", "response"];

    private static readonly string[] Sentinels = [ContentSentinel, NameSentinel];

    private static readonly TimeSpan BrokerReadBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QueueReadBudget = TimeSpan.FromSeconds(15);

    [RequiresDockerFact]
    public async Task The_current_transport_and_observability_surfaces_do_not_expose_attachment_sentinels()
    {
        await EnsureOutgoingTopicAsync();

        var restRecipient = $"cus_{Guid.NewGuid():N}";
        var restIdempotencyKey = $"sentinel-rest-{Guid.NewGuid():N}";
        var restLogs = new SentinelLogCaptureProvider();
        using WebApplicationFactory<Program> restHost = ingressFixture.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(restLogs)));
        using HttpClient credentialSource = ingressFixture.CreateProducerClient(
            "sentinel-rest-producer", NotificationsApi.SendTransactional);
        using HttpClient producer = restHost.CreateClient();
        producer.DefaultRequestHeaders.Authorization = credentialSource.DefaultRequestHeaders.Authorization;
        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(
                $"missing-{Guid.NewGuid():N}",
                recipientId: restRecipient,
                variables: new
                {
                    content = ContentSentinel,
                    fileName = NameSentinel,
                }),
            restIdempotencyKey);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var responseBody = await response.Content.ReadAsStringAsync();

        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) = await KafkaIngressApi.CreatePublishedTemplateAsync(
            ingressFixture,
            application,
            "transactional",
            sensitiveVariables: ["code"]);
        await ingressFixture.SeedProducerGrantsAsync(
            (KafkaIngressFixture.RequestedProducer, application, "transactional"));

        var busRecipient = $"cus_{Guid.NewGuid():N}";
        var busIdempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var kafkaIngressLogs = new SentinelLogCaptureProvider();
        await using ServiceProvider ingressProvider = ingressFixture.BuildIngressProvider(
            replaceServices: services => services.AddLogging(logging => logging.AddProvider(kafkaIngressLogs)));
        KafkaDisposition disposition = await IngressRecords.ProcessAsync(
            ingressFixture,
            ingressProvider,
            busRecipient,
            KafkaIngressApi.RequestedEvent(
                application,
                templateKey,
                "transactional",
                busRecipient,
                busIdempotencyKey,
                new KafkaIngressApi.RequestedEventOptions
                {
                    Variables = new { code = $"{ContentSentinel}|{NameSentinel}" },
                }),
            KafkaIngressApi.ProducerHeaders(KafkaIngressFixture.RequestedProducer));
        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe("sensitive-variables-on-bus");

        ConsumeResult<string, byte[]> deadLetter = ingressFixture
            .ReadAll(KafkaIngressFixture.DeadLetterTopic, BrokerReadBudget)
            .Single(record => IngressRecords.Header(record, "idempotencyKey") == busIdempotencyKey);

        var restEntityId = $"{NotificationsApi.Application}:{restIdempotencyKey}";
        var busEntityId = $"{application}:{busIdempotencyKey}";
        List<AuditEvent> auditEvents = await ingressFixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == restEntityId || entry.EntityId == busEntityId)
            .ToListAsync());
        auditEvents.Count.ShouldBe(2);

        List<OutboxMessage> busOutbox = await ingressFixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.Transport == OutboxTransports.Kafka
                && (message.MessageKey == restRecipient || message.MessageKey == busRecipient))
            .ToListAsync());
        busOutbox.Count.ShouldBe(2);

        var kafkaRelayLogs = new SentinelLogCaptureProvider();
        await using ServiceProvider kafkaRelay = BuildKafkaRelayProvider(kafkaRelayLogs);
        OutboxAppend missingKafkaTopic = OutboxEnvelopes.Envelope(
            "invalid sentinel relay topic",
            "operational") with
        {
            Transport = OutboxTransports.Kafka,
        };
        Guid missingKafkaOutboxId = await AppendOutboxAsync(
            kafkaRelay,
            ingressFixture.PostgresConnectionString,
            missingKafkaTopic);
        try
        {
            OutboxRelayPassResult kafkaPass = await RunRelayPassAsync(kafkaRelay);
            kafkaPass.Failed.ShouldBe(1);
            kafkaPass.Published.ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await OutboxRelayFixture.DeleteOutboxRowsAsync(kafkaRelay, [missingKafkaOutboxId]);
        }
        var brokerRecords = ingressFixture
            .ReadAll(OutgoingTopic, BrokerReadBudget)
            .Where(record => record.Message.Key == restRecipient || record.Message.Key == busRecipient)
            .ToList();
        brokerRecords.Count.ShouldBe(2);

        var sqsRelayLogs = new SentinelLogCaptureProvider();
        await using ServiceProvider queueRelay = BuildSqsRelayProvider(sqsRelayLogs);
        OutboxAppend queueAppend = OutboxEnvelopes.Envelope(
            OutboxRelayFixture.OperationalQueue,
            "operational");
        Guid queueOutboxId = await queueFixture.AppendOutboxAsync(queueRelay, queueAppend);
        OutboxAppend missingQueue = OutboxEnvelopes.Envelope(
            $"missing-sentinel-queue-{Guid.NewGuid():N}",
            "operational");
        Guid missingQueueOutboxId = await queueFixture.AppendOutboxAsync(queueRelay, missingQueue);
        var storedQueuePayload = await OutboxRelayFixture.StoredPayloadTextAsync(
            queueRelay,
            queueOutboxId);
        try
        {
            OutboxRelayPassResult queuePass = await OutboxRelayFixture.RunRelayPassAsync(queueRelay);
            queuePass.Failed.ShouldBe(1);
            queuePass.Published.ShouldBe(1);
        }
        finally
        {
            await OutboxRelayFixture.DeleteOutboxRowsAsync(queueRelay, [missingQueueOutboxId]);
        }
        List<Message> queued = await queueFixture.ReceiveAllAsync(
            OutboxRelayFixture.OperationalQueue,
            expected: 1,
            QueueReadBudget);
        Message queueMessage = queued.ShouldHaveSingleItem();

        var captures = new SurfaceCapture[]
        {
            new("audit", AuditFragments(auditEvents)),
            new("broker", KafkaFragments(brokerRecords)),
            new("dead-letter", KafkaFragments([deadLetter])),
            new("log", LogFragments(
                ("rest", restLogs),
                ("kafka-ingress", kafkaIngressLogs),
                ("kafka-relay", kafkaRelayLogs),
                ("sqs-relay", sqsRelayLogs))),
            new("outbox", [.. OutboxFragments(busOutbox), storedQueuePayload]),
            new("queue", SqsFragments(queueMessage)),
            new("response", ResponseFragments(response, responseBody)),
        };

        AssertNoSentinels(captures);
    }

    [RequiresDockerFact]
    public async Task A_sentinel_relayed_through_the_real_outbox_and_queue_makes_the_scan_fail()
    {
        var sqsRelayLogs = new SentinelLogCaptureProvider();
        await using ServiceProvider relay = BuildSqsRelayProvider(sqsRelayLogs);
        OutboxAppend append = OutboxEnvelopes.Envelope(
            OutboxRelayFixture.CriticalQueue,
            "critical") with
        {
            PayloadJson = JsonSerializer.Serialize(new
            {
                content = ContentSentinel,
                fileName = NameSentinel,
            }),
        };
        await queueFixture.AppendOutboxAsync(relay, append);

        OutboxRelayPassResult pass = await OutboxRelayFixture.RunRelayPassAsync(relay);
        pass.Failed.ShouldBe(0);
        pass.Published.ShouldBe(1);
        Message queued = (await queueFixture.ReceiveAllAsync(
            OutboxRelayFixture.CriticalQueue,
            expected: 1,
            QueueReadBudget)).ShouldHaveSingleItem();
        queued.Body.ShouldContain(ContentSentinel);

        SurfaceCapture[] captures = CapturesWith(
            new SurfaceCapture("queue", SqsFragments(queued)));

        ShouldAssertException failure = Should.Throw<ShouldAssertException>(
            () => AssertNoSentinels(captures));

        failure.Message.ShouldContain("queue");
        failure.Message.ShouldContain(ContentSentinel);
    }

    [Fact]
    public void Sentinels_emitted_before_log_capture_are_rejected_in_every_captured_log_part()
    {
        AssertCausalLogLeak(
            logger => logger.Log(
                LogLevel.Information,
                new EventId(9100),
                "clean-state",
                null,
                (_, _) => ContentSentinel),
            ContentSentinel);

        AssertCausalLogLeak(
            logger =>
            {
                var state = new Dictionary<string, object?> { ["attachment"] = ContentSentinel };
                logger.Log(
                    LogLevel.Information,
                    new EventId(9101),
                    state,
                    null,
                    static (_, _) => "clean formatted message");
            },
            ContentSentinel);

        AssertCausalLogLeak(
            logger =>
            {
                using IDisposable? outer = logger.BeginScope(
                    new Dictionary<string, object?> { ["attachmentScope"] = NameSentinel });
                using IDisposable? inner = logger.BeginScope("clean-inner-scope");
                logger.Log(
                    LogLevel.Information,
                    new EventId(9102),
                    "clean-state",
                    null,
                    static (_, _) => "clean formatted message");
            },
            NameSentinel,
            expectedScopeCount: 2);

        AssertCausalLogLeak(
            logger => logger.Log(
                LogLevel.Error,
                new EventId(9103),
                "clean-state",
                new InvalidOperationException(NameSentinel),
                static (_, _) => "clean formatted message"),
            NameSentinel);
    }

    private async Task EnsureOutgoingTopicAsync()
    {
        using IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = ingressFixture.BootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification
                {
                    Name = OutgoingTopic,
                    NumPartitions = 1,
                    ReplicationFactor = 1,
                },
            ]);
        }
        catch (CreateTopicsException exception) when (
            exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
        }
    }

    private ServiceProvider BuildKafkaRelayProvider(ILoggerProvider loggerProvider)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:Messaging:Ef:ConnectionString"] = ingressFixture.PostgresConnectionString,
                ["Platform:Messaging:Kafka:BootstrapServers"] = ingressFixture.BootstrapServers,
                ["Platform:Messaging:Kafka:DeliveryTimeoutMilliseconds"] = "5000",
                ["Platform:Messaging:Kafka:FlushTimeoutMilliseconds"] = "5000",
                ["Platform:Messaging:Relay:Transports:0"] = OutboxTransports.Kafka,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(loggerProvider));
        services.AddPlatformMessaging(configuration);
        services.AddOutboxRelay(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private ServiceProvider BuildSqsRelayProvider(ILoggerProvider loggerProvider)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:Messaging:Ef:ConnectionString"] = queueFixture.PostgresConnectionString,
                ["Platform:Messaging:Sqs:ServiceUrl"] = queueFixture.SqsEndpoint,
                ["Platform:Messaging:Sqs:Region"] = "us-east-1",
                ["Platform:Messaging:Sqs:AccessKey"] = "test",
                ["Platform:Messaging:Sqs:SecretKey"] = "test",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(loggerProvider));
        services.AddPlatformMessaging(configuration);
        services.AddOutboxRelay(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<Guid> AppendOutboxAsync(
        ServiceProvider provider,
        string connectionString,
        OutboxAppend message)
    {
        IOutboxWriter writer = provider.GetRequiredService<IOutboxWriter>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        Guid id = await writer.AppendAsync(transaction, message, CancellationToken.None);
        await transaction.CommitAsync();
        return id;
    }

    private static async Task<OutboxRelayPassResult> RunRelayPassAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<OutboxRelay>()
            .RunPassAsync(CancellationToken.None);
    }

    private static string[] AuditFragments(IEnumerable<AuditEvent> events)
        => events.SelectMany(entry => new[]
        {
            entry.ActorType,
            entry.ActorId,
            entry.Application ?? string.Empty,
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            entry.DetailsJson,
            entry.Canonical ?? string.Empty,
        }).ToArray();

    private static string[] KafkaFragments(IEnumerable<ConsumeResult<string, byte[]>> records)
        => records.SelectMany(record =>
        {
            IEnumerable<string> headers = record.Message.Headers?.Select(header =>
                $"{header.Key}:{Encoding.UTF8.GetString(header.GetValueBytes())}") ?? [];
            return new[]
            {
                record.Message.Key ?? string.Empty,
                Encoding.UTF8.GetString(record.Message.Value ?? []),
            }.Concat(headers);
        }).ToArray();

    private static string[] OutboxFragments(IEnumerable<OutboxMessage> messages)
        => messages.SelectMany(message => new[]
        {
            message.Destination,
            message.Transport,
            message.EventType,
            message.MessageKey,
            message.HeadersJson,
            message.PayloadJson,
            message.PriorityClass,
        }).ToArray();

    private static string[] SqsFragments(Message message)
        =>
        [
            message.Body,
            message.MessageId,
            .. message.MessageAttributes.SelectMany(attribute => new[]
            {
                attribute.Key,
                attribute.Value.StringValue ?? string.Empty,
            }),
        ];

    private static string[] ResponseFragments(HttpResponseMessage response, string body)
        =>
        [
            body,
            .. response.Headers.SelectMany(header => header.Value.Prepend(header.Key)),
            .. response.Content.Headers.SelectMany(header => header.Value.Prepend(header.Key)),
        ];

    private static string[] LogFragments(
        params (string Flow, SentinelLogCaptureProvider Provider)[] observedFlows)
        => observedFlows.SelectMany(observed =>
        {
            observed.Provider.Events.ShouldNotBeEmpty(
                $"the '{observed.Flow}' flow must contribute real log events.");
            return observed.Provider.Events.SelectMany(log => LogEventFragments(observed.Flow, log));
        }).ToArray();

    private static IEnumerable<string> LogEventFragments(string flow, SentinelCapturedLogEvent log)
    {
        yield return $"{flow}:message:{log.Message}";
        foreach (SentinelCapturedLogValue value in log.State)
        {
            yield return $"{flow}:state-key:{value.Key}";
            yield return $"{flow}:state-value:{value.Value}";
        }

        if (log.Exception is not null)
        {
            yield return $"{flow}:exception:{log.Exception}";
        }

        foreach (SentinelCapturedLogScope scope in log.Scopes)
        {
            yield return $"{flow}:scope:{scope.Formatted}";
            foreach (SentinelCapturedLogValue value in scope.State)
            {
                yield return $"{flow}:scope-state-key:{value.Key}";
                yield return $"{flow}:scope-state-value:{value.Value}";
            }
        }
    }

    private static void AssertCausalLogLeak(
        Action<ILogger> emit,
        string sentinel,
        int expectedScopeCount = 0)
    {
        var provider = new SentinelLogCaptureProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(provider);
        });
        ILogger logger = loggerFactory.CreateLogger("sentinel-log-falsification");

        emit(logger);

        provider.Events.ShouldHaveSingleItem().Scopes.Count.ShouldBe(expectedScopeCount);
        SurfaceCapture[] captures = CapturesWith(
            new SurfaceCapture("log", LogFragments(("causal-log", provider))));
        ShouldAssertException failure = Should.Throw<ShouldAssertException>(
            () => AssertNoSentinels(captures));
        failure.Message.ShouldContain("log");
        failure.Message.ShouldContain(sentinel);
    }

    private static SurfaceCapture[] CapturesWith(SurfaceCapture observed)
    {
        SurfaceCapture[] captures = ExpectedSurfaces
            .Select(surface => new SurfaceCapture(surface, ["clean-surface-value"]))
            .ToArray();
        captures[Array.IndexOf(ExpectedSurfaces, observed.Name)] = observed;
        return captures;
    }

    internal static void AssertNoSentinels(IReadOnlyCollection<SurfaceCapture> captures)
    {
        captures.Select(capture => capture.Name)
            .Order(StringComparer.Ordinal)
            .ShouldBe(ExpectedSurfaces);

        foreach (SurfaceCapture capture in captures)
        {
            capture.Fragments.ShouldNotBeEmpty(
                $"a superfície '{capture.Name}' precisa ser observada por conteúdo real.");
            foreach (var fragment in capture.Fragments)
            {
                foreach (var sentinel in Sentinels)
                {
                    fragment.Contains(sentinel, StringComparison.Ordinal).ShouldBeFalse(
                        $"a superfície '{capture.Name}' contém a sentinela '{sentinel}'.");
                }
            }
        }
    }

    internal sealed record SurfaceCapture(string Name, IReadOnlyList<string> Fragments);
}

internal sealed record SentinelCapturedLogValue(string Key, string Value);

internal sealed record SentinelCapturedLogScope(
    string Formatted,
    IReadOnlyList<SentinelCapturedLogValue> State);

internal sealed record SentinelCapturedLogEvent(
    string Message,
    IReadOnlyList<SentinelCapturedLogValue> State,
    string? Exception,
    IReadOnlyList<SentinelCapturedLogScope> Scopes);

internal sealed class SentinelLogCaptureProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    internal ConcurrentQueue<SentinelCapturedLogEvent> Events { get; } = new();

    public ILogger CreateLogger(string categoryName) => new SentinelCaptureLogger(this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        => _scopeProvider = scopeProvider;

    public void Dispose()
    {
    }

    private static SentinelCapturedLogValue[] CaptureState(object? state)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> structured)
        {
            return structured
                .Select(value => new SentinelCapturedLogValue(value.Key, Format(value.Value)))
                .ToArray();
        }

        return [new SentinelCapturedLogValue("$state", Format(state))];
    }

    private static SentinelCapturedLogScope CaptureScope(object? scope)
        => new(Format(scope), CaptureState(scope));

    private static string Format(object? value)
        => value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private sealed class SentinelCaptureLogger(SentinelLogCaptureProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => provider._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<SentinelCapturedLogScope>();
            provider._scopeProvider.ForEachScope(
                static (scope, captured) => captured.Add(CaptureScope(scope)),
                scopes);
            provider.Events.Enqueue(new SentinelCapturedLogEvent(
                formatter(state, exception),
                CaptureState(state),
                exception?.ToString(),
                scopes));
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SentinelSurfaceFixtureGroup :
    ICollectionFixture<KafkaIngressFixture>,
    ICollectionFixture<OutboxRelayFixture>
{
    public const string Name = "attachment-sentinel-surfaces";
}
