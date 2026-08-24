using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class ProducerRegistryFreshnessTests(KafkaIngressFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_default_snapshot_is_unavailable_at_sixty_seconds_when_refresh_fails()
        => await AssertUnavailableAtSixtySecondsAsync(new ProducerRegistryOptions());

    [RequiresDockerFact]
    public async Task A_cache_ttl_above_sixty_seconds_cannot_extend_snapshot_authority()
        => await AssertUnavailableAtSixtySecondsAsync(
            new ProducerRegistryOptions { CacheTtlSeconds = 3600 });

    [RequiresDockerFact]
    public async Task A_utc_regression_cannot_extend_snapshot_authority_past_sixty_monotonic_seconds()
        => await AssertUnavailableAtSixtySecondsAsync(
            new ProducerRegistryOptions(),
            regressUtc: true);

    [RequiresDockerFact]
    public async Task A_cold_load_that_consumes_sixty_seconds_cannot_authorize()
    {
        var application = KafkaIngressApi.NewApplication();
        await fixture.SeedProducerGrantsAsync(
            (KafkaIngressFixture.RequestedProducer, application, "transactional"));
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var scopeFactory = new AdvancingScopeFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clock,
            TimeSpan.FromSeconds(60));
        using var registry = new CachedProducerRegistry(
            scopeFactory,
            Options.Create(new ProducerRegistryOptions()),
            clock,
            provider.GetRequiredService<ILogger<CachedProducerRegistry>>());

        ProducerGrants? loaded = await registry.CurrentAsync(CancellationToken.None);

        loaded.ShouldBeNull();
        scopeFactory.CreateScopeCount.ShouldBe(1);
        KafkaGateDecision decision = await new ProducerRegistryConsumerGate(registry)
            .EvaluateAsync(CancellationToken.None);
        decision.CanConsume.ShouldBeFalse();
        scopeFactory.CreateScopeCount.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task Thirty_two_callers_share_one_failed_refresh_and_one_second_of_backoff()
    {
        var application = KafkaIngressApi.NewApplication();
        await fixture.SeedProducerGrantsAsync(
            (KafkaIngressFixture.RequestedProducer, application, "transactional"));
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var scopeFactory = new ControlledFailingScopeFactory(
            provider.GetRequiredService<IServiceScopeFactory>());
        using var registry = new CachedProducerRegistry(
            scopeFactory,
            Options.Create(new ProducerRegistryOptions()),
            clock,
            provider.GetRequiredService<ILogger<CachedProducerRegistry>>());
        ProducerGrants? loaded = await registry.CurrentAsync(CancellationToken.None);
        loaded.ShouldNotBeNull();

        scopeFactory.Fail = true;
        clock.AdvanceTimestamp(TimeSpan.FromSeconds(60));
        using var start = new Barrier(33);
        Task<ProducerGrants?>[] reads = [.. Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                start.SignalAndWait();
                return await registry.CurrentAsync(CancellationToken.None);
            }))];
        start.SignalAndWait();
        await scopeFactory.FailureStarted.WaitAsync(TimeSpan.FromSeconds(5));
        scopeFactory.ReleaseFailure();
        ProducerGrants?[] results = await Task.WhenAll(reads);

        results.ShouldAllBe(result => result == null);
        scopeFactory.FailedRefreshAttempts.ShouldBe(1);

        (await registry.CurrentAsync(CancellationToken.None)).ShouldBeNull();
        scopeFactory.FailedRefreshAttempts.ShouldBe(1);

        clock.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        (await registry.CurrentAsync(CancellationToken.None)).ShouldBeNull();
        scopeFactory.FailedRefreshAttempts.ShouldBe(2);
    }

    private async Task AssertUnavailableAtSixtySecondsAsync(
        ProducerRegistryOptions options,
        bool regressUtc = false)
    {
        var application = KafkaIngressApi.NewApplication();
        await fixture.SeedProducerGrantsAsync(
            (KafkaIngressFixture.RequestedProducer, application, "transactional"));
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var scopeFactory = new FailingScopeFactory(
            provider.GetRequiredService<IServiceScopeFactory>());
        using var registry = new CachedProducerRegistry(
            scopeFactory,
            Options.Create(options),
            clock,
            provider.GetRequiredService<ILogger<CachedProducerRegistry>>());

        ProducerGrants? loaded = await registry.CurrentAsync(CancellationToken.None);
        loaded.ShouldNotBeNull();
        loaded.Allows(KafkaIngressFixture.RequestedProducer, application, "transactional")
            .ShouldBeTrue();

        scopeFactory.Fail = true;
        if (regressUtc)
        {
            clock.RegressUtc(TimeSpan.FromHours(1));
            clock.AdvanceTimestamp(TimeSpan.FromSeconds(59));
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(59));
        }

        (await registry.CurrentAsync(CancellationToken.None)).ShouldBeSameAs(loaded);
        scopeFactory.FailedRefreshAttempts.ShouldBe(0);

        if (regressUtc)
        {
            clock.AdvanceTimestamp(TimeSpan.FromSeconds(1));
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        (await registry.CurrentAsync(CancellationToken.None)).ShouldBeNull();
        scopeFactory.FailedRefreshAttempts.ShouldBe(1);
        KafkaGateDecision decision = await new ProducerRegistryConsumerGate(registry)
            .EvaluateAsync(CancellationToken.None);
        decision.CanConsume.ShouldBeFalse();
    }

    private sealed class FailingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public bool Fail { get; set; }

        public int FailedRefreshAttempts { get; private set; }

        public IServiceScope CreateScope()
        {
            if (!Fail)
            {
                return inner.CreateScope();
            }

            FailedRefreshAttempts++;
            throw new InvalidOperationException("Falha de refresh injetada pelo teste.");
        }
    }

    private sealed class AdvancingScopeFactory(
        IServiceScopeFactory inner,
        MutableTimeProvider clock,
        TimeSpan loadDuration) : IServiceScopeFactory
    {
        private int _createScopeCount;

        public int CreateScopeCount => _createScopeCount;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _createScopeCount);
            clock.Advance(loadDuration);
            return inner.CreateScope();
        }
    }

    private sealed class ControlledFailingScopeFactory(IServiceScopeFactory inner)
        : IServiceScopeFactory
    {
        private readonly TaskCompletionSource _failureStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _failedRefreshAttempts;

        public bool Fail { get; set; }

        public int FailedRefreshAttempts => _failedRefreshAttempts;

        public Task FailureStarted => _failureStarted.Task;

        public IServiceScope CreateScope()
        {
            if (!Fail)
            {
                return inner.CreateScope();
            }

            Interlocked.Increment(ref _failedRefreshAttempts);
            _failureStarted.TrySetResult();
            _releaseFailure.Task.GetAwaiter().GetResult();
            throw new InvalidOperationException("Falha controlada de refresh.");
        }

        public void ReleaseFailure() => _releaseFailure.TrySetResult();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            AdvanceTimestamp(elapsed);
        }

        public void AdvanceTimestamp(TimeSpan elapsed) => _timestamp += elapsed.Ticks;

        public void RegressUtc(TimeSpan elapsed) => now -= elapsed;
    }
}
