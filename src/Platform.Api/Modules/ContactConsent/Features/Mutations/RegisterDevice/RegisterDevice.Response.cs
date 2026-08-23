namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class RegisterDevice
{
    /// <summary>The registration state after the post; the token itself never echoes back.</summary>
    internal sealed record Response(
        Guid DeviceTokenId,
        string Platform,
        DateTimeOffset RegisteredAt,
        DateTimeOffset LastSeenAt);

    /// <summary>Every outcome of the use case, modeled as data.</summary>
    internal abstract record Outcome
    {
        private Outcome()
        {
        }

        /// <summary>Registered or refreshed; answer 200.</summary>
        internal sealed record Registered(Response Response) : Outcome;

        /// <summary>A concurrent registration for the same recipient won the race; answer 409.</summary>
        internal sealed record ConcurrencyConflict : Outcome;
    }
}
