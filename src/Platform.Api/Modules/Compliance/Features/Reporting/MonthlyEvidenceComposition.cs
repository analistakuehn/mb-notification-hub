using System.Xml;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.Api.Modules.Compliance.Features.Reporting;

/// <summary>
/// The calendar month a report covers, in UTC. Nothing about the month depends
/// on where anybody is: the trail stores instants and the report is read by
/// people in more than one place.
/// </summary>
internal readonly record struct ReportMonth(int Year, int Month)
{
    internal DateTimeOffset FromInclusive => new(Year, Month, 1, 0, 0, 0, TimeSpan.Zero);

    internal DateTimeOffset ToExclusive => FromInclusive.AddMonths(1);

    internal string Name => EvidenceReportKeys.MonthName(Year, Month);

    /// <summary>The month one instant falls in.</summary>
    internal static ReportMonth Of(DateTimeOffset instant)
    {
        DateTimeOffset utc = instant.ToUniversalTime();
        return new ReportMonth(utc.Year, utc.Month);
    }

    internal ReportMonth AddMonths(int months)
    {
        DateTimeOffset shifted = FromInclusive.AddMonths(months);
        return new ReportMonth(shifted.Year, shifted.Month);
    }
}

/// <summary>
/// Turns what the owning modules answered into the archived document. It is a
/// pure function of its inputs on purpose: the bytes must be reproducible from
/// the same sources, which is the whole basis of a rerun that recognizes what
/// it already wrote.
/// </summary>
internal static class MonthlyEvidenceComposition
{
    internal static MonthlyEvidenceReport Compose(
        ReportMonth month,
        TimeSpan reconciliationGrace,
        NotificationOutcomeSummary outcomes,
        AuditPeriodEvidence trail)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(trail);

        return new MonthlyEvidenceReport
        {
            FormatVersion = MonthlyEvidenceReport.CurrentFormatVersion,
            Report = MonthlyEvidenceReport.ReportKind,
            Window = new MonthlyEvidenceReport.ReportWindow
            {
                Month = month.Name,
                FromInclusive = month.FromInclusive,
                ToExclusive = month.ToExclusive,
                ReconciliationGrace = XmlConvert.ToString(reconciliationGrace),
            },
            Volumes = [.. outcomes.VolumesByClass.Select(volume => new MonthlyEvidenceReport.ClassVolume
            {
                Class = volume.Class,
                Requested = volume.Requested,
                ByStatus = [.. volume.ByStatus.Select(status => new MonthlyEvidenceReport.NamedCount
                {
                    Name = status.Status,
                    Count = status.Count,
                })],
            })],
            Channels = [.. outcomes.OutcomesByChannel.Select(ToChannel)],
            Refusals = new MonthlyEvidenceReport.RefusalSummary
            {
                ByPolicyReason = [.. outcomes.RejectionsByReason.Select(rejection =>
                    new MonthlyEvidenceReport.NamedCount { Name = rejection.Reason, Count = rejection.Count })],
                ByTrailAction = [.. trail.ReasonCounts
                    .GroupBy(reason => reason.Action, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new MonthlyEvidenceReport.ActionReasons
                    {
                        Action = group.Key,
                        ByReason = [.. group
                            .OrderBy(reason => reason.Reason, StringComparer.Ordinal)
                            .Select(reason => new MonthlyEvidenceReport.NamedCount
                            {
                                Name = reason.Reason,
                                Count = reason.Count,
                            })],
                    })],
            },
            Trail = new MonthlyEvidenceReport.TrailActivity
            {
                ByAction = [.. trail.ActionCounts.Select(action => new MonthlyEvidenceReport.NamedCount
                {
                    Name = action.Action,
                    Count = action.Count,
                })],
                UnchainedRows = trail.UnchainedRows,
            },
            GovernedChanges = [.. trail.GovernedChanges.Select(change => new MonthlyEvidenceReport.GovernedChange
            {
                Seq = change.Seq,
                Action = change.Action,
                EntityType = change.EntityType,
                EntityId = change.EntityId,
                ActorType = change.ActorType,
                ActorId = change.ActorId,
                Application = change.Application,
                OccurredAt = change.OccurredAt,
                Hash = change.Hash,
            })],
            Approvals = [.. trail.Approvals.Select(approval => new MonthlyEvidenceReport.Approval
            {
                SubjectType = approval.SubjectType,
                SubjectId = approval.SubjectId,
                SubjectVersion = approval.SubjectVersion,
                ContentHash = approval.ContentHash,
                Role = approval.Role,
                ApproverOid = approval.ApproverOid,
                ApprovedAt = approval.ApprovedAt,
            })],
            ChainVerification = [.. trail.ChainVerifications.Select(verification =>
                new MonthlyEvidenceReport.ChainVerificationOutcome
                {
                    Partition = verification.Partition,
                    IntactRounds = verification.IntactRounds,
                    FailedRounds = verification.FailedRounds,
                    LastIntactAt = verification.LastIntactAt,
                    LastFailureAt = verification.LastFailureAt,
                })],

            // Absent by rule, not by omission: no source in this hub answers
            // any of the three, and an empty list would assert that nothing of
            // the kind happened. The names stay declared so the phase that
            // gains the source knows which member it is filling.
            DeadLetterQueues = null,
            ProviderFailures = null,
            PrivilegedAccessActivations = null,
        };
    }

    private static MonthlyEvidenceReport.ChannelOutcome ToChannel(NotificationChannelOutcome outcome)
        => new()
        {
            Channel = outcome.Channel,
            DeliveryConfirmation = outcome.DeliveryConfirmation,
            Attempts = outcome.Attempts,
            AcceptedByProvider = outcome.AcceptedByProvider,
            Delivered = outcome.Delivered,
            Bounced = outcome.Bounced,
            Failed = outcome.Failed,
            Unknown = outcome.Unknown,
            Pending = outcome.Pending,
            DeliveryRate = Rate(outcome.Delivered, outcome),
            BounceRate = Rate(outcome.Bounced, outcome),
        };

    /// <summary>
    /// A rate the report is entitled to state, or nothing at all. Two
    /// conditions withhold it, and both would otherwise be published as a
    /// zero: a channel whose providers never report a delivery, and a channel
    /// where no provider accepted anything to report on.
    /// </summary>
    private static double? Rate(long numerator, NotificationChannelOutcome outcome)
    {
        var measurable = string.Equals(
            outcome.DeliveryConfirmation,
            DeliveryConfirmationSources.ProviderFeedback,
            StringComparison.Ordinal);
        return measurable && outcome.AcceptedByProvider > 0
            ? Math.Round((double)numerator / outcome.AcceptedByProvider, 6, MidpointRounding.ToEven)
            : null;
    }
}
