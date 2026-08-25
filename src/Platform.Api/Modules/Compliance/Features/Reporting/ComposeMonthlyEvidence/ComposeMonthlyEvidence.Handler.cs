using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Compliance.Features.Reporting;

/// <summary>
/// Composition of the monthly evidence report and its archival. The module
/// owns the question and none of the data: both halves arrive through the
/// published contracts of the modules that own them, and the bytes leave
/// through the archive contract of the module that owns immutability.
/// </summary>
internal static partial class ComposeMonthlyEvidence
{
    private const string ReportContentType = "application/json";

    /// <summary>What one composition settled: where the report is, and whether this round put it there.</summary>
    internal sealed record Outcome(string Month, string Key, string Sha256Hex, bool AlreadyPresent);

    internal sealed class Handler(
        INotificationOutcomeReport outcomes,
        IAuditPeriodEvidence trail,
        IEvidenceArchive archive,
        IOptions<MonthlyEvidenceReportOptions> options,
        ILogger<Handler> logger)
    {
        /// <summary>
        /// Composes one closed month and archives it. Rerunning over a month
        /// already archived recomputes the same bytes and settles on the object
        /// that is already there; it never rewrites one, because the
        /// destination forbids it and because evidence that changed after it
        /// was archived is a finding for a person.
        /// </summary>
        public async Task<Result<Outcome>> HandleAsync(ReportMonth month, CancellationToken cancellationToken)
        {
            Result<NotificationOutcomeSummary> summary = await outcomes.SummarizeAsync(
                month.FromInclusive, month.ToExclusive, cancellationToken);
            if (summary.IsFailure)
            {
                return new Result<Outcome>(false, null, summary.ErrorKind, summary.Error);
            }

            AuditPeriodEvidence period = await trail.SummarizeAsync(
                month.FromInclusive, month.ToExclusive, cancellationToken);

            MonthlyEvidenceReport report = MonthlyEvidenceComposition.Compose(
                month, options.Value.ReconciliationGrace, summary.Value!, period);
            var key = EvidenceReportKeys.Monthly(
                month.Year, month.Month, MonthlyEvidenceReport.CurrentFormatVersion);

            Result<ArchivedEvidence> archived = await archive.ArchiveAsync(
                key, report.CanonicalBytes(), ReportContentType, cancellationToken);
            if (archived.IsFailure)
            {
                return new Result<Outcome>(false, null, archived.ErrorKind, archived.Error);
            }

            ArchivedEvidence receipt = archived.Value!;
            if (!receipt.AlreadyPresent)
            {
                logger.MonthlyReportArchived(month.Name, receipt.Key, receipt.Sha256Hex);
            }

            return Result.Success(new Outcome(
                month.Name, receipt.Key, receipt.Sha256Hex, receipt.AlreadyPresent));
        }
    }
}
