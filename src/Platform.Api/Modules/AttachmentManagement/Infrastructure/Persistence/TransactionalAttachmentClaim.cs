using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// Claims a whole set of attachments with parameterized SQL over the caller's
/// own transaction, so the claim and whatever the caller is committing become
/// durable together or not at all.
/// <para>
/// Nothing here opens a connection, begins a transaction, or commits one. The
/// connection under the caller's transaction is the only one these statements
/// run on, which is what makes the claim atomic with an acceptance this module
/// knows nothing about, and it is also why a second connection is not a
/// throughput question here but a correctness one: work split across two
/// connections cannot be one transaction.
/// </para>
/// <para>
/// The set is locked before it is read, in the order of the reference rather
/// than the order the request declared. Two requests that name the same
/// attachments in opposite orders would otherwise take the same two locks in
/// opposite orders, which is a deadlock the moment both are in flight. Sorting
/// by a key that is stable across callers makes every claimer walk the set the
/// same way, so the second one waits instead of dying. The snapshot that comes
/// back is still in the order the request declared, because the order of the
/// set is part of what was asked for.
/// </para>
/// <para>
/// The lock is taken for update rather than for share on purpose: it is the
/// same lock the disposal takes, and holding it is what keeps a claim from
/// confirming over bytes a disposal is about to remove or a revocation is
/// about to withdraw. The contention it adds between concurrent acceptances of
/// one attachment costs nothing measurable, because the acceptance transaction
/// already serializes on the chain lock of the audit trail.
/// </para>
/// </summary>
internal sealed class TransactionalAttachmentClaim(TimeProvider timeProvider) : IAttachmentClaim
{
    /// <summary>
    /// Prefix of the durable identity of a claim hold. It is what keeps the
    /// holds of a claim apart from the holds every other kind of dependent
    /// takes over the same attachments.
    /// </summary>
    private const string HolderPrefix = "claim_";

    /// <summary>
    /// Separates the two values the holder is derived from, in a character
    /// neither of them can carry. Joining them plainly would let one
    /// application's key and another's spell the same holder.
    /// </summary>
    private const char HolderSeparator = '\u001f';

    // The whole set, locked in a single statement and in an order that does
    // not depend on the caller. The lock sits above the sort in the plan, so
    // the rows are locked as they come out of it.
    private const string LockSetSql = """
        SELECT id, reference, application, state
        FROM attachmentmanagement.attachment
        WHERE reference = ANY(@references)
        ORDER BY reference
        FOR UPDATE
        """;

    // What this claim key already holds. It reads by holder alone, with no
    // predicate on the reason, because it has to see exactly what the write
    // below would collide with: the row that stops that write is keyed on the
    // attachment and the holder and on nothing else.
    private const string LiveHoldsSql = """
        SELECT attachment_id
        FROM attachmentmanagement.attachment_dependency
        WHERE holder = @holder AND released_at IS NULL
        """;

    // The release in force per attachment, with the generation it was granted
    // over. The latest one wins: a revalidation writes a second row with an
    // instant of its own, and the snapshot has to say which bytes are the ones
    // being accepted now.
    private const string ReleasedSetSql = """
        SELECT DISTINCT ON (release_row.attachment_id)
            release_row.attachment_id,
            release_row.generation_id,
            attachment_row.file_name,
            attachment_row.content_type,
            generation_row.length_bytes
        FROM attachmentmanagement.attachment_release AS release_row
        JOIN attachmentmanagement.attachment_object_generation AS generation_row
            ON generation_row.id = release_row.generation_id
        JOIN attachmentmanagement.attachment AS attachment_row
            ON attachment_row.id = release_row.attachment_id
        WHERE release_row.attachment_id = ANY(@attachmentIds)
        ORDER BY release_row.attachment_id, release_row.released_at DESC, release_row.id DESC
        """;

