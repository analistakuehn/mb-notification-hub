using Microsoft.Extensions.Diagnostics.HealthChecks;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

namespace NotificationHub.UnitTests.Notifications.KillSwitch;

public sealed class KillSwitchHealthCheckTests
{
    [Fact]
    public async Task A_cold_cache_is_healthy_when_the_snapshot_source_responds()
    {
        HashSet<KillSwitchAddress> snapshot = [];
        var source = new SnapshotSource(
            _ => Task.FromResult<IReadOnlySet<KillSwitchAddress>>(snapshot));
        var cache = new KillSwitchCache(source, new ManualTimeProvider());
        var healthCheck = new KillSwitchHealthCheck(cache);

        HealthCheckResult result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data["snapshot"].ShouldBe("fresh");
        source.LoadCount.ShouldBe(1);
    }

    [Fact]
    public async Task An_unavailable_snapshot_source_makes_the_health_check_unhealthy()
    {
        var source = new SnapshotSource(
            _ => Task.FromException<IReadOnlySet<KillSwitchAddress>>(
                new InvalidOperationException("database unavailable for private-key")));
        var cache = new KillSwitchCache(source, new ManualTimeProvider());
        var healthCheck = new KillSwitchHealthCheck(cache);

        HealthCheckResult result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldBe("Snapshot do kill switch não está disponível.");
        result.Data["snapshot"].ShouldBe("unavailable");
        result.Exception.ShouldBeNull();
        source.LoadCount.ShouldBe(1);
    }

    [Fact]
    public async Task An_expired_cache_is_refreshed_before_reporting_health()
    {
        HashSet<KillSwitchAddress> snapshot = [];
        var source = new SnapshotSource(
            _ => Task.FromResult<IReadOnlySet<KillSwitchAddress>>(snapshot));
        var timeProvider = new ManualTimeProvider();
        var cache = new KillSwitchCache(source, timeProvider);
        var healthCheck = new KillSwitchHealthCheck(cache);

        HealthCheckResult initial = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        HealthCheckResult refreshed = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        initial.Status.ShouldBe(HealthStatus.Healthy);
        refreshed.Status.ShouldBe(HealthStatus.Healthy);
        refreshed.Data["snapshot"].ShouldBe("fresh");
        source.LoadCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_utc_regression_does_not_prevent_health_from_refreshing_an_expired_cache()
    {
        HashSet<KillSwitchAddress> snapshot = [];
        var source = new SnapshotSource(
            _ => Task.FromResult<IReadOnlySet<KillSwitchAddress>>(snapshot));
        var timeProvider = new ManualTimeProvider();
        var cache = new KillSwitchCache(source, timeProvider);
        var healthCheck = new KillSwitchHealthCheck(cache);

        HealthCheckResult initial = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        timeProvider.RegressUtc(TimeSpan.FromHours(1));
        timeProvider.AdvanceTimestamp(TimeSpan.FromSeconds(6));
        HealthCheckResult refreshed = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        initial.Status.ShouldBe(HealthStatus.Healthy);
        refreshed.Status.ShouldBe(HealthStatus.Healthy);
        refreshed.Data["snapshot"].ShouldBe("fresh");
        source.LoadCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_cold_load_that_consumes_the_snapshot_ttl_reports_unhealthy()
    {
        var timeProvider = new ManualTimeProvider();
        HashSet<KillSwitchAddress> snapshot = [];
        var source = new SnapshotSource(_ =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            return Task.FromResult<IReadOnlySet<KillSwitchAddress>>(snapshot);
        });
        var cache = new KillSwitchCache(source, timeProvider);
        var healthCheck = new KillSwitchHealthCheck(cache);

        HealthCheckResult result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Data["snapshot"].ShouldBe("unavailable");
        source.LoadCount.ShouldBe(1);
    }

    private sealed class SnapshotSource(
        Func<CancellationToken, Task<IReadOnlySet<KillSwitchAddress>>> load)
        : IKillSwitchSnapshotSource
    {
        private int _loadCount;

        internal int LoadCount => _loadCount;

        public Task<IReadOnlySet<KillSwitchAddress>> LoadActiveAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loadCount);
            return load(cancellationToken);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            AdvanceTimestamp(duration);
        }

        internal void AdvanceTimestamp(TimeSpan duration) => _timestamp += duration.Ticks;

        internal void RegressUtc(TimeSpan duration) => _utcNow -= duration;
    }
}
