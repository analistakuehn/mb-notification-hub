using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Infrastructure.Verification;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// The integrity sensor. These tests care about two things: that a clean
/// partition advances the checkpoint instead of replaying everything forever,
/// and that a row altered behind the append-only guarantee is caught, reported
/// in the trail, and visible to whoever watches the host.
/// </summary>
[Collection(AuditMaintenanceCollectionDefinition.Name)]
public sealed class AuditChainVerificationTests(AuditMaintenanceFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_clean_partition_advances_the_checkpoint_and_tolerates_holes_in_the_sequence()
    {
        DateTimeOffset month = MonthOffset(-18);
        MonthlyPartitionWindow window = WindowOf(month);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"verify-clean-1-{Guid.CreateVersion7():N}", month.AddDays(2));

        // An aborted transaction consumes a sequence value: the chain skips
        // the number, and the verification must not read that as damage.
        await fixture.ScalarAsync<long>("SELECT nextval(pg_get_serial_sequence('audit.audit_event', 'seq'))");
        await fixture.AppendAsync($"verify-clean-2-{Guid.CreateVersion7():N}", month.AddDays(3));

        await using ServiceProvider provider = fixture.BuildProvider();
        ChainVerificationOutcome outcome = await VerifyAsync(provider, window);

        outcome.IsIntact.ShouldBeTrue(outcome.Failure);
        outcome.ChainedCount.ShouldBe(2);
        outcome.WasFullReplay.ShouldBeTrue();

        ChainVerificationCheckpoint checkpoint = await CheckpointAsync(provider, window.PartitionName);
        checkpoint.LastSeq.ShouldBe(outcome.ThroughSeq);
        checkpoint.Failure.ShouldBeNull();
        checkpoint.FullyVerifiedAt.ShouldNotBeNull();

        // The next round resumes instead of replaying, and finds nothing new.
        ChainVerificationOutcome second = await VerifyAsync(provider, window);
        second.IsIntact.ShouldBeTrue(second.Failure);
        second.WasFullReplay.ShouldBeFalse();
        second.ChainedCount.ShouldBe(0);

        (await ActionsOfAsync(window.PartitionName)).ShouldAllBe(
            action => action == AuditActions.AuditChainVerified);
    }

    [RequiresDockerFact]
    public async Task An_altered_row_is_caught_reported_in_the_trail_and_degrades_the_health_check()
    {
        DateTimeOffset month = MonthOffset(-19);
        MonthlyPartitionWindow window = WindowOf(month);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"verify-tamper-1-{Guid.CreateVersion7():N}", month.AddDays(2));

        await using ServiceProvider provider = fixture.BuildProvider();
        (await VerifyAsync(provider, window)).IsIntact.ShouldBeTrue();

        // The check reports every partition it found broken, so the assertions
        // here are about this partition and survive the deliberate damage the
        // sibling tests leave behind in theirs.
        (await HealthAsync(provider)).Description
            .ShouldNotBeNull().ShouldNotContain(window.PartitionName);

        var entityId = $"verify-tamper-2-{Guid.CreateVersion7():N}";
        await fixture.AppendAsync(entityId, month.AddDays(4));
        var tamperedSeq = await fixture.ScalarAsync<long>(
            $"SELECT seq FROM audit.audit_event WHERE entity_id = '{entityId}'");

        // Reaching around the append-only trigger the way only a privileged
        // session could. The chain is what makes that visible afterwards.
        await fixture.ExecuteAsync($"""
            SET session_replication_role = replica;
            UPDATE audit.audit_event SET actor_id = 'tampered' WHERE seq = {tamperedSeq};
            SET session_replication_role = DEFAULT;
            """);

        ChainVerificationOutcome outcome = await VerifyAsync(provider, window);

        // The chain covers the canonical text, so an edit beside it would keep
        // every hash valid; the verification compares the row's own columns
        // against that text and names the one that drifted.
        outcome.IsIntact.ShouldBeFalse();
        outcome.BrokenSeq.ShouldBe(tamperedSeq);
        outcome.Failure.ShouldBe("canonical-drift:actor_id");

        (await ActionsOfAsync(window.PartitionName))
            .ShouldContain(AuditActions.AuditChainVerificationFailed);

        HealthCheckResult health = await HealthAsync(provider);
        health.Status.ShouldBe(HealthStatus.Degraded);
        health.Description.ShouldNotBeNull().ShouldContain(window.PartitionName);

        ChainVerificationCheckpoint checkpoint = await CheckpointAsync(provider, window.PartitionName);
        checkpoint.Failure.ShouldBe("canonical-drift:actor_id");
        checkpoint.FailedSeq.ShouldBe(tamperedSeq);
    }

    [RequiresDockerFact]
    public async Task An_edited_canonical_text_breaks_the_link_that_covers_it()
    {
        DateTimeOffset month = MonthOffset(-21);
        MonthlyPartitionWindow window = WindowOf(month);
        await fixture.EnsurePartitionAsync(month);
        var entityId = $"verify-canonical-{Guid.CreateVersion7():N}";
        await fixture.AppendAsync(entityId, month.AddDays(2));
        var tamperedSeq = await fixture.ScalarAsync<long>(
            $"SELECT seq FROM audit.audit_event WHERE entity_id = '{entityId}'");

        await fixture.ExecuteAsync($"""
            SET session_replication_role = replica;
            UPDATE audit.audit_event
            SET canonical = replace(canonical, '"actorId":"audit-maintenance-tests"', '"actorId":"forged"'),
                actor_id = 'forged'
            WHERE seq = {tamperedSeq};
            SET session_replication_role = DEFAULT;
            """);

        await using ServiceProvider provider = fixture.BuildProvider();
        ChainVerificationOutcome outcome = await VerifyAsync(provider, window);

        // The columns and the canonical text agree again, and the hash over
        // those exact bytes is what refuses to reproduce.
        outcome.IsIntact.ShouldBeFalse();
        outcome.Failure.ShouldBe("hash-mismatch");
        outcome.BrokenSeq.ShouldBe(tamperedSeq);
    }

    [RequiresDockerFact]
    public async Task Rows_inside_the_stabilization_watermark_wait_for_the_next_round()
    {
        DateTimeOffset month = MonthOffset(-20);
        MonthlyPartitionWindow window = WindowOf(month);
        await fixture.EnsurePartitionAsync(month);
        DateTimeOffset settled = month.AddDays(2);
        DateTimeOffset inFlight = month.AddDays(3);
        await fixture.AppendAsync($"verify-watermark-1-{Guid.CreateVersion7():N}", settled);
        await fixture.AppendAsync($"verify-watermark-2-{Guid.CreateVersion7():N}", inFlight);

        var clock = new MutableTimeProvider(inFlight.AddMinutes(1));
        await using ServiceProvider provider = fixture.BuildProvider(
            configureServices: services => services.AddSingleton<TimeProvider>(clock));

        // The first round is a full replay, which covers the whole partition
        // regardless of the watermark; the resumed round is the one that has
        // to hold back what is still settling.
        await VerifyAsync(provider, window);
        await fixture.AppendAsync($"verify-watermark-3-{Guid.CreateVersion7():N}", inFlight.AddMinutes(2));
        ChainVerificationOutcome resumed = await VerifyAsync(provider, window);

        resumed.IsIntact.ShouldBeTrue(resumed.Failure);
        resumed.ChainedCount.ShouldBe(0);

        clock.Advance(TimeSpan.FromMinutes(30));
        ChainVerificationOutcome later = await VerifyAsync(provider, window);
        later.IsIntact.ShouldBeTrue(later.Failure);
        later.ChainedCount.ShouldBeGreaterThan(0);
    }

    private static async Task<ChainVerificationOutcome> VerifyAsync(
        ServiceProvider provider,
        MonthlyPartitionWindow window)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<ChainVerifier>()
            .VerifyAsync(window, forceFullReplay: false, CancellationToken.None);
    }

    private static async Task<ChainVerificationCheckpoint> CheckpointAsync(
        ServiceProvider provider,
        string partition)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>()
            .ChainVerificationCheckpoints
            .AsNoTracking()
            .SingleAsync(checkpoint => checkpoint.PartitionName == partition);
    }

    /// <summary>Runs the registered check exactly as the host would run it.</summary>
    private static async Task<HealthCheckResult> HealthAsync(ServiceProvider provider)
    {
        HealthCheckRegistration registration = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations
            .Single(entry => entry.Name == "audit-chain-verification");
        using IServiceScope scope = provider.CreateScope();
        return await registration.Factory(scope.ServiceProvider)
            .CheckHealthAsync(new HealthCheckContext { Registration = registration }, CancellationToken.None);
    }

    private Task<List<string>> ActionsOfAsync(string partition)
        => fixture.QueryTextsAsync($"""
            SELECT action FROM audit.audit_event
            WHERE entity_type = 'audit_partition' AND entity_id = '{partition}'
            ORDER BY seq
            """);

    private static MonthlyPartitionWindow WindowOf(DateTimeOffset month)
        => MonthlyPartitions.Plan("audit_event", month, 0)[0];

    private static DateTimeOffset MonthOffset(int months)
    {
        DateTime utc = DateTime.UtcNow;
        return new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero)
            .AddMonths(months);
    }
}
