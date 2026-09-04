using System.Net.Http.Headers;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Domain;

/// <summary>
/// Every state an attachment can be in. They are stored as written here, so a
/// name longer than the mapped column would only fail when a real transition
/// tried to persist it, which is why a test holds these names against the
/// mapping instead of against a number repeated here.
/// </summary>
public static class AttachmentStates
{
    public const string AwaitingUpload = "awaiting-upload";
    public const string Received = "received";

    /// <summary>
    /// The verdict did not conclude. The attachment is not released and not
    /// refused, and it stays here only until its own deadline passes.
    /// </summary>
    public const string Inconclusive = "validation-inconclusive";

    /// <summary>The one state an explicit approval leads to, and nothing else does.</summary>
    public const string Released = "released";

    /// <summary>
    /// The attachment is refused for good. Two things lead here and both are
    /// final: a check that refused the content, and a wait for a verdict that
    /// reached its deadline. Which of the two is read from the durable detail,
    /// because the public answer is one word for the whole family.
    /// </summary>
    public const string Rejected = "rejected";

    /// <summary>
    /// The release this attachment carried was taken back. It is not a
    /// verdict and it says nothing about the bytes: the content was approved
    /// and the approval was withdrawn afterwards, which is why it is a word of
    /// its own rather than a second way of spelling a refusal.
    /// <para>
    /// It is as final as a refusal is. Nothing here reopens it, and only an
    /// explicit revalidation, which writes a release of its own with an
    /// instant of its own, could ever grant another one.
    /// </para>
    /// </summary>
    public const string Revoked = "revoked";

    /// <summary>
    /// The content is gone. It is written by the sweep that removes the bytes
    /// of an attachment nothing is doing anything with, and only after the
    /// store has confirmed that nothing is left under the key, so the record
    /// outlives the content and says so instead of going silent.
    /// <para>
    /// It is the one state that says nothing about a verdict. Four states
    /// reach it, and the act that ended each of them keeps its own trace: the
    /// durable detail, the instant of the ending and the record of what was
    /// stored are all left exactly as they were, because they are the only
    /// remaining answer to what this attachment was.
    /// </para>
    /// </summary>
    public const string Discarded = "discarded";

    /// <summary>
    /// The states whose content can be abandoned, and the only ones a sweep
    /// may take bytes from. It is the published form of the rule the aggregate
    /// applies: the aggregate answers when the content of one attachment stops
    /// being kept, and this is the same question asked of the state alone, so
    /// a reader of the schema and a reader of the aggregate see one list.
    /// <para>
    /// The three that are missing are missing on purpose. A release is what
    /// the whole flow exists to produce, a wait for a verdict has an owner and
    /// a deadline of its own, and an attachment whose content is already gone
    /// has nothing left to take.
    /// </para>
    /// </summary>
    public static readonly string[] Discardable =
        [AwaitingUpload, Received, Rejected, Revoked];
}

public enum AttachmentReceiveOutcome
{
    Received,
    SizeMismatch,
    AlreadyReceived,
}

/// <summary>What a validation transition did, or why it did nothing.</summary>
public enum AttachmentValidationTransition
{
    /// <summary>The attachment now carries the state the transition names.</summary>
    Applied,

    /// <summary>
    /// The content is not there yet, so there is nothing to decide about. A
    /// verdict over an attachment nobody uploaded would be a verdict over no
    /// bytes.
    /// </summary>
    NotReceived,

    /// <summary>
    /// The attachment is already settled. Nothing here reopens it and nothing
    /// here grants a second release: a second release is a second row with an
    /// instant of its own, and only an explicit revalidation writes it.
    /// <para>
    /// Three states settle an attachment and they arrive here as one answer: a
    /// release, a refusal, and a release that was taken back. A caller that
    /// has to tell them apart reads the state, because the difference is the
    /// state and never the reason a verdict was refused.
    /// </para>
    /// </summary>
    AlreadyDecided,
}

/// <summary>What a discard did, or why it did nothing.</summary>
public enum AttachmentDiscardTransition
{
    /// <summary>The attachment now says its content is gone.</summary>
    Applied,

    /// <summary>
    /// Nothing here is abandoned. Either the state is one nobody may discard
    /// from, or the window this module keeps that state for has not run out.
    /// It is the same answer for both, because a caller acts on neither: what
    /// it may do is offer the attachment again later.
    /// </summary>
    NotAbandoned,
}

/// <summary>What a revocation did, or why it did nothing.</summary>
public enum AttachmentRevocationTransition
{
    /// <summary>The release was taken back by this call.</summary>
    Applied,

    /// <summary>
    /// The release was already taken back before this call, so this one
    /// changed nothing. It is the answer a retry gets after a lost response,
    /// and it is not a failure: the caller asked for a state the attachment
    /// already carries.
    /// </summary>
    AlreadyRevoked,

