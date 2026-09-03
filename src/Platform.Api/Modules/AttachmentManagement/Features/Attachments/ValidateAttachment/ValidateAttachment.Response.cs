namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class ValidateAttachment
{
    /// <summary>
    /// What a producer is told when a verdict left the attachment in a state
    /// it can still work with. It carries the state and nothing else: which
    /// check refused, and how long a wait has left to run, are durable state
    /// read by the authorized operations query, and a producer that could read
    /// them here would read a map of what to work around.
    /// </summary>
    internal sealed record Response(string Reference, string State);
}
