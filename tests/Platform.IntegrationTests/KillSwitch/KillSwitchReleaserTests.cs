using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.Audit;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.KillSwitch;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class KillSwitchReleaserTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task Concurrent_releasers_append_one_resume_message()
    {
        SeededHold seeded = await SeedCoreHoldAsync(active: false, expired: false);
        await using ServiceProvider firstProvider = fixture.BuildMaintenanceWorkerProvider();
        await using ServiceProvider secondProvider = fixture.BuildMaintenanceWorkerProvider();
        await using AsyncServiceScope firstScope = firstProvider.CreateAsyncScope();
        await using AsyncServiceScope secondScope = secondProvider.CreateAsyncScope();
        KillSwitchHoldReleaser first = firstScope.ServiceProvider
            .GetRequiredService<KillSwitchHoldReleaser>();
        KillSwitchHoldReleaser second = secondScope.ServiceProvider
            .GetRequiredService<KillSwitchHoldReleaser>();

        var released = await Task.WhenAll(
            first.ReleaseBatchAsync(CancellationToken.None),
            second.ReleaseBatchAsync(CancellationToken.None));

        released.Sum().ShouldBe(1);
        (await ResumeMessageCountAsync(seeded.RecipientId)).ShouldBe(1);
        (await ReleasedAtAsync(seeded.WorkId)).ShouldNotBeNull();
    }

    [RequiresDockerFact]
    public async Task Invalid_claim_is_terminalized_without_aborting_a_later_valid_hold()
    {
        var payloadMarker = $"untrusted-{Guid.NewGuid():N}";
        SeededClaimHold invalid = await SeedClaimHoldAsync(
            JsonSerializer.Serialize(new { unexpected = payloadMarker }),
            DateTimeOffset.UtcNow.AddHours(-1));
        SeededHold valid = await SeedCoreHoldAsync(
            active: false,
            expired: false,
            ttlSeconds: 3_600);
        using var logs = new CapturingLoggerProvider();
        await using ServiceProvider provider = fixture.BuildMaintenanceWorkerProvider(
            loggerProvider: logs);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        KillSwitchHoldReleaser releaser = scope.ServiceProvider
            .GetRequiredService<KillSwitchHoldReleaser>();

        var released = await releaser.ReleaseBatchAsync(CancellationToken.None);

        released.ShouldBeGreaterThanOrEqualTo(1);
        (await ReleasedAtAsync(invalid.WorkId)).ShouldNotBeNull();
        (await ReleasedAtAsync(valid.WorkId)).ShouldNotBeNull();
        (await ResumeMessageCountAsync(valid.RecipientId)).ShouldBe(1);
        var renderedLogs = string.Join(Environment.NewLine, logs.Messages);
        renderedLogs.ShouldContain(invalid.HoldId.ToString());
        renderedLogs.ShouldContain("invalid-claim-payload");
        renderedLogs.ShouldNotContain(payloadMarker);
    }

    [RequiresDockerFact]
    public async Task One_hundred_orphaned_holds_do_not_hide_a_later_valid_hold()
    {
        var orphanPrefix = await SeedOrphanedHoldsAsync(
            count: 100,
            DateTimeOffset.UtcNow.AddHours(-1));
        SeededHold valid = await SeedCoreHoldAsync(
            active: false,
            expired: false,
            ttlSeconds: 3_600);
        await using ServiceProvider provider = fixture.BuildMaintenanceWorkerProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        KillSwitchHoldReleaser releaser = scope.ServiceProvider
            .GetRequiredService<KillSwitchHoldReleaser>();

        var released = await releaser.ReleaseBatchAsync(CancellationToken.None);

        released.ShouldBeGreaterThanOrEqualTo(1);
        (await UnreleasedHoldCountAsync(orphanPrefix)).ShouldBe(0);
        (await ReleasedAtAsync(valid.WorkId)).ShouldNotBeNull();
        (await ResumeMessageCountAsync(valid.RecipientId)).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_resume_blocked_after_release_reopens_one_active_hold()
    {
        SeededHold seeded = await SeedCoreHoldAsync(active: false, expired: false);
        await using ServiceProvider maintenance = fixture.BuildMaintenanceWorkerProvider();
        await using AsyncServiceScope maintenanceScope = maintenance.CreateAsyncScope();
        KillSwitchHoldReleaser releaser = maintenanceScope.ServiceProvider
            .GetRequiredService<KillSwitchHoldReleaser>();

        (await releaser.ReleaseBatchAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);
        await ActivateApplicationSwitchAsync(seeded.Application);
        OutboxMessage resume = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.MessageKey == seeded.RecipientId));
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, resume.Destination))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        await AssertSingleActiveHoldAsync(seeded.WorkId, expectedVersion: 3);

        var queueUrl = (await fixture.Sqs.GetQueueUrlAsync(resume.Destination)).QueueUrl;
        await fixture.Sqs.SendMessageAsync(queueUrl, resume.PayloadJson);
        (await CorePipelineFixture.RunCorePassAsync(core, resume.Destination))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        await AssertSingleActiveHoldAsync(seeded.WorkId, expectedVersion: 3);
        (await ResumeMessageCountAsync(seeded.RecipientId)).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task An_active_switch_keeps_the_hold_without_a_resume_message()
    {
        SeededHold seeded = await SeedCoreHoldAsync(active: true, expired: false);
        await using ServiceProvider provider = fixture.BuildMaintenanceWorkerProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        KillSwitchHoldReleaser releaser = scope.ServiceProvider
            .GetRequiredService<KillSwitchHoldReleaser>();

        await releaser.ReleaseBatchAsync(CancellationToken.None);

        (await ResumeMessageCountAsync(seeded.RecipientId)).ShouldBe(0);
        (await ReleasedAtAsync(seeded.WorkId)).ShouldBeNull();
    }

    [RequiresDockerFact]
    public async Task An_outbox_failure_keeps_the_hold_unreleased()
    {
        SeededHold seeded = await SeedCoreHoldAsync(active: false, expired: false);
        var failingOutbox = new FailingOutboxWriter();
        await using ServiceProvider provider = fixture.BuildMaintenanceWorkerProvider(
            replaceServices: services =>
            {
                services.RemoveAll<IOutboxWriter>();
                services.AddSingleton<IOutboxWriter>(failingOutbox);
            });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        KillSwitchHoldReleaser releaser = scope.ServiceProvider
            .GetRequiredService<KillSwitchHoldReleaser>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => releaser.ReleaseBatchAsync(CancellationToken.None));

        failingOutbox.CallCount.ShouldBe(1);
        (await ReleasedAtAsync(seeded.WorkId)).ShouldBeNull();
        (await ResumeMessageCountAsync(seeded.RecipientId)).ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task An_expired_hold_resumes_through_the_audited_ttl_path_without_calling_a_provider()
    {
        SeededHold seeded = await SeedCoreHoldAsync(active: true, expired: true);
        await using ServiceProvider provider = fixture.BuildMaintenanceWorkerProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        KillSwitchHoldReleaser releaser = scope.ServiceProvider
            .GetRequiredService<KillSwitchHoldReleaser>();

        await releaser.ReleaseBatchAsync(CancellationToken.None);

        OutboxMessage resume = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.MessageKey == seeded.RecipientId));
        resume.Destination.ShouldBe("core-transactional");
        resume.EventType.ShouldBe("notification.accepted");
        using (JsonDocument payload = JsonDocument.Parse(resume.PayloadJson))
        {
            payload.RootElement.GetProperty("payload")
                .GetProperty("notificationId")
                .GetGuid()
                .ShouldBe(seeded.NotificationId);
        }

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, "core-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        (await NotificationStatusAsync(seeded.NotificationId)).ShouldBe(NotificationStatuses.Expired);
        (await ReleasedAtAsync(seeded.WorkId)).ShouldNotBeNull();
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "notification.expired"
                && audit.EntityId == seeded.NotificationId.ToString())))
            .ShouldBe(1);

        await CorePipelineFixture.RunRelayPassAsync(relay);
        var fakeProvider = new TargetCountingProvider(seeded.NotificationId);
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            replaceServices: services =>
            {
                services.RemoveAll<IChannelProviderResolver>();
                services.AddSingleton<IChannelProviderResolver>(
                    new FixedProviderResolver(fakeProvider));
            });
        await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional");
        fakeProvider.TargetCallCount.ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task More_than_one_batch_of_blocked_holds_does_not_hide_an_eligible_hold()
    {
        SeededHold eligible = await SeedCoreHoldAsync(
            active: false,
            expired: false,
            ttlSeconds: 3_600);
        await SeedBlockedHoldsAsync(count: 101, DateTimeOffset.UtcNow.AddMinutes(30));
        await using ServiceProvider provider = fixture.BuildMaintenanceWorkerProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        KillSwitchHoldReleaser releaser = scope.ServiceProvider
            .GetRequiredService<KillSwitchHoldReleaser>();

        var released = await releaser.ReleaseBatchAsync(CancellationToken.None);

        released.ShouldBeLessThanOrEqualTo(100);
        (await ReleasedAtAsync(eligible.WorkId)).ShouldNotBeNull();
    }

    private async Task<SeededHold> SeedCoreHoldAsync(
        bool active,
        bool expired,
        int ttlSeconds = 60)
        => await fixture.UsingScopeAsync(async services =>
        {
            NotificationsDbContext db = services.GetRequiredService<NotificationsDbContext>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset acceptedAt = expired ? now.AddMinutes(-2) : now;
            var notification = Notification.Accept(new NotificationDraft
            {
                Application = $"application-{Guid.NewGuid():N}",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                RecipientId = $"cus_{Guid.NewGuid():N}",
                Class = NotificationClasses.Transactional,
                TemplateKey = $"template-{Guid.NewGuid():N}",
                TemplateVersion = 1,
                VariablesMaskedJson = "{}",
                RequestedBy = "kill-switch-releaser-tests",
                TtlSeconds = ttlSeconds,
                AcceptedAt = acceptedAt,
            });
            db.Notifications.Add(notification);
            if (active)
            {
                db.KillSwitches.Add(KillSwitchState.Activate(
                    KillSwitchScope.Application,
                    notification.Application,
                    "kill-switch-releaser-tests",
                    now));
            }

            await db.SaveChangesAsync();
            var request = new KillSwitchHoldRequest
            {
                WorkKind = KillSwitchWorkKinds.Core,
                WorkId = $"core:{notification.Id:N}",
                Scope = KillSwitchScope.Application,
                Key = notification.Application,
                Destination = "core-transactional",
                PayloadJson = JsonSerializer.Serialize(new { notificationId = notification.Id }),
                ExpiresAt = notification.ExpiresAt,
            };
            KillSwitchHoldWriter writer = new(db);
            await writer.HoldAsync(request, claimedAttemptId: null, CancellationToken.None);
            return new SeededHold(
                notification.Id,
                notification.RecipientId,
                request.WorkId,
                notification.Application);
        });

    private async Task SeedBlockedHoldsAsync(int count, DateTimeOffset expiresAt)
        => await fixture.UsingScopeAsync(async services =>
        {
            NotificationsDbContext db = services.GetRequiredService<NotificationsDbContext>();
            var application = $"blocked-application-{Guid.NewGuid():N}";
            db.KillSwitches.Add(KillSwitchState.Activate(
                KillSwitchScope.Application,
                application,
                "kill-switch-releaser-tests",
                DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            KillSwitchHoldWriter writer = new(db);
            for (var index = 0; index < count; index++)
            {
                var notificationId = Guid.CreateVersion7();
                await writer.HoldAsync(
                    new KillSwitchHoldRequest
                    {
                        WorkKind = KillSwitchWorkKinds.Core,
                        WorkId = $"core:{notificationId:N}",
                        Scope = KillSwitchScope.Application,
                        Key = application,
                        Destination = "core-transactional",
                        PayloadJson = JsonSerializer.Serialize(new { notificationId }),
                        ExpiresAt = expiresAt,
                    },
                    claimedAttemptId: null,
                    CancellationToken.None);
            }

            return true;
        });

    private async Task<SeededClaimHold> SeedClaimHoldAsync(
        string payloadJson,
        DateTimeOffset expiresAt)
        => await fixture.UsingScopeAsync(async services =>
        {
            NotificationsDbContext db = services.GetRequiredService<NotificationsDbContext>();
            var workId = $"invalid-claim:{Guid.NewGuid():N}";
            var request = new KillSwitchHoldRequest
            {
                WorkKind = KillSwitchWorkKinds.Core,
                WorkId = workId,
                Scope = KillSwitchScope.Application,
                Key = $"application-{Guid.NewGuid():N}",
                Destination = "core-transactional",
                PayloadJson = payloadJson,
                ExpiresAt = expiresAt,
            };
            KillSwitchHoldWriter writer = new(db);
            await writer.HoldAsync(request, claimedAttemptId: null, CancellationToken.None);
            Guid holdId = await db.KillSwitchHolds
                .AsNoTracking()
                .Where(hold => hold.WorkKind == request.WorkKind && hold.WorkId == workId)
                .Select(hold => hold.Id)
                .SingleAsync();
            return new SeededClaimHold(holdId, workId);
        });

    private async Task<string> SeedOrphanedHoldsAsync(int count, DateTimeOffset expiresAt)
        => await fixture.UsingScopeAsync(async services =>
        {
            NotificationsDbContext db = services.GetRequiredService<NotificationsDbContext>();
            var workPrefix = $"orphan:{Guid.NewGuid():N}:";
            KillSwitchHoldWriter writer = new(db);
            for (var index = 0; index < count; index++)
            {
                await writer.HoldAsync(
                    new KillSwitchHoldRequest
                    {
                        WorkKind = KillSwitchWorkKinds.Core,
                        WorkId = $"{workPrefix}{index}",
                        Scope = KillSwitchScope.Application,
                        Key = $"application-{Guid.NewGuid():N}",
                        Destination = "core-transactional",
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            notificationId = Guid.CreateVersion7(),
                        }),
                        ExpiresAt = expiresAt,
                    },
                    claimedAttemptId: null,
                    CancellationToken.None);
            }

            return workPrefix;
        });

    private async Task ActivateApplicationSwitchAsync(string application)
        => await fixture.QueryNotificationsDbAsync(async db =>
        {
            db.KillSwitches.Add(KillSwitchState.Activate(
                KillSwitchScope.Application,
                application,
                "kill-switch-releaser-tests",
                DateTimeOffset.UtcNow));
            return await db.SaveChangesAsync();
        });

    private async Task AssertSingleActiveHoldAsync(string workId, long expectedVersion)
    {
        List<KillSwitchHold> holds = await fixture.QueryNotificationsDbAsync(db => db.KillSwitchHolds
            .AsNoTracking()
            .Where(hold => hold.WorkKind == KillSwitchWorkKinds.Core && hold.WorkId == workId)
            .ToListAsync());
        KillSwitchHold hold = holds.ShouldHaveSingleItem();
        hold.ReleasedAt.ShouldBeNull();
        hold.Version.ShouldBe(expectedVersion);
    }

    private async Task<int> ResumeMessageCountAsync(string recipientId)
        => await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId));

    private async Task<DateTimeOffset?> ReleasedAtAsync(string workId)
        => await fixture.QueryNotificationsDbAsync(db => db.KillSwitchHolds
            .AsNoTracking()
            .Where(hold => hold.WorkKind == KillSwitchWorkKinds.Core && hold.WorkId == workId)
            .Select(hold => hold.ReleasedAt)
            .SingleAsync());

    private async Task<int> UnreleasedHoldCountAsync(string workPrefix)
        => await fixture.QueryNotificationsDbAsync(db => db.KillSwitchHolds
            .AsNoTracking()
            .CountAsync(hold => hold.WorkId.StartsWith(workPrefix) && hold.ReleasedAt == null));

    private async Task<string> NotificationStatusAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.Status)
            .SingleAsync());

    private sealed record SeededHold(
        Guid NotificationId,
        string RecipientId,
        string WorkId,
        string Application);

    private sealed record SeededClaimHold(Guid HoldId, string WorkId);

    private sealed class FixedProviderResolver(IChannelProvider provider) : IChannelProviderResolver
    {
        public Task<Result<IChannelProvider>> ResolveAsync(
            Channel channel,
            CancellationToken cancellationToken)
        {
            _ = channel;
            _ = cancellationToken;
            return Task.FromResult(Result.Success(provider));
        }
    }

    private sealed class TargetCountingProvider(Guid targetNotificationId) : IChannelProvider
    {
        private int _targetCallCount;

        public Channel Channel => Channel.Email;

        public string ProviderKey => "fake-email";

        internal int TargetCallCount => _targetCallCount;

        public Task<ProviderResult> SendAsync(
            DispatchRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (request.Correlation?.NotificationId == targetNotificationId)
            {
                Interlocked.Increment(ref _targetCallCount);
            }

            return Task.FromResult(ProviderResult.Accepted("fake-provider-message"));
        }
    }

    private sealed class FailingOutboxWriter : IOutboxWriter
    {
        private int _callCount;

        internal int CallCount => _callCount;

        public Task<Guid> AppendAsync(
            DbTransaction transaction,
            OutboxAppend message,
            CancellationToken cancellationToken)
        {
            _ = transaction;
            _ = message;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            return Task.FromException<Guid>(new InvalidOperationException("outbox unavailable"));
        }
    }
}
