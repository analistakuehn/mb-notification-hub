namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class DisableTemplate
{
    /// <summary>
    /// HTTP body: the canonical reason recorded in the audit trail, and an
    /// optional note. The reason is a code because the periodic evidence read
    /// groups the trail by it and the archived report copies the group name
    /// verbatim; the note is where the incident of the day goes, without
    /// becoming a category of its own.
    /// </summary>
    internal sealed record Request(string Reason, string? Note);

    internal sealed record Command(string Key, string Reason, string? Note, string Actor);
}
