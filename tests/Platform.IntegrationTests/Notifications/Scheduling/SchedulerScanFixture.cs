using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Notifications.Scheduling;

/// <summary>
/// Environment of the scheduler's scans: a Postgres container of its own, the
/// three schemas the scans touch, and the delivery-tracker role composed
/// exactly as the worker host composes it.
/// <para>
/// A container of its own rather than the shared pipeline one, and the reason
/// is the scans themselves. They read the whole table: every attempt still
/// waiting on a deadline, every notification still parked. Run against the
/// shared database they would claim the rows of whatever test ran before,
/// write triggers for notifications no one here created, and leave a neighbour
/// failing on a queue it never touched. An isolated database is what lets the
/// assertions below be about the rows this suite wrote.
/// </para>
/// </summary>
public sealed class SchedulerScanFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private ServiceProvider? _services;

    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>
    /// The clock every scan reads. Moving it is how this suite reaches a
    /// deadline or a grace period without sleeping, and it is what makes the
    /// assertions about elapsed time exact instead of approximately timed.
    /// </summary>
    public MutableClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public async Task<T> UsingScanScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using IServiceScope scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>One round of the two overdue scans, through the composed role.</summary>
    public async Task<OverdueFallbackScanResultView> RunOverdueScanAsync()
        => await UsingScanScopeAsync(async provider =>
        {
            OverdueFallbackScanResult result = await provider
                .GetRequiredService<OverdueFallbackScan>()
                .RunAsync(CancellationToken.None);
            return new OverdueFallbackScanResultView(
                result.DeadlineRequested,
                result.UnknownRequested,
                result.StaleRequestsReleased,
                result.OldestOverdue);
        });

    /// <summary>One round of the release scan, through the composed role.</summary>
    public async Task<int> RunReleaseScanAsync()
        => await UsingScanScopeAsync(async provider =>
        {
            DeferredReleaseScanResult result = await provider
                .GetRequiredService<DeferredReleaseScan>()
                .RunAsync(CancellationToken.None);
            return result.Released;
        });

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

    public async Task<T> QueryPlatformDbAsync<T>(Func<PlatformMessagingDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>());
    }

    public async Task<T> QueryAuditDbAsync<T>(Func<AuditDbContext, Task<T>> query)
    {
        using IServiceScope scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuditDbContext>());
    }

    /// <summary>
    /// Writes one notification already dispatched, with one attempt of the
    /// given channel in the given state. Rows rather than a pipeline run on
    /// purpose: what is under test here is which rows a predicate selects, and
    /// driving four classes through the whole pipeline would spend minutes
    /// proving the pipeline again.
    /// </summary>
    public async Task<SeededAttempt> SeedDispatchedAttemptAsync(AttemptSeed seed)
    {
        Guid notificationId = Guid.Empty;
        Guid attemptId = Guid.Empty;
        await ExecuteNotificationsDbAsync(async db =>
        {
            var notification = Notification.Accept(new NotificationDraft
            {
                Application = seed.Application,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                RecipientId = $"cus_{Guid.NewGuid():N}",
                Class = seed.Class,
                TemplateKey = "tpl-scheduler",
                AuthFlow = seed.AuthFlow,
                TemplateVersion = 1,
                VariablesMaskedJson = "{}",
                RequestedBy = "scheduler-tests",
                TtlSeconds = 3600,
                AcceptedAt = seed.CreatedAt,
            });
            notification.MarkDispatched(policyVersion: 1);
            db.Notifications.Add(notification);

            var attempt = NotificationAttempt.Queue(new NotificationAttemptDraft
            {
                NotificationId = notification.Id,
                Sequence = 1,
                Channel = seed.Channel,
                RenderedContentEncrypted = [1, 2, 3],
                ContentHashFull = new string('a', 64),
                ContentHashMasked = new string('a', 64),
                FallbackDeadline = seed.FallbackDeadline,
                QueuedAt = seed.CreatedAt,
            });
            db.NotificationAttempts.Add(attempt);
            await db.SaveChangesAsync();

            await db.NotificationAttempts
                .Where(candidate => candidate.Id == attempt.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.Status, seed.Status)
                    .SetProperty(candidate => candidate.StatusChangedAt, seed.StatusChangedAt));

            notificationId = notification.Id;
            attemptId = attempt.Id;
        });

        return new SeededAttempt(notificationId, attemptId);
    }

    /// <summary>Writes one notification parked on a release instant.</summary>
    public async Task<Guid> SeedDeferredNotificationAsync(
        string @class,
        DateTimeOffset releaseAt,
        DateTimeOffset? createdAt = null)
    {
        Guid notificationId = Guid.Empty;
        await ExecuteNotificationsDbAsync(async db =>
        {
            var notification = Notification.Accept(new NotificationDraft
            {
                Application = $"app-{Guid.NewGuid():N}",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                RecipientId = $"cus_{Guid.NewGuid():N}",
                Class = @class,
                TemplateKey = "tpl-scheduler",
                TemplateVersion = 1,
                VariablesMaskedJson = "{}",
                RequestedBy = "scheduler-tests",
                TtlSeconds = 86_400,
                AcceptedAt = createdAt ?? DateTimeOffset.UtcNow,
            });
            notification.MarkDeferred(releaseAt, policyVersion: 1);
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();
            notificationId = notification.Id;
        });

        return notificationId;
    }

    /// <summary>Fallback triggers currently sitting in the outbox for one notification.</summary>
    public async Task<int> CountFallbackTriggersAsync(Guid notificationId)
        => await CountOutboxAsync(notificationId, DispatchMessages.FallbackRequestedType);

    /// <summary>
    /// Outbox rows of one type that carry this notification in their claim
    /// check. Read through the JSON operators rather than through a substring
    /// of the serialized payload: the column is jsonb, the database re-writes
    /// it on the way out, and a match against the text of a value the store
    /// reformats is an oracle that grades the formatter.
    /// </summary>
    public async Task<int> CountOutboxAsync(Guid notificationId, string eventType)
        => await QueryPlatformDbAsync(db => db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value" FROM platform.outbox
                WHERE event_type = {eventType}
                  AND payload->'payload'->>'notificationId' = {notificationId.ToString()}
                """)
            .SingleAsync());

    /// <summary>Destination of the fallback trigger written for one notification.</summary>
    public async Task<string?> FallbackDestinationAsync(Guid notificationId)
        => await QueryPlatformDbAsync(db => db.Database
            .SqlQuery<string>(
                $"""
                SELECT destination AS "Value" FROM platform.outbox
                WHERE event_type = {DispatchMessages.FallbackRequestedType}
                  AND payload->'payload'->>'notificationId' = {notificationId.ToString()}
                """)
            .FirstOrDefaultAsync());

    /// <summary>Release destination of the queue message written for one notification.</summary>
    public async Task<string?> ReleaseDestinationAsync(Guid notificationId)
        => await QueryPlatformDbAsync(db => db.Database
            .SqlQuery<string>(
                $"""
                SELECT destination AS "Value" FROM platform.outbox
                WHERE event_type = {CoreMessageProcessor.AcceptedMessageType}
                  AND payload->'payload'->>'notificationId' = {notificationId.ToString()}
                """)
            .FirstOrDefaultAsync());

    /// <summary>Trail entries the release wrote for one notification.</summary>
    public async Task<int> CountReleaseTrailAsync(Guid notificationId)
        => await QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == SchedulerAuditVocabulary.NotificationReleased
                && entry.EntityId == notificationId.ToString()));

    private ServiceProvider Services => _services
        ?? throw new InvalidOperationException("O ambiente do scheduler ainda não foi iniciado.");

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await _postgres.StartAsync();
        _services = BuildProvider(null);

        using IServiceScope scope = _services.CreateScope();

        // TemplateManagement first, like every other fixture: its history
        // creates the trail tables the Audit adoption migration takes over.
        await scope.ServiceProvider.GetRequiredService<TemplateManagementDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>()
            .Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// A second, independent composition over the same database: two providers
    /// are two connection pools, which is what a second replica of this role
    /// really is. Two scopes of one provider would share too much to prove
    /// anything about concurrency.
    /// </summary>
    public ServiceProvider BuildSecondReplica() => BuildProvider(null);

    /// <summary>The role composed with the given settings, for the tests about configuration.</summary>
    public ServiceProvider BuildReplicaWith(IDictionary<string, string?> overrides)
        => BuildProvider(overrides);

    private ServiceProvider BuildProvider(IDictionary<string, string?>? overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Platform:Messaging:Ef:ConnectionString"] = ConnectionString,
            ["Platform:Messaging:Sqs:ServiceUrl"] = "http://localhost:4566",
            ["Platform:Messaging:Sqs:Region"] = "us-east-1",
            ["Platform:Messaging:Sqs:AccessKey"] = "test",
            ["Platform:Messaging:Sqs:SecretKey"] = "test",
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = ConnectionString,
            ["Modules:Audit:Persistence:Ef:ConnectionString"] = ConnectionString,
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = ConnectionString,
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
        DeliveryTrackerWorkerRole.ConfigureServices(services, configuration);

        // Registered after the role on purpose: the role registers the system
        // clock defensively, and the last registration is the one resolved.
        services.AddSingleton<TimeProvider>(Clock);

        // The audit trail of the release writes into the schema this fixture
        // migrated, so the context that owns it has to be resolvable here.
        services.AddAuditPersistence(configuration);
        services.AddEntityFramework(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

/// <summary>Clock the scans read; the tests move it instead of waiting.</summary>
public sealed class MutableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>What one seeded attempt looks like from the test's side.</summary>
public sealed record SeededAttempt(Guid NotificationId, Guid AttemptId);

/// <summary>Counts of one overdue round, exposed without the internal record.</summary>
public sealed record OverdueFallbackScanResultView(
    int DeadlineRequested,
    int UnknownRequested,
    int StaleRequestsReleased,
    TimeSpan? OldestOverdue);

/// <summary>Inputs of one seeded attempt.</summary>
public sealed record AttemptSeed
{
    public required string Class { get; init; }

    public required string Status { get; init; }

    public string Channel { get; init; } = "push";

    public bool AuthFlow { get; init; }

    public string Application { get; init; } = "app-scheduler";

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? FallbackDeadline { get; init; }

    public DateTimeOffset? StatusChangedAt { get; init; }
}

[CollectionDefinition(Name)]
public sealed class SchedulerScanCollectionDefinition : ICollectionFixture<SchedulerScanFixture>
{
    public const string Name = "scheduler-scan";
}
