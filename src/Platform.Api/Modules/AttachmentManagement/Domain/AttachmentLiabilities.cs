namespace NotificationHub.Api.Modules.AttachmentManagement.Domain;

/// <summary>
/// Every outstanding repair an attachment can be carrying, as the durable
/// state spells it. An attachment carries at most one of them and usually
/// none, which is why the column that holds them is empty for almost every
/// row and why the index over it is built on the exception.
/// <para>
/// The vocabulary is closed and it names the repair rather than the incident.
/// A flag would say only that something is owed, and whoever ran the round
/// would have to rediscover which repair by reading the store, the record and
/// the clock again, which is exactly the reading the column exists to spare.
/// </para>
/// <para>
/// The two words are mutually exclusive by the state they can occur in, and
/// that is what lets one column hold both. Custody is owed only while the
/// bytes never arrived, and a verdict is owed only after they did.
/// </para>
/// </summary>
public static class AttachmentLiabilities
{
    /// <summary>
    /// Bytes are durable under the key this attachment derives, and the record
    /// of generations does not account for them. Every retry of the upload is
    /// refused by the conditional write while they are there, so the repair is
    /// to remove what the record does not claim and give the key back.
    /// </summary>
    public const string CustodyUnreclaimed = "custody-unreclaimed";

    /// <summary>
    /// A verdict did not conclude and the attachment is waiting on the
    /// deadline of that verdict. The repair is to close the wait once the
    /// deadline has passed, which nothing else does unless somebody happens to
    /// ask for a validation again.
    /// </summary>
    public const string VerdictOpen = "verdict-open";

    /// <summary>
    /// The whole vocabulary. It is also the width the column is mapped to, so
    /// a word longer than the mapping would fail on the write that used it
    /// rather than on a later reading of a truncated value.
    /// </summary>
    public static readonly string[] All = [CustodyUnreclaimed, VerdictOpen];

    /// <summary>Longest word the vocabulary holds, and the mapped width.</summary>
    public const int MaxLength = 30;

    public static bool IsKnown(string? value)
        => value is not null && Array.IndexOf(All, value) >= 0;
}