    /// <summary>
    /// There is no release here to take back. An attachment that was never
    /// released has no approval to withdraw, and one that was refused never
    /// had one either, so both leave through this answer instead of through a
    /// silent success that would read as a revocation nobody performed.
    /// </summary>
    NotReleased,
}

public sealed class Attachment
{
    public const int MaxApplicationLength = 100;
    public const int MaxFileNameLength = 255;
    public const int MaxContentTypeLength = 200;

    /// <summary>
    /// Room for the fine detail of the last validation outcome. It is durable
    /// state and never a public answer: the public vocabulary carries one
    /// reason for the whole family of content refusals, and which check
    /// refused is read from here by the authorized query.
    /// <para>
    /// The width is a ceiling and not a measurement, so a detail longer than
    /// it is refused rather than cut down. A truncated detail reads back as a
    /// word nobody wrote, and the one reader of this column is an
    /// investigation.
    /// </para>
    /// </summary>
    public const int MaxValidationDetailLength = 40;

    private Attachment()
    {
        Reference = null!;
        Application = null!;
        FileName = null!;
        ContentType = null!;
        State = null!;
    }

    public Guid Id { get; private set; }

    public AttachmentReference Reference { get; private set; }

    public string Application { get; private set; }

    public string FileName { get; private set; }

    public string ContentType { get; private set; }

    public long SizeBytes { get; private set; }

    public Guid ContentId { get; private set; }

    public string State { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }

    /// <summary>
    /// Which check refused, or which verdict did not conclude. Null while
    /// nothing has been decided about the content.
    /// </summary>
    public string? ValidationDetail { get; private set; }

    /// <summary>
    /// When the tolerance for a verdict that does not conclude runs out. It is
    /// written once, by the first verdict that does not conclude, and no later
    /// verdict, claim, attempt or retry moves it: a deadline any of those
    /// restarted would be a deadline that never arrives.
    /// </summary>
    public DateTimeOffset? InconclusiveUntil { get; private set; }

    /// <summary>
    /// When the attachment reached the state that ended it. The refusal and
    /// the withdrawal write it, because those are the two acts nothing
    /// reopens, and it is the clock the retention of both is counted from.
    /// <para>
    /// A column of its own because neither instant exists anywhere else, and
    /// neither can be inferred from the ones that do. A refusal writes the
    /// state and the detail and nothing else; the withdrawal writes a row that
    /// dates the grant it took back rather than the attachment. And the
    /// receipt is as far from the refusal as the producer's next call is, so a
    /// retention counted from it would end an attachment that was refused a
    /// minute ago.
    /// </para>
    /// <para>
    /// The discard leaves it as it found it. Overwriting it there would erase
    /// the instant that made the attachment discardable in the first place,
    /// and the reading that asks why the content is gone would find the answer
    /// replaced by the act that removed it.
    /// </para>
    /// </summary>
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>
    /// The repair this attachment is owed, or nothing when it owes none. It is
    /// the whole reason a reconciliation exists as a round rather than as a
    /// walk of everything: the rows that owe a repair are the ones a failure
    /// outside the transaction left behind, and there is no other durable
    /// place they are written down.
    /// <para>
    /// Two writers reach it and they are not the same kind of write. The
    /// transitions below own it while they own the row, and they set it and
    /// clear it in the transaction that moves the state. The upload path owns
    /// the other value and cannot use them, because it writes precisely when
    /// its own transaction failed and there is no aggregate left to save; it
    /// annotates the row with a statement of its own, guarded so it never
    /// overwrites a repair already recorded.
    /// </para>
    /// </summary>
    public string? ReconciliationLiability { get; private set; }

    /// <summary>
    /// Registers the metadata of an attachment whose bytes have not arrived
    /// yet, refusing anything the module could not hold.
    /// <para>
    /// The ceiling arrives as an argument because it is an approved value and
    /// not a shape of the aggregate. Written here as a constant it would be a
    /// product limit chosen by whoever wrote this file, and it would keep on
    /// admitting sizes long after the approved one had moved.
    /// </para>
    /// </summary>
    public static Result<Attachment> Register(
        string application,
        string fileName,
        string contentType,
        long sizeBytes,
        long maxSizeBytes,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(application)
            || application.Length > MaxApplicationLength
            || string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > MaxFileNameLength
            || !IsValidMediaType(contentType)
            || contentType.Length > MaxContentTypeLength
            || sizeBytes <= 0
            || sizeBytes > maxSizeBytes)
        {
            return Result.ValidationError<Attachment>(ErrorCodes.InvalidMetadata);
        }

