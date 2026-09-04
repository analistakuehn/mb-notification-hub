namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Where the bytes of an attachment go, derived from the record and from
/// nothing else.
/// <para>
/// It is one function because the derivation has two readers that must not
/// disagree. The write places bytes there; the reconciliation goes looking for
/// bytes nobody accounted for and can only look where the write would have put
/// them. Two spellings of the same rule would leave the second one searching a
/// place the first never wrote to, and the search would come back empty and
/// report that nothing is owed.
/// </para>
/// </summary>
internal static class AttachmentObjectKeys
{
    private const string Folder = "attachments/";

    /// <summary>The key of one attachment's content, derived from its content identifier.</summary>
    internal static string For(Guid contentId) => $"{Folder}{contentId:N}";
}
