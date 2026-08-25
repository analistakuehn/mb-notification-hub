using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;

/// <summary>
/// Decides what a provider failure code says about the destination, using
/// vocabulary the operator configures rather than a table compiled into this
/// assembly. Providers revise their failure codes on their own schedule, and
/// the consequence of the decision, suppressing a contact point, is close to
/// irreversible for the person behind it: an operator has to be able to
/// retire a code the same day the provider changes its meaning.
/// </summary>
internal static class SuppressionClassifier
{
    /// <summary>
    /// Classifies one failure code. Invalid destination is tested first
    /// because it is the more specific claim, and anything the configured
    /// lists do not name stays <see cref="SuppressionSignal.None"/>: an
    /// unknown code is unknown, never assumed permanent.
    /// </summary>
    internal static SuppressionSignal Classify(
        string? code,
        IReadOnlyList<string> invalidDestinationCodes,
        IReadOnlyList<string> hardBounceCodes)
    {
        if (string.IsNullOrWhiteSpace(code)) return SuppressionSignal.None;

        if (WebhookRequestGuards.Names(invalidDestinationCodes, code)) return SuppressionSignal.InvalidDestination;

        return WebhookRequestGuards.Names(hardBounceCodes, code)
            ? SuppressionSignal.HardBounce
            : SuppressionSignal.None;
    }
}
