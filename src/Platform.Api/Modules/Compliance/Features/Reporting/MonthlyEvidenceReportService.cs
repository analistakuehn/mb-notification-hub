using Microsoft.Extensions.Options;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Compliance.Features.Reporting;

/// <summary>
/// Scheduler of the monthly evidence report: one round at host start, then one
/// per configured cadence. A round composes every closed month inside the
/// lookback that has already served its reconciliation grace, and a month
/// already archived costs one head request more than the reads.
/// </summary>
/// <remarks>
/// A failed month is logged and left to the next round: the job is a producer
/// of evidence and never a gate, so it must not take the host down and must
/// not stop at the first month that cannot be composed.
/// </remarks>
internal sealed class MonthlyEvidenceReportService(
    IServiceScopeFactory scopeFactory,
    IOptions<MonthlyEvidenceReportOptions> options,
    TimeProvider timeProvider,
    ILogger<MonthlyEvidenceReportService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MonthlyEvidenceReportOptions settings = options.Value;
        if (!settings.Enabled)
        {
            logger.MonthlyReportDisabled();
            return;
        }

        logger.MonthlyReportStarted(settings.Interval, settings.ReconciliationGrace);
        try
        {
            await RunRoundAsync(stoppingToken);
            using var timer = new PeriodicTimer(settings.Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunRoundAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: the round is idempotent and the next start
            // reaches the same months through the same deterministic keys.
        }
    }

    /// <summary>
    /// The closed months a round revisits, most recent first. The current
    /// month is never a candidate: it is still being written.
    /// </summary>
    internal static IEnumerable<ReportMonth> DueMonths(
        DateTimeOffset now,
        TimeSpan reconciliationGrace,
        int lookbackMonths)
    {
        var current = ReportMonth.Of(now);
        for (var back = 1; back <= lookbackMonths; back++)
        {
            ReportMonth candidate = current.AddMonths(-back);
            if (now >= candidate.ToExclusive + reconciliationGrace)
            {
                yield return candidate;
            }
        }
    }

    private async Task RunRoundAsync(CancellationToken cancellationToken)
    {
        MonthlyEvidenceReportOptions settings = options.Value;
        foreach (ReportMonth month in DueMonths(
            timeProvider.GetUtcNow(), settings.ReconciliationGrace, settings.LookbackMonths))
        {
            await ComposeAsync(month, cancellationToken);
        }
    }

    private async Task ComposeAsync(ReportMonth month, CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            Result<ComposeMonthlyEvidence.Outcome> composed = await scope.ServiceProvider
                .GetRequiredService<ComposeMonthlyEvidence.Handler>()
                .HandleAsync(month, cancellationToken);
            if (composed.IsFailure)
            {
                logger.MonthlyReportRefused(month.Name, composed.Error!);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.MonthlyReportRoundFailed(exception, month.Name);
        }
    }
}