    public async Task<AttachmentClaimOutcome> ClaimAsync(
        DbTransaction transaction,
        AttachmentClaimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.NotificationId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Application)
            || string.IsNullOrWhiteSpace(request.ClaimKey))
        {
            throw new ArgumentException(
                "A claim names the notification it is taken for, the application it belongs to "
                    + "and the idempotent key of the caller.",
                nameof(request));
        }

        DbConnection connection = AttachmentTransaction.ConnectionOf(transaction);
        string[] references = [.. request.References];
        var holder = HolderOf(request.Application, request.ClaimKey);

        IReadOnlyList<LockedAttachment> locked = await LockSetAsync(
            connection, transaction, references, cancellationToken);

        // Identity first, and one answer for both halves of it. A reference
        // nobody minted and a reference that belongs to another application
        // are different facts, and telling them apart here would let a caller
        // learn which references exist by asking.
        if (locked.Count != references.Length
            || locked.Any(row => !string.Equals(
                row.Application, request.Application, StringComparison.Ordinal)))
        {
            return AttachmentClaimOutcome.Refused(AttachmentClaimStatus.NotClaimable);
        }

        Dictionary<string, LockedAttachment> byReference =
            locked.ToDictionary(row => row.Reference, StringComparer.Ordinal);
        Guid[] requested = [.. references.Select(reference => byReference[reference].Id)];

        // What this key already stands for is read before eligibility is, so
        // that a claim which already happened answers with the set it accepted
        // instead of being refused by a release that was taken back after it.
        // A repeat that started failing the day an attachment was revoked
        // would turn every retry of an accepted notification into a rejection.
        HashSet<Guid> held = await LiveHoldsAsync(connection, transaction, holder, cancellationToken);
        if (held.Count > 0)
        {
            return held.Count == requested.Length && Array.TrueForAll(requested, held.Contains)
                ? AttachmentClaimOutcome.Claimed(await SnapshotAsync(
                    connection, transaction, references, byReference, cancellationToken))
                : AttachmentClaimOutcome.Refused(AttachmentClaimStatus.ClaimKeyConflict);
        }

        if (locked.Any(row => !string.Equals(
            row.State, AttachmentStates.Released, StringComparison.Ordinal)))
        {
            return AttachmentClaimOutcome.Refused(AttachmentClaimStatus.NotClaimable);
        }

        DateTimeOffset acquiredAt = timeProvider.GetUtcNow();
        foreach (LockedAttachment row in locked)
        {
            var written = await AttachmentDependencyUpsert.ExecuteAsync(
                transaction,
                row.Id,
                AttachmentDependencyReasons.ClaimConfirmed,
                holder,
                acquiredAt,
                cancellationToken);

            // Zero rows means a live hold of this same key appeared between
            // the read above and this write. Nothing can produce one while
            // this transaction holds every row of the set, and the claim
            // refuses rather than reporting a set it did not write.
            if (written != 1)
            {
                return AttachmentClaimOutcome.Refused(AttachmentClaimStatus.NotClaimable);
            }
        }

        return AttachmentClaimOutcome.Claimed(await SnapshotAsync(
            connection, transaction, references, byReference, cancellationToken));
    }

    /// <summary>
    /// The durable identity of the work a claim holds for. It is derived and
    /// not carried: the caller's key is longer than the column that records a
    /// holder, and a holder is opaque by contract anyway.
    /// <para>
    /// Deriving it from the application and the idempotent key, and never from
    /// the notification, is what makes a repeat idempotent. A retry after a
    /// commit whose result was never learned mints a new notification
    /// identifier for the same request, and a holder built from it would take
    /// a second hold over the same attachments for one acceptance.
    /// </para>
    /// <para>
    /// It is derivable rather than recoverable, which is enough: an
    /// investigation holding the notification row holds the application and
    /// the idempotent key it was accepted under, and can compute this value.
    /// </para>
    /// </summary>
    internal static string HolderOf(string application, string claimKey)
        => HolderPrefix
            + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(application + HolderSeparator + claimKey)));

    private static async Task<IReadOnlyList<LockedAttachment>> LockSetAsync(
        DbConnection connection,
        DbTransaction transaction,
        string[] references,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LockSetSql;
        AttachmentTransaction.AddParameter(command, "references", references);

        var rows = new List<LockedAttachment>(references.Length);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LockedAttachment(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return rows;
    }

    private static async Task<HashSet<Guid>> LiveHoldsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string holder,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LiveHoldsSql;
        AttachmentTransaction.AddParameter(command, "holder", holder);

        var held = new HashSet<Guid>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            held.Add(reader.GetGuid(0));
        }

        return held;
    }

    /// <summary>
    /// The set as the claim accepted it, in the order the request declared.
    /// An attachment without a release in force leaves no member here, and the
    /// missing member is what stops the snapshot from being built: a set that
    /// is short one attachment is not the set that was asked for.
    /// </summary>
    private static async Task<AcceptedAttachmentSet> SnapshotAsync(
        DbConnection connection,
        DbTransaction transaction,
        string[] references,
        Dictionary<string, LockedAttachment> byReference,
        CancellationToken cancellationToken)
    {
        Guid[] attachmentIds = [.. byReference.Values.Select(row => row.Id)];
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReleasedSetSql;
        AttachmentTransaction.AddParameter(command, "attachmentIds", attachmentIds);

        var released = new Dictionary<Guid, ReleasedContent>();
        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                released.Add(
                    reader.GetGuid(0),
                    new ReleasedContent(
                        reader.GetGuid(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetInt64(4)));
            }
        }

        return AcceptedAttachmentSet.Of(references.Select(reference =>
        {
            LockedAttachment row = byReference[reference];
            if (!released.TryGetValue(row.Id, out ReleasedContent content))
            {
                throw new InvalidOperationException(
                    "An attachment this claim accepted carries no release to be accepted under.");
            }

            return new AcceptedAttachment
            {
                Reference = reference,
                ContentIdentity = AttachmentContentIdentity.For(content.GenerationId),
                Name = content.Name,
                MediaType = content.MediaType,
                Length = content.Length,
            };
        }));
    }

    private sealed record LockedAttachment(
        Guid Id,
        string Reference,
        string Application,
        string State);

    private readonly record struct ReleasedContent(
        Guid GenerationId,
        string Name,
        string MediaType,
        long Length);
}