        return Result.Success(new Attachment
        {
            Id = Guid.CreateVersion7(),
            Reference = AttachmentReference.Generate(),
            Application = application,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            ContentId = Guid.NewGuid(),
            State = AttachmentStates.AwaitingUpload,
            CreatedAt = createdAt,
        });
    }

    public AttachmentReceiveOutcome MarkReceived(
        long receivedSizeBytes,
        DateTimeOffset receivedAt)
    {
        if (State == AttachmentStates.Received)
        {
            return AttachmentReceiveOutcome.AlreadyReceived;
        }

        if (receivedSizeBytes != SizeBytes)
        {
            return AttachmentReceiveOutcome.SizeMismatch;
        }

        State = AttachmentStates.Received;
        ReceivedAt = receivedAt;
        return AttachmentReceiveOutcome.Received;
    }

    /// <summary>
    /// Why a verdict may not act on this attachment, or nothing when one may.
    /// It guards every transition below and it is also asked before a verdict
    /// is requested at all, so an attachment that is already settled costs no
    /// call to whatever produces one.
    /// </summary>
    public AttachmentValidationTransition? VerdictRefusal()
        => State switch
        {
            AttachmentStates.Received or AttachmentStates.Inconclusive => null,
            AttachmentStates.AwaitingUpload => AttachmentValidationTransition.NotReceived,
            _ => AttachmentValidationTransition.AlreadyDecided,
        };

    /// <summary>
    /// The only way into the released state. Every other outcome, and the
    /// absence of one, leaves the attachment where it was.
    /// </summary>
    public AttachmentValidationTransition Release()
    {
        if (VerdictRefusal() is { } refusal)
        {
            return refusal;
        }

        State = AttachmentStates.Released;

        // The detail of an earlier verdict that did not conclude stops
        // describing the attachment the moment it is released. The deadline of
        // that verdict stays: it is the trace that this attachment once sat
        // waiting, and it is read only while the waiting state is the one the
        // attachment carries.
        ValidationDetail = null;

        // The wait is over, so the repair it was owed is over with it, in the
        // same transaction that ends the wait. Left behind, it would hand the
        // round a released attachment to close a verdict on.
        ReconciliationLiability = null;
        return AttachmentValidationTransition.Applied;
    }

    /// <summary>Ends the attachment for good, naming the check that refused it.</summary>
    public AttachmentValidationTransition Reject(string detail, DateTimeOffset endedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (VerdictRefusal() is { } refusal)
        {
            return refusal;
        }

        State = AttachmentStates.Rejected;
        ValidationDetail = detail;

        // The instant the attachment ended, written by the act that ends it
        // and by nothing later. Whoever ends it is the only one who knows
        // when, and the retention of a refusal is counted from here.
        EndedAt = endedAt;
        ReconciliationLiability = null;
        return AttachmentValidationTransition.Applied;
    }

    /// <summary>
    /// Records a verdict that did not conclude, and starts the tolerance for
    /// it when this is the first one.
    /// </summary>
    public AttachmentValidationTransition HoldInconclusive(
        string detail,
        DateTimeOffset now,
        TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (VerdictRefusal() is { } refusal)
        {
            return refusal;
        }

        State = AttachmentStates.Inconclusive;
        ValidationDetail = detail;

        // Assigned only while there is nothing there. A plain assignment would
        // push the deadline forward on every repeated verdict, and a deadline
        // every retry renews is a deadline nothing ever reaches.
        InconclusiveUntil ??= now + window;

        // Written here, and not by whoever notices later, because this is the
        // transaction that creates the wait. A round that had to find these
        // rows by the state and the deadline would be reading two columns that
        // describe the attachment and inferring a repair from them; the value
        // written here is the repair itself, and it is what the index over
        // outstanding work is built on.
        ReconciliationLiability = AttachmentLiabilities.VerdictOpen;
        return AttachmentValidationTransition.Applied;
    }

    /// <summary>
    /// Why a revocation may not act on this attachment, or nothing when one
    /// may. It guards the transition below and it is also asked before the
    /// release a revocation would name is looked up at all, so an attachment
    /// nobody released costs no search for one.
    /// </summary>
    public AttachmentRevocationTransition? RevocationRefusal()
        => State switch
        {
            AttachmentStates.Released => null,
            AttachmentStates.Revoked => AttachmentRevocationTransition.AlreadyRevoked,
            _ => AttachmentRevocationTransition.NotReleased,
        };

    /// <summary>
    /// Takes back the release this attachment carries. It is the only way out
    /// of the released state and it reaches that state from nowhere else.
    /// <para>
    /// It is not a revalidation and it restarts nothing. The attachment ends
    /// here, and the durable detail of the validation is left exactly as it
    /// was: a revocation is an act over the grant, not a reading of the
    /// content, and writing a validation detail for it would say a check
    /// refused bytes that no check ever looked at again.
    /// </para>
    /// </summary>
    public AttachmentRevocationTransition Revoke(DateTimeOffset endedAt)
    {
        if (RevocationRefusal() is { } refusal)
        {
            return refusal;
        }

        State = AttachmentStates.Revoked;

        // The same instant the row of the withdrawal carries. The row dates
        // the grant that was taken back and this dates the attachment, and the
        // retention of a withdrawal is counted from here rather than from a
        // row that a reading would have to go and find.
        EndedAt = endedAt;
        return AttachmentRevocationTransition.Applied;
    }

    /// <summary>
    /// When the content of this attachment stops being kept, or nothing when
    /// nothing about it is abandoned.
    /// <para>
    /// Four states can be abandoned and each one counts from the last event
    /// that could still have changed it: the registration for an upload that
    /// never started, the arrival of the bytes for content nobody ever asked a
    /// verdict about, and the ending itself for a refusal and for a withdrawal.
    /// None of them counts from the creation, which would put a ceiling on the
    /// life of an attachment that is being used exactly as intended.
    /// </para>
    /// <para>
    /// Three states are absent and each absence is a decision. A release is
    /// what the whole flow exists to produce, and its expiry is computed from
    /// a validity read at comparison time, so a discard keyed on it would
    /// destroy bytes that the next change to that value would make usable
    /// again. A wait for a verdict that did not conclude already has an owner
    /// and a deadline of its own, and it lands in a refusal, where this clock
    /// starts. And an attachment whose content is already gone has nothing
    /// left to take.
    /// </para>
    /// <para>
    /// A window nobody set is not a window of zero. Zero would mean the
    /// content is abandoned the instant the state is reached, which is a
    /// decision about the product taken by an omission, so it answers that
    /// nothing is discardable and the startup guard refuses the value long
    /// before anything reaches here.
    /// </para>
    /// </summary>
    public DateTimeOffset? DiscardableFrom(AttachmentRetentionWindows windows)
        => State switch
        {
            AttachmentStates.AwaitingUpload =>
                Deadline(CreatedAt, windows.UnstartedUpload),
            AttachmentStates.Received =>
                Deadline(ReceivedAt, windows.UnvalidatedContent),
            AttachmentStates.Rejected =>
                Deadline(EndedAt, windows.RefusedContent),
            AttachmentStates.Revoked =>
                Deadline(EndedAt, windows.WithdrawnRelease),
            _ => null,
        };

    /// <summary>
    /// Records that the content of an abandoned attachment is gone. It says
    /// nothing about who removed it and it removes nothing itself: the caller
    /// has already had the store confirm every removal under the key, and this
    /// is the durable statement that it happened.
    /// <para>
    /// Nothing here consults the dependencies. What protects an attachment
    /// something still depends on is the operation that removes the bytes, and
    /// a second reading of the same rule here would be a second place for it
    /// to be wrong.
    /// </para>
    /// </summary>
    public AttachmentDiscardTransition Discard(
        DateTimeOffset now,
        AttachmentRetentionWindows windows)
    {
        if (DiscardableFrom(windows) is not { } deadline || now < deadline)
        {
            return AttachmentDiscardTransition.NotAbandoned;
        }

        State = AttachmentStates.Discarded;

        // A reclaim owed over a key whose content has just been removed is a
        // reclaim that has been carried out. Left behind, it would send the
        // repair round back to a key it has nothing to take from, round after
        // round, for an attachment nobody can upload to any more.
        ReconciliationLiability = null;
        return AttachmentDiscardTransition.Applied;
    }

    /// <summary>
    /// Whether the tolerance for a verdict that never concluded has run out.
    /// It answers only about an attachment that is waiting on one.
    /// </summary>
    public bool InconclusiveWindowHasElapsed(DateTimeOffset now)
        => State == AttachmentStates.Inconclusive
            && InconclusiveUntil is { } deadline
            && now >= deadline;

    /// <summary>
    /// Whether the durable state can hold this detail as written. A detail it
    /// cannot hold is not written short: it is treated as no answer at all.
    /// </summary>
    internal static bool IsUsableDetail(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxValidationDetailLength;

    /// <summary>
    /// The instant a window that started at <paramref name="from"/> runs out,
    /// or nothing when either half is missing. A window of zero or less is a
    /// window nobody set, and it never runs out.
    /// </summary>
    private static DateTimeOffset? Deadline(DateTimeOffset? from, TimeSpan window)
        => from is { } start && window > TimeSpan.Zero ? start + window : null;

    internal static bool IsValidMediaType(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && MediaTypeHeaderValue.TryParse(value, out _);
}
