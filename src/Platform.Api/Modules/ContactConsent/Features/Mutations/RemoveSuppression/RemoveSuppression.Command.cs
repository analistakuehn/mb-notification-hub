namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class RemoveSuppression
{
    /// <summary>
    /// One operator taking back a suppression. The justification is required
    /// and travels to the trail: reversing an automatic decision about whether
    /// the hub may address a person is exactly the kind of act an auditor asks
    /// the reason for, and the actor alone does not answer that.
    /// </summary>
    internal sealed record Command(string Justification);
}
