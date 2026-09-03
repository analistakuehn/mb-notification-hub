using System.Buffers;
using System.Buffers.Text;
using System.Net;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// The body of one Mail Send call, written onto the connection as it is read
/// out of custody.
/// <para>
/// Nothing here holds the message. The literal parts were composed before the
/// call and each attachment is opened when the connection is ready for it,
/// encoded in blocks and closed on the way out, so the memory one send costs
/// is the same whether it carries a kilobyte or the whole envelope. That is
/// the property this shape exists for: measured against the alternative that
/// materializes the message, the cost of holding it is the attachment itself,
/// and the cost of this one is a pair of fixed buffers.
/// </para>
/// <para>
/// The base64 of the content is written straight onto the connection, in the
/// alphabet the encoder produces and through no escape encoder at all, which
/// is what keeps the field the exact length the composition declared. Content
/// chosen by a sender therefore cannot lengthen the message.
/// </para>
/// </summary>
internal sealed class SendGridMailContent(
    SendGridMailBody body,
    IAcceptedAttachmentContent content) : HttpContent
{
    /// <summary>
    /// A multiple of three, so every block but the last encodes into whole
    /// quartets and the carry between blocks is at most two bytes.
    /// </summary>
    private const int ReadChunkBytes = 48 * 1_024;

    /// <summary>
    /// Stable reason of a body the custody could not supply. It is not a
    /// network fault, and telling the two apart is the difference between an
    /// operator reading that the provider is unreachable and reading that the
    /// bytes of an accepted attachment are.
    /// </summary>
    internal const string ContentUnavailable = "attachment-content-unavailable";

    /// <summary>
    /// Stable reason of a body whose attachment stopped matching the length
    /// the release was granted over. The declared length was computed from
    /// that value, so a source of another size makes the body disagree with
    /// what the provider was told to expect.
    /// </summary>
    internal const string ContentLengthChanged = "attachment-content-length-changed";

    private const string LengthMessage =
        "O conteúdo de um anexo do conjunto aceito não tem mais o comprimento sob o qual "
        + "foi liberado; o corpo declarado deixaria de descrever a mensagem enviada.";

    /// <summary>
    /// Why the body could not be finished, when it could not. The transport
    /// reports a body that stopped as a broken request, and a broken request
    /// says nothing about whose fault it was: this is what lets the caller
    /// tell a custody that did not hand the bytes over from a connection that
    /// dropped.
    /// </summary>
    internal string? Interrupted { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        for (var index = 0; index < body.Attachments.Count; index++)
        {
            await stream.WriteAsync(body.Segments[index], cancellationToken);
            await WriteAttachmentAsync(stream, body.Attachments[index], cancellationToken);
        }

        await stream.WriteAsync(body.Segments[^1], cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// The exact length of the body, known before a byte of it moves. It is
    /// declared rather than left to the transport to discover: a length the
    /// transport had to find out would mean holding the message to measure it,
    /// which is the cost this whole shape exists to avoid.
    /// </summary>
    protected override bool TryComputeLength(out long length)
    {
        length = body.DeclaredLength;
        return true;
    }

    /// <summary>
    /// Reads one attachment out of custody and writes it as base64, in blocks,
    /// keeping at most two bytes between them.
    /// <para>
    /// Two things stop the write, and both stop it in the middle of a request
    /// the provider has already begun to receive. That is the price of never
    /// holding the message, and it is paid where it costs least: the length
    /// was declared before the first byte moved, so a body that stops short is
    /// a request the provider cannot read as a complete message, and it
    /// delivers nothing.
    /// </para>
    /// </summary>
    private async Task WriteAttachmentAsync(
        Stream destination,
        SendGridAttachmentSlot slot,
        CancellationToken cancellationToken)
    {
        using AcceptedAttachmentContent open = await content.OpenAsync(
            slot.ContentIdentity, cancellationToken);
        if (open is not { Status: AcceptedAttachmentContentStatus.Opened, Stream: { } source })
        {
            Interrupted = ContentUnavailable;
            throw new IOException(
                "A custódia não entregou o conteúdo de um anexo do conjunto aceito; "
                + "a mensagem não pode ser composta e o envio não se completa.");
        }

        // Pooled and wiped on the way back: both buffers hold clear attachment
        // bytes, and the pool is shared by the whole process, so an array
        // returned as it is outlives this send in any memory dump taken later.
        var raw = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        var encoded = ArrayPool<byte>.Shared.Rent(Base64.GetMaxEncodedToUtf8Length(ReadChunkBytes));
        try
        {
            long read = 0;
            var carry = 0;
            while (true)
            {
                var take = await source.ReadAsync(
                    raw.AsMemory(carry, ReadChunkBytes - carry), cancellationToken);
                if (take == 0)
                {
                    break;
                }

                read += take;

                // Checked before the block is written and not after it: a
                // source that keeps delivering past what was declared would
                // otherwise put on the wire the very bytes that make the body
                // disagree with its declared length.
                if (read > slot.RawLength)
                {
                    Interrupted = ContentLengthChanged;
                    throw new IOException(LengthMessage);
                }

                var available = carry + take;
                var aligned = available - (available % 3);
                Base64.EncodeToUtf8(
                    raw.AsSpan(0, aligned), encoded, out _, out var wrote, isFinalBlock: false);
                await destination.WriteAsync(encoded.AsMemory(0, wrote), cancellationToken);
                carry = available - aligned;
                if (carry > 0)
                {
                    raw.AsSpan(aligned, carry).CopyTo(raw);
                }
            }

            if (carry > 0)
            {
                Base64.EncodeToUtf8(
                    raw.AsSpan(0, carry), encoded, out _, out var tail, isFinalBlock: true);
                await destination.WriteAsync(encoded.AsMemory(0, tail), cancellationToken);
            }

            if (read != slot.RawLength)
            {
                Interrupted = ContentLengthChanged;
                throw new IOException(LengthMessage);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw, clearArray: true);
            ArrayPool<byte>.Shared.Return(encoded, clearArray: true);
        }
    }
}
