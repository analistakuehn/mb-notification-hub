using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
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

    /// <summary>The default round interval is part of the delivery budget, só it is asserted.</summary>
    [Fact]
    public void The_default_round_interval_is_two_seconds()
        => new SchedulerScanOptions().Interval.ShouldBe(TimeSpan.FromSeconds(2));

    /// <summary>
    /// The fallback of a critical push has an accepted time to the SMS, and
    /// that time is a sum of knobs nobody was adding up.
    /// <para>
    /// Every term is either a configured default this test reads or a hop the
    /// delivery design budgets. The terms that are read matter most: the round
    /// interval and the provider timeout are the two an operator can change,
    /// and either one raised on its own puts the sum past the accepted window
    /// with nothing to say só. No integration test measures this path with a
    /// real clock, because the end-to-end scenario drives every stage by hand
    /// off a controlled clock, só this arithmetic is the only guard the
    /// repository has.
    /// </para>
    /// <para>
    /// It is a budget and not a measurement, and the difference is worth
    /// stating: passing here means the configured worst case fits, never that
    /// the running system does. What it does catch is the change that makes the
    /// promise arithmetically impossible.
    /// </para>
    /// </summary>
    [Fact]
    public void The_worst_case_fallback_to_sms_fits_the_accepted_window()
    {
        // The timeout the critical plan gives its first step, which is where
        // the deadline this scan reads comes from.
        var criticalFirstStep = TimeSpan.FromSeconds(30);

        // The two queue hops and the Core stage, as the delivery design budgets
        // them: outbox to relay, relay to consumer, and the stage itself.
        var outboxHop = TimeSpan.FromMilliseconds(300);
        var relayHop = TimeSpan.FromMilliseconds(300);
        var coreStage = TimeSpan.FromMilliseconds(200);

        // The accepted time from a degraded push to the SMS that replaces it.
        var accepted = TimeSpan.FromSeconds(35);

        TimeSpan worstCase = criticalFirstStep
            + new SchedulerScanOptions().Interval
            + outboxHop
            + relayHop
            + coreStage
            + TimeSpan.FromSeconds(new TwilioOptions().TimeoutSeconds);

        worstCase.ShouldBeLessThanOrEqualTo(
            accepted,
            "o prazo do passo, o intervalo da varredura, os dois saltos de fila, o estágio Core e a "
            + "chamada ao provedor somam o tempo até o SMS de fallback; se a soma passa do aceite, a "
            + "promessa do plano crítico é aritmeticamente impossível e nenhum teste de tempo real "
            + "existe para descobrir isso.");
    }

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
