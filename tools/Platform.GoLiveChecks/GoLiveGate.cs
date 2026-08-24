using System.Globalization;

namespace NotificationHub.Platform.GoLiveChecks;

internal sealed class GoLiveGate(
    IGoLiveCheckSource templateSource,
    IGoLiveCheckSource graphSource,
    TimeProvider timeProvider)
{
    public async Task<GateRunResult> RunAsync(CancellationToken cancellationToken)
    {
        GoLiveSourceReceipt templateResult = await ReadSourceAsync(templateSource, cancellationToken);
        GoLiveSourceReceipt graphResult = await ReadSourceAsync(graphSource, cancellationToken);
        IReadOnlyList<GoLiveSourceReceipt> sources = [templateResult, graphResult];
        List<string> reasons = [];

        if (templateResult.Count is > 0)
        {
            reasons.Add(GoLiveReasons.PublishedOperationalTemplatesPresent);
        }

        if (graphResult.Count is > 0)
        {
            reasons.Add(GoLiveReasons.OperationalRoleAssignmentsPresent);
        }

        foreach (GoLiveSourceReceipt source in sources.Where(source => source.Count is null))
        {
            reasons.Add(GoLiveReasons.SourceUnavailable(source.Identifier));
        }

        var hasError = sources.Any(source => source.Count is null);
        var hasViolation = sources.Any(source => source.Count is > 0);
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
