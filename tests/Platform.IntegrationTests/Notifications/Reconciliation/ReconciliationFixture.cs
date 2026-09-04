using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;
using NotificationHub.Api.Modules.Notifications;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Notifications.Reconciliation;

/// <summary>
/// Environment of the delivery reconciliation: a Postgres container of its
/// own, the schemas the job touches, the double every provider call lands on,
/// and the <c>notifications-maintenance</c> role composed exactly as the
/// worker host composes it.
/// <para>
/// A database of its own for the same reason the scheduler's suite keeps one.
/// The job reads the whole table: every attempt a provider ever accepted and
/// never reported on. Run against a shared database with a clock this suite
/// moves forward, it would ask providers about rows other tests wrote and
/// settle attempts nobody here created.
/// </para>
/// <para>
/// No Redis container, deliberately. The role composes the contact directory
/// for exactly one read, the reveal of a destination, and that read is never
/// cached; the module's multiplexer is lazy, so the configured address is
/// present and never dialled.
/// </para>
/// </summary>
public sealed class ReconciliationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private readonly string _envelopeMasterKey =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private ServiceProvider? _services;
    private FakeProviderServer? _provider;

    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>
    /// The clock the job reads. Moving it is how this suite reaches the
    /// staleness window without waiting six hours, and it is what makes the
    /// eligibility assertions exact instead of approximately timed.
    /// </summary>
    public ReconciliationClock Clock { get; } = new(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The double both provider lookups call.</summary>
    public FakeProviderServer Provider => _provider
        ?? throw new InvalidOperationException("O servidor falso de provedor ainda não foi iniciado.");

    public ServiceProvider Services => _services
        ?? throw new InvalidOperationException("O ambiente da reconciliação ainda não foi iniciado.");

    /// <summary>One round of the reconciliation, through the composed role.</summary>
    public async Task<ReconciliationRoundView> RunRoundAsync()
    {
        using IServiceScope scope = Services.CreateScope();
        DeliveryReconciliationResult result = await scope.ServiceProvider
            .GetRequiredService<DeliveryReconciliationScan>()
            .RunAsync(CancellationToken.None);
        return new ReconciliationRoundView(
            result.Examined,
            result.Queried,
            result.Corrected,
            result.WithoutLookup,
            result.LiabilityRetired);
    }

    /// <summary>One round of the index retirement alone, for the measurement of it.</summary>
    public async Task<int> RunLiabilitySweepAsync()
    {
        using IServiceScope scope = Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<ScanIndexLiabilitySweep>()
            .RunAsync(CancellationToken.None);
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

    /// <summary>
    /// Writes one dispatched notification with one attempt in the state the
    /// case needs. Rows rather than a pipeline run, like the scheduler's suite:
    /// what is under test is which rows the job picks and what it does with the
    /// provider's answer, and driving the whole pipeline would spend minutes
    /// proving the pipeline again.
    /// </summary>
    public async Task<SeededReconciliationAttempt> SeedAttemptAsync(ReconciliationSeed seed)
    {
        Guid notificationId = Guid.Empty;
        Guid attemptId = Guid.Empty;
        var recipientId = seed.RecipientId ?? $"cus_{Guid.NewGuid():N}";
        await ExecuteNotificationsDbAsync(async db =>
        {
            var notification = Notification.Accept(new NotificationDraft
            {
                Application = seed.Application,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                RecipientId = recipientId,
                Class = seed.Class,
                TemplateKey = "tpl-reconciliation",
                TemplateVersion = 1,
                VariablesMaskedJson = "{}",
                RequestedBy = "reconciliation-tests",
                TtlSeconds = 86_400,
                AcceptedAt = seed.CreatedAt,
            });
            notification.MarkDispatched(policyVersion: 1, AdmittedDeliveryPlan.Serialize(
                [
                    new DeliveryPlanStep(Channel.Create("push").Value!, TimeSpan.FromSeconds(30)),
                    new DeliveryPlanStep(Channel.Create("email").Value!, null),
                ]));
            if (seed.NotificationStatus == NotificationStatuses.Failed) notification.MarkFailedAfterDispatch();
            if (seed.NotificationStatus == NotificationStatuses.Expired) notification.MarkExpiredAfterDispatch();

            db.Notifications.Add(notification);

            var attempt = NotificationAttempt.Queue(new NotificationAttemptDraft
            {
                NotificationId = notification.Id,
                Sequence = 1,
                Channel = seed.Channel,
                ContactPointId = seed.ContactPointId,
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
                    .SetProperty(candidate => candidate.ProviderKey, seed.ProviderKey)
                    .SetProperty(candidate => candidate.ProviderMessageId, seed.ProviderMessageId)
                    .SetProperty(candidate => candidate.ErrorCode, seed.ErrorCode)
                    .SetProperty(candidate => candidate.SentAt, seed.SentAt)
                    .SetProperty(candidate => candidate.StatusChangedAt, seed.StatusChangedAt));

            notificationId = notification.Id;
            attemptId = attempt.Id;
        });

        return new SeededReconciliationAttempt(notificationId, attemptId, recipientId);
    }

    /// <summary>
    /// Writes one recipient with one contact point whose stored value really
    /// decrypts to <paramref name="value"/>, through the module's own
    /// protector. A stub ciphertext would make the reveal fail and hide
    /// whatever the destination route does with a real one.
    /// </summary>
    public async Task<Guid> SeedContactPointAsync(string recipientId, string channel, string value)
    {
        using IServiceScope scope = Services.CreateScope();
        ProtectedContactValue protectedValue = await scope.ServiceProvider
            .GetRequiredService<ContactValueProtector>()
            .ProtectAsync(value, CancellationToken.None);

        ContactConsentDbContext db = scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>();
        if (!await db.RecipientProfiles.AnyAsync(profile => profile.RecipientId == recipientId))
        {
            db.RecipientProfiles.Add(
                RecipientProfile.Create(recipientId, null, null, Clock.GetUtcNow()));
        }

        var point = ContactPoint.Declare(
            recipientId, channel, protectedValue.Encrypted, protectedValue.Hash, verified: true);
        db.ContactPoints.Add(point);
        await db.SaveChangesAsync();
        return point.Id;
    }

    /// <summary>Status of one attempt, read back from the store.</summary>
    public async Task<AttemptStateView> AttemptStateAsync(Guid attemptId)
        => await QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.Id == attemptId)
            .Select(attempt => new AttemptStateView(
                attempt.Status,
                attempt.ErrorCode,
                attempt.DeliveredAt,
                attempt.PlanAdvancedAt,
                attempt.ProviderMessageId))
            .SingleAsync());

    /// <summary>Trail entries of one action for one notification.</summary>
    public async Task<int> CountTrailAsync(Guid notificationId, string action)
        => await QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == action && entry.EntityId == notificationId.ToString()));

    /// <summary>Details of the trail entries of one action, as stored.</summary>
    public async Task<IReadOnlyList<string>> TrailDetailsAsync(Guid notificationId)
        => await QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == notificationId.ToString())
            .Select(entry => entry.DetailsJson)
            .ToListAsync());

    /// <summary>How many evidence rows this hub holds for one provider event.</summary>
    public async Task<int> CountEvidenceAsync(string providerEventId)
        => await QueryNotificationsDbAsync(db => db.DeliveryEvents
            .AsNoTracking()
            .CountAsync(evidence => evidence.ProviderEventId == providerEventId));

    /// <summary>Rows still sitting in the three partial indexes the scheduler reads.</summary>
    public async Task<int> CountParkedInScanIndexAsync()
        => await QueryNotificationsDbAsync(db => db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value"
                FROM notifications.notification_attempt
                WHERE fallback_deadline IS NOT NULL
                  AND plan_advanced_at IS NULL
                  AND fallback_requested_at IS NULL
                """)
            .SingleAsync());

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable) return;

        await _postgres.StartAsync();
        _provider = await FakeProviderServer.StartAsync();
        _services = BuildProvider(null, null);

        using IServiceScope scope = _services.CreateScope();

        // TemplateManagement first, like every other fixture: its history
        // creates the trail tables the Audit adoption migration takes over.
        await scope.ServiceProvider.GetRequiredService<TemplateManagementDbContext>()
            .Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ContactConsentDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<PlatformMessagingDbContext>()
            .Database.MigrateAsync();
        await EnsureClockPartitionsAsync(scope.ServiceProvider);
    }

    /// <summary>
    /// Guarantees the monthly partitions the clock of this suite writes into.
    /// <para>
    /// The migrations provision from the wall clock of whatever runs them,
    /// the current month and two ahead, which is what a deployment needs and
    /// never what a suite with a clock of its own needs. This suite reads a
    /// fixed instant, so that the staleness window is reached by moving the
    /// clock instead of by waiting six hours, and every row it seeds carries
    /// that instant. The two agree only while the fixed instant happens to
    /// fall inside the month the suite runs in, and the day they stop
    /// agreeing every seed dies with no partition found for the row.
    /// </para>
    /// <para>
    /// The month before and the month after are provisioned along with it
    /// because a seed reaches back hours and days from the fixed instant, and
    /// a fixed instant near either edge of a month would otherwise put those
    /// rows outside the one window this arrangement created.
    /// </para>
    /// </summary>
    private async Task EnsureClockPartitionsAsync(IServiceProvider serviceProvider)
    {
        DateTime anchor = Clock.GetUtcNow().UtcDateTime;
        NotificationsDbContext notifications = serviceProvider
            .GetRequiredService<NotificationsDbContext>();
        AuditDbContext audit = serviceProvider.GetRequiredService<AuditDbContext>();
        for (var offset = -1; offset <= 1; offset++)
        {
            DateOnly from = new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(offset);
            foreach (var table in NotificationsPartitionManagerService.PartitionedTables)
            {
                await notifications.Database.ExecuteSqlRawAsync(
                    PartitionSql("notifications", table, from));
            }

            await audit.Database.ExecuteSqlRawAsync(PartitionSql("audit", "audit_event", from));
        }
    }

    private static string PartitionSql(string schema, string table, DateOnly from)
    {
        DateOnly to = from.AddMonths(1);
        return $"""
            CREATE TABLE IF NOT EXISTS {schema}."{table}_{from.Year:D4}_{from.Month:D2}"
            PARTITION OF {schema}."{table}"
            FOR VALUES FROM ('{from:yyyy-MM-dd} 00:00:00+00') TO ('{to:yyyy-MM-dd} 00:00:00+00')
            """;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();

        if (_provider is not null) await _provider.DisposeAsync();

        await _postgres.DisposeAsync();
    }

    /// <summary>The role composed with one setting overridden, and its own log sink.</summary>
    public ServiceProvider BuildRoleWith(
        IDictionary<string, string?>? overrides = null,
        ILoggerProvider? loggerProvider = null)
        => BuildProvider(overrides, loggerProvider);

    private ServiceProvider BuildProvider(
        IDictionary<string, string?>? overrides,
        ILoggerProvider? loggerProvider)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Platform:Messaging:Ef:ConnectionString"] = ConnectionString,
            ["Platform:Messaging:Sqs:ServiceUrl"] = "http://localhost:4566",
            ["Platform:Messaging:Sqs:Region"] = "us-east-1",
            ["Platform:Messaging:Sqs:AccessKey"] = "test",
            ["Platform:Messaging:Sqs:SecretKey"] = "test",
            ["Platform:Cryptography:Envelope:KeyId"] = "integration-tests-envelope",
            ["Platform:Cryptography:Envelope:MasterKey"] = _envelopeMasterKey,
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = ConnectionString,
            ["Modules:Notifications:Redis:ConnectionString"] = "localhost:6379",
            ["Modules:Audit:Persistence:Ef:ConnectionString"] = ConnectionString,
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = ConnectionString,
            ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] = ConnectionString,
            ["Modules:ContactConsent:Redis:ConnectionString"] = "localhost:6379",

            // Both lookups answer at the double, and neither of them may ever
            // reach a real provider from a test host.
            ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = Provider.BaseAddress.ToString(),
            ["Modules:Dispatch:Providers:SendGrid:ApiKey"] = "sg-integration-key",
            ["Modules:Dispatch:Providers:Twilio:BaseAddress"] = Provider.BaseAddress.ToString(),
            ["Modules:Dispatch:Providers:Twilio:AccountSid"] = "AC-integration",
            ["Modules:Dispatch:Providers:Twilio:CredentialSecret"] = "twilio-integration-secret",

            // The rear-guard jobs this role also owns stay off: they rewrite
            // stored ciphertext and would touch rows this suite seeded for
            // other reasons.
            ["Modules:Notifications:RenderedContentRetention:Enabled"] = "false",
            ["Modules:Notifications:RenderedContentBackfill:Enabled"] = "false",
        };
        if (overrides is not null)
            foreach ((var key, var value) in overrides) settings[key] = value;

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (loggerProvider is not null) logging.AddProvider(loggerProvider);
        });
        NotificationsMaintenanceWorkerRole.ConfigureServices(services, configuration);

        // Registered after the role on purpose: the role registers the system
        // clock defensively, and the last registration is the one resolved.
        services.AddSingleton<TimeProvider>(Clock);
        services.AddAuditPersistence(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}

/// <summary>Clock the job reads; the tests move it instead of waiting six hours.</summary>
public sealed class ReconciliationClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>Counts of one reconciliation round, exposed without the internal record.</summary>
public sealed record ReconciliationRoundView(
    int Examined,
    int Queried,
    int Corrected,
    int WithoutLookup,
    int LiabilityRetired);

/// <summary>What one seeded attempt looks like from the test's side.</summary>
public sealed record SeededReconciliationAttempt(Guid NotificationId, Guid AttemptId, string RecipientId);

/// <summary>The stored state of one attempt, as the assertions read it.</summary>
public sealed record AttemptStateView(
    string Status,
    string? ErrorCode,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? PlanAdvancedAt,
    string? ProviderMessageId);

/// <summary>Inputs of one seeded attempt.</summary>
public sealed record ReconciliationSeed
{
    public required string Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string Class { get; init; } = "transactional";

    public string Channel { get; init; } = "email";

    public string Application { get; init; } = "app-reconciliation";

    public string NotificationStatus { get; init; } = NotificationStatuses.Dispatched;

    public string? ProviderKey { get; init; } = "sendgrid";

    public string? ProviderMessageId { get; init; }

    public string? ErrorCode { get; init; }

    public string? RecipientId { get; init; }

    public Guid? ContactPointId { get; init; }

    public DateTimeOffset? SentAt { get; init; }

    public DateTimeOffset? StatusChangedAt { get; init; }

    public DateTimeOffset? FallbackDeadline { get; init; }
}

[CollectionDefinition(Name)]
public sealed class ReconciliationCollectionDefinition : ICollectionFixture<ReconciliationFixture>
{
    public const string Name = "delivery-reconciliation";
}
