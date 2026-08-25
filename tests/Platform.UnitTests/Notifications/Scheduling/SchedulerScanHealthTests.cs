using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

namespace NotificationHub.UnitTests.Notifications.Scheduling;

/// <summary>
/// The health of a scheduler is the health of its rounds. Every other probe of
/// this role answers about a connection or a queue, and all of them keep
/// answering success while the scan loop is dead, because a dead loop raises
/// nothing and drains nothing.
/// </summary>
public sealed class SchedulerScanHealthTests
{
    [Fact]
    public async Task A_host_that_has_not_finished_its_first_round_is_healthy()
    {
        var clock = new SteppableTimeProvider();
        var heartbeat = new SchedulerScanHeartbeat(clock);

        HealthCheckResult result = await CheckAsync(heartbeat, new SchedulerScanOptions());

        result.Status.ShouldBe(
            HealthStatus.Healthy,
            "a primeira rodada de um processo recém-iniciado ainda não teve tempo de acontecer; "
            + "reprovar ali faria todo rollout parecer indisponibilidade.");
    }

    [Fact]
    public async Task A_scheduler_whose_rounds_keep_landing_is_healthy()
    {
        var clock = new SteppableTimeProvider();
        var heartbeat = new SchedulerScanHeartbeat(clock);
        var options = new SchedulerScanOptions();
        heartbeat.RoundCompleted();

        clock.Advance(options.Interval * (options.HealthyRoundsMissedLimit - 1));

        (await CheckAsync(heartbeat, options)).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task A_scheduler_that_stopped_finishing_rounds_is_unhealthy()
    {
        var clock = new SteppableTimeProvider();
        var heartbeat = new SchedulerScanHeartbeat(clock);
        var options = new SchedulerScanOptions();
        heartbeat.RoundCompleted();

        clock.Advance(options.Interval * (options.HealthyRoundsMissedLimit + 1));

        HealthCheckResult result = await CheckAsync(heartbeat, options);

        result.Status.ShouldBe(
            HealthStatus.Unhealthy,
            "um scheduler parado não derruba o processo nem esvazia fila alguma; se a ausência de "
            + "rodadas não reprova aqui, nada mais reprova e as entregas param em silêncio.");
    }

    [Fact]
    public async Task A_scheduler_turned_off_by_configuration_is_healthy_and_says_so()
    {
        var heartbeat = new SchedulerScanHeartbeat(new SteppableTimeProvider());

        HealthCheckResult result = await CheckAsync(
            heartbeat, new SchedulerScanOptions { Enabled = false });

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Description.ShouldNotBeNull().ShouldContain("desligada");
    }

    /// <summary>The default round interval is part of the delivery budget, so it is asserted.</summary>
    [Fact]
    public void The_default_round_interval_is_five_seconds()
        => new SchedulerScanOptions().Interval.ShouldBe(TimeSpan.FromSeconds(5));

    /// <summary>
    /// The grace the inconclusive-verdict scan waits before asking for the next
    /// step. It is configuration, and its default is the one the delivery
    /// design names.
    /// </summary>
    [Fact]
    public void The_default_grace_for_an_inconclusive_verdict_is_a_minute()
        => new SchedulerScanOptions().UnknownGrace.ShouldBe(TimeSpan.FromSeconds(60));

    private static async Task<HealthCheckResult> CheckAsync(
        SchedulerScanHeartbeat heartbeat,
        SchedulerScanOptions options)
        => await new SchedulerScanHealthCheck(heartbeat, Options.Create(options))
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    /// <summary>
    /// A clock whose monotonic timestamp moves only when the test moves it.
    /// The heartbeat measures the gap between rounds in elapsed time rather
    /// than in wall-clock instants, so this has to control the timestamp and
    /// not merely the current instant.
    /// </summary>
    private sealed class SteppableTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_timestamp);

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan delta) => _timestamp += delta.Ticks;
    }
}
