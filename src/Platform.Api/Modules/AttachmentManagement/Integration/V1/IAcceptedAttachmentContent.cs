namespace NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

/// <summary>
/// Whether the content behind an accepted attachment was handed over. Two
/// answers, and only one of them yields bytes.
/// </summary>
public enum AcceptedAttachmentContentStatus
{
    /// <summary>
    /// The content was not handed over: the handle names nothing this module
    /// recorded, the record is gone, or the custody could not be reached.
    /// <para>
    /// One word for the three, because the difference between them changes
    /// nothing the caller does: none of them yields bytes, and a caller with
    /// no bytes has no message to compose. Which of the three closed is on
    /// this module's own operational line.
    /// </para>
    /// <para>
    /// It is the value zero on purpose. A status nobody set, and a stand-in
    /// that was never told what to answer, both read as this one, and the
    /// alternative is a default that reports content a caller then reads as an
    /// empty stream and sends as an empty attachment.
    /// </para>
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The content was handed over and <see cref="AcceptedAttachmentContent.Stream"/>
    /// carries it.
    /// </summary>
    Opened,
}

/// <summary>
/// One reading of the content behind an accepted attachment. Disposing it
/// releases the reading and everything the custody handed over with it.
/// <para>
/// The stream reads forward once. It has no length and no seek, because that
/// is what a remote reading offers, and a caller that needed to know the size
/// in advance already holds the length the release was granted over.
/// </para>
/// </summary>
public sealed class AcceptedAttachmentContent : IDisposable
{
    private readonly IDisposable? _owner;

    private AcceptedAttachmentContent(
        AcceptedAttachmentContentStatus status,
        Stream? stream,
        IDisposable? owner)
    {
        Status = status;
        Stream = stream;
        _owner = owner;
    }

    public AcceptedAttachmentContentStatus Status { get; }

    /// <summary>The content, or nothing when it was not handed over.</summary>
    public Stream? Stream { get; }

    /// <summary>
    /// A reading that yielded bytes, over whatever the custody handed the
    /// stream out with.
    /// </summary>
    public static AcceptedAttachmentContent Opened(Stream stream, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new AcceptedAttachmentContent(AcceptedAttachmentContentStatus.Opened, stream, owner);
    }

    /// <summary>A reading that yielded nothing.</summary>
    public static AcceptedAttachmentContent Unavailable()
        => new(AcceptedAttachmentContentStatus.Unavailable, null, null);

    public void Dispose()
    {
        Stream?.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>
/// Hands over the content of one accepted attachment, named by the opaque
/// handle the snapshot carries.
/// <para>
/// This is the way to the bytes and the only one. The handle is resolved here,
/// against this module's own record of which generation was captured and where
/// it lives, so no consumer learns a store, a key, a generation, a managed key
/// or an address, and nothing a consumer holds can be exchanged for the
/// content anywhere but here. A consumer that reached the custody itself would
/// be a second authority over which bytes an accepted attachment is, free to
/// read a generation the release never named.
/// </para>
/// <para>
/// It says nothing about eligibility. Whether the set may still leave is the
/// release check, asked immediately before the call that cannot be taken back;
/// this one is asked after that, by the path that actually composes the
/// message, and it opens exactly the generation the handle names rather than
/// whatever the key points at now.
/// </para>
/// <para>
/// It hands over a reading and never the bytes: the content is never
/// materialized on this side of the boundary, which is what lets a caller
/// stream an attachment of any size at a fixed cost.
/// </para>
/// </summary>
public interface IAcceptedAttachmentContent
{
    /// <summary>
    /// Opens the content the handle names. The caller disposes what it gets
    /// back, whatever the answer.
    /// </summary>
    Task<AcceptedAttachmentContent> OpenAsync(
        string contentIdentity,
        CancellationToken cancellationToken);
}
