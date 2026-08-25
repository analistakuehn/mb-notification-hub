using System.Globalization;

namespace NotificationHub.Platform.GoLiveChecks;

/// <summary>
/// One run of the gate over its sources. Two of the three sources are read for
/// the receipt alone: the published operational templates and the granted send
/// role used to be violations, because nothing in the fleet read the release
/// instant of a deferred notification and a notification of that class would
/// have stayed parked forever. The scheduler reads it now, so the counts stay
/// in the receipt as evidence of what is switched on and stop deciding the
/// exit code.
/// <para>
/// What still refuses a release is a published critical policy whose delivery
/// plan has no step after the first: that is the shape a critical notification
/// with no fallback takes, and the fleet cannot claim otherwise while one
/// exists. An unreadable source remains an error rather than a pass, in every
/// source, because a receipt missing its evidence proves nothing.
/// </para>
/// </summary>
internal sealed class GoLiveGate(
    IGoLiveCheckSource templateSource,
    IGoLiveCheckSource graphSource,
    IGoLiveCheckSource criticalPlanSource,
    TimeProvider timeProvider)
{
    public async Task<GateRunResult> RunAsync(CancellationToken cancellationToken)
    {
        GoLiveSourceReceipt templateResult = await ReadSourceAsync(templateSource, cancellationToken);
        GoLiveSourceReceipt graphResult = await ReadSourceAsync(graphSource, cancellationToken);
        GoLiveSourceReceipt planResult = await ReadSourceAsync(criticalPlanSource, cancellationToken);
        IReadOnlyList<GoLiveSourceReceipt> sources = [templateResult, graphResult, planResult];
        List<string> reasons = [];

        var hasViolation = planResult.Count is > 0;
        if (hasViolation)
        {
            reasons.Add(GoLiveReasons.CriticalPlansWithoutFallbackPresent);
        }

        foreach (GoLiveSourceReceipt source in sources.Where(source => source.Count is null))
        {
            reasons.Add(GoLiveReasons.SourceUnavailable(source.Identifier));
        }

        var hasError = sources.Any(source => source.Count is null);
        var status = hasError
            ? GoLiveStatuses.Error
            : hasViolation
                ? GoLiveStatuses.Fail
                : GoLiveStatuses.Pass;
        var exitCode = hasError
            ? GoLiveExitCodes.Error
            : hasViolation
                ? GoLiveExitCodes.Violation
                : GoLiveExitCodes.Pass;
        var receipt = new GoLiveReceipt(
            timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            status,
            sources,
            reasons);
        return new GateRunResult(receipt, exitCode);
    }

    private static async ValueTask<GoLiveSourceReceipt> ReadSourceAsync(
        IGoLiveCheckSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            GoLiveSourceCheck result = await source.CheckAsync(cancellationToken);
            var identityRequired = string.Equals(
                source.Identifier,
                GoLiveSourceIdentifiers.MicrosoftGraph,
                StringComparison.Ordinal);
            return result.Count < 0 || (identityRequired && result.VerifiedIdentity is null)
                ? new GoLiveSourceReceipt(source.Identifier, null)
                : new GoLiveSourceReceipt(
                    source.Identifier,
                    result.Count,
                    result.VerifiedIdentity);
        }
        catch (Exception)
        {
            return new GoLiveSourceReceipt(source.Identifier, null);
        }
    }
}
