using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

namespace NotificationHub.UnitTests.Notifications.KillSwitch;

public sealed class KillSwitchCacheTests
{
    [Fact]
    public async Task Concurrent_cold_checks_share_one_snapshot_load()
    {
        var source = new SnapshotSource(async cancellationToken =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Active((KillSwitchScope.Channel, "sms"));
        });
        var cache = new KillSwitchCache(source, new ManualTimeProvider());

        Task<KillSwitchEvaluation>[] checks = [.. Enumerable.Range(0, 32)
            .Select(_ => cache.EvaluateAsync(
                KillSwitchScope.Channel, "sms", CancellationToken.None).AsTask())];
        KillSwitchEvaluation[] results = await Task.WhenAll(checks);

        source.LoadCount.ShouldBe(1);
        results.ShouldAllBe(result => result == KillSwitchEvaluation.Blocked);
    }

    [Fact]
    public async Task An_expired_snapshot_failure_fails_closed_without_serving_stale_data()
    {
        var calls = 0;
        var source = new SnapshotSource(_ => sourceCall());
        var time = new ManualTimeProvider();
        var cache = new KillSwitchCache(source, time);

        KillSwitchEvaluation initial = await cache.EvaluateAsync(
            KillSwitchScope.Application, "billing", CancellationToken.None);
        initial.ShouldBe(KillSwitchEvaluation.Allowed);

        time.Advance(TimeSpan.FromSeconds(6));
        KillSwitchEvaluation afterFailure = await cache.EvaluateAsync(
            KillSwitchScope.Application, "billing", CancellationToken.None);
        afterFailure.ShouldBe(KillSwitchEvaluation.Unavailable);
        source.LoadCount.ShouldBe(2);
        return;

        Task<IReadOnlySet<KillSwitchAddress>> sourceCall()
        {
            calls++;
            return calls == 1
                ? Task.FromResult<IReadOnlySet<KillSwitchAddress>>(Active())
                : Task.FromException<IReadOnlySet<KillSwitchAddress>>(
                    new InvalidOperationException("postgres unavailable"));
        }
    }

    [Fact]
    public async Task A_utc_regression_cannot_extend_a_snapshot_past_its_monotonic_ttl()
    {
        var calls = 0;
        var source = new SnapshotSource(_ => sourceCall());
        var time = new ManualTimeProvider();
        var cache = new KillSwitchCache(source, time);

        KillSwitchEvaluation initial = await cache.EvaluateAsync(
            KillSwitchScope.Application, "billing", CancellationToken.None);
        initial.ShouldBe(KillSwitchEvaluation.Allowed);

        time.RegressUtc(TimeSpan.FromHours(1));
        time.AdvanceTimestamp(TimeSpan.FromSeconds(6));

        KillSwitchEvaluation afterFailure = await cache.EvaluateAsync(
            KillSwitchScope.Application, "billing", CancellationToken.None);
        afterFailure.ShouldBe(KillSwitchEvaluation.Unavailable);
        source.LoadCount.ShouldBe(2);
        return;

        Task<IReadOnlySet<KillSwitchAddress>> sourceCall()
        {
            calls++;
            return calls == 1
                ? Task.FromResult<IReadOnlySet<KillSwitchAddress>>(Active())
                : Task.FromException<IReadOnlySet<KillSwitchAddress>>(
                    new InvalidOperationException("postgres unavailable"));
        }
    }

    [Fact]
    public async Task A_cold_load_that_consumes_the_snapshot_ttl_fails_closed()
    {
        var time = new ManualTimeProvider();
        var source = new SnapshotSource(_ =>
        {
            time.Advance(TimeSpan.FromSeconds(5));
            return Task.FromResult<IReadOnlySet<KillSwitchAddress>>(
                Active((KillSwitchScope.Producer, "orders")));
        });
        var cache = new KillSwitchCache(source, time);

        KillSwitchEvaluation result = await cache.EvaluateAsync(
            KillSwitchScope.Producer, "orders", CancellationToken.None);

        result.ShouldBe(KillSwitchEvaluation.Unavailable);
        cache.Status().State.ShouldBe(KillSwitchSnapshotState.Unavailable);
        source.LoadCount.ShouldBe(1);
    }

    [Fact]
    public async Task Thirty_two_failed_refresh_callers_share_one_second_of_backoff()
    {
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SnapshotSource(async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("postgres unavailable");
        });
        var cache = new KillSwitchCache(source, time);

        Task<KillSwitchEvaluation>[] checks = [.. Enumerable.Range(0, 32)
            .Select(_ => cache.EvaluateAsync(
                KillSwitchScope.Application, "billing", CancellationToken.None).AsTask())];
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        KillSwitchEvaluation[] results = await Task.WhenAll(checks);

        results.ShouldAllBe(result => result == KillSwitchEvaluation.Unavailable);
        source.LoadCount.ShouldBe(1);

        KillSwitchEvaluation duringBackoff = await cache.EvaluateAsync(
            KillSwitchScope.Application, "billing", CancellationToken.None);
        duringBackoff.ShouldBe(KillSwitchEvaluation.Unavailable);
        source.LoadCount.ShouldBe(1);

        time.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        KillSwitchEvaluation afterBackoff = await cache.EvaluateAsync(
            KillSwitchScope.Application, "billing", CancellationToken.None);
        afterBackoff.ShouldBe(KillSwitchEvaluation.Unavailable);
        source.LoadCount.ShouldBe(2);
    }

    [Fact]
    public async Task Two_local_cache_instances_observe_the_updated_snapshot_after_the_ttl()
    {
        IReadOnlySet<KillSwitchAddress> active = Active();
        var source = new SnapshotSource(_ => Task.FromResult(active));
        var time = new ManualTimeProvider();
        var first = new KillSwitchCache(source, time);
        var second = new KillSwitchCache(source, time);

        KillSwitchEvaluation firstInitial = await first.EvaluateAsync(
            KillSwitchScope.Producer, "orders", CancellationToken.None);
        KillSwitchEvaluation secondInitial = await second.EvaluateAsync(
            KillSwitchScope.Producer, "orders", CancellationToken.None);
        firstInitial.ShouldBe(KillSwitchEvaluation.Allowed);
        secondInitial.ShouldBe(KillSwitchEvaluation.Allowed);

        active = Active((KillSwitchScope.Producer, "orders"));
        time.Advance(TimeSpan.FromSeconds(5));

        KillSwitchEvaluation firstBlocked = await first.EvaluateAsync(
            KillSwitchScope.Producer, "orders", CancellationToken.None);
        KillSwitchEvaluation secondBlocked = await second.EvaluateAsync(
            KillSwitchScope.Producer, "orders", CancellationToken.None);
        firstBlocked.ShouldBe(KillSwitchEvaluation.Blocked);
        secondBlocked.ShouldBe(KillSwitchEvaluation.Blocked);
    }

    private static HashSet<KillSwitchAddress> Active(
        params (KillSwitchScope Scope, string Key)[] addresses)
        => addresses.Select(address => new KillSwitchAddress(address.Scope, address.Key)).ToHashSet();

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
