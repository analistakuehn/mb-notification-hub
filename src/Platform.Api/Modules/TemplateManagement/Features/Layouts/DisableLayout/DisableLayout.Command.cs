using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class DisableLayout
{
    /// <summary>
    /// HTTP body, unchanged: the canonical reason, and an optional note. The
    /// reason is a code because the periodic evidence read groups the trail by
    /// it and the archived report copies the group name verbatim; the note is
    /// where the incident of the day goes, without becoming a category of its
    /// own. Only the reason reaches the trail. The note is stored by this
    /// context, and the trail records the reference to it.
    /// </summary>
    internal sealed record Request(string Reason, string? Note);

    /// <summary>
    /// The note reaches the handler as a type, never as a second string beside
    /// the reason code. Only a reference to the stored note travels to the
    /// trail, so the prose has to stay distinguishable from the code it sits
    /// next to, and a type is what makes that distinction checkable by a rule
    /// instead of by a reader.
    /// </summary>
    internal sealed record Command(string Key, string Reason, LifecycleNoteText? Note, string Actor);
}
