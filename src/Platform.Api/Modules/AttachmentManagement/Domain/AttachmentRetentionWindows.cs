namespace NotificationHub.Api.Modules.AttachmentManagement.Domain;

/// <summary>
/// How long the module keeps the content of an attachment nothing is doing
/// anything with, one window per state that can be abandoned. Each one is
/// counted from the last event that could still have changed that state, and
/// never from the creation.
/// <para>
/// They arrive as an argument, exactly as the size ceiling does, because all
/// four are approved values and none of them is this module's to choose.
/// Written as constants they would be product decisions taken by whoever
/// edited this file, and they would go on deciding long after the approved
/// ones had moved.
/// </para>
/// <para>
/// Zero is the mark of a value nobody set. A window of zero would mean the
/// content is abandoned the moment the state is reached, which is the same
/// product decision taken by an omission, so the aggregate treats it as a
/// window that never runs out and the startup guard refuses it outright.
/// </para>
/// </summary>
/// <param name="UnstartedUpload">
/// How long an attachment whose bytes never arrived is kept. What it governs
/// is the key rather than the record: nothing was stored under it by this
/// module, and what may be there is a write whose answer was lost.
/// </param>
/// <param name="UnvalidatedContent">
/// How long content nobody ever asked a verdict about is kept. Asking is the
/// producer's own next step and it can be taken at any time, so the window is
/// the whole of what separates a producer that is slow from one that is gone.
/// </param>
/// <param name="RefusedContent">
/// How long the content of a refused attachment is kept. Nothing can release
/// it any more, so what the window buys is the reading of the bytes that were
/// refused, and what it costs is holding content that a check turned down.
/// </param>
/// <param name="WithdrawnRelease">
/// How long the content of an attachment whose release was taken back is kept.
/// The content was approved once and the approval was withdrawn, so this is a
/// window over legitimate bytes and not over refused ones.
/// </param>
public readonly record struct AttachmentRetentionWindows(
    TimeSpan UnstartedUpload,
    TimeSpan UnvalidatedContent,
    TimeSpan RefusedContent,
    TimeSpan WithdrawnRelease);
