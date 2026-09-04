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
    public AttachmentValidationTransition Reject(string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (VerdictRefusal() is { } refusal)
        {
            return refusal;
        }

        State = AttachmentStates.Rejected;
        ValidationDetail = detail;
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
    public AttachmentRevocationTransition Revoke()
    {
        if (RevocationRefusal() is { } refusal)
        {
            return refusal;
        }

        State = AttachmentStates.Revoked;
        return AttachmentRevocationTransition.Applied;
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

    internal static bool IsValidMediaType(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && MediaTypeHeaderValue.TryParse(value, out _);
}
