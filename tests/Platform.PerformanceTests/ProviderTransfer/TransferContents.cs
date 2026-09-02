using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace NotificationHub.PerformanceTests.ProviderTransfer;

/// <summary>
/// Media type every arm declares, so a difference between arms cannot come
/// from a difference of header.
/// </summary>
internal static class MailSendMediaType
{
    internal static MediaTypeHeaderValue Json => new("application/json") { CharSet = "utf-8" };
}

/// <summary>
/// The body an arm that already holds everything sends. It writes the finished
/// array in chunks so the write stage has somewhere to be interrupted, which
/// costs nothing: the socket writes in chunks anyway.
/// </summary>
internal sealed class ObservedByteArrayContent : HttpContent
{
    private const int WriteChunkBytes = 64 * 1_024;

    private readonly byte[] _body;
    private readonly TransferInterrupter _interrupter;
    private readonly bool _declareLength;

    internal ObservedByteArrayContent(byte[] body, TransferInterrupter interrupter, bool declareLength)
    {
        _body = body;
        _interrupter = interrupter;
        _declareLength = declareLength;
        Headers.ContentType = MailSendMediaType.Json;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        await _interrupter.ObserveAsync(TransferStage.HttpWrite, 0, cancellationToken);
        while (offset < _body.Length)
        {
            var take = Math.Min(WriteChunkBytes, _body.Length - offset);
            await stream.WriteAsync(_body.AsMemory(offset, take), cancellationToken);
            offset += take;
            await _interrupter.ObserveAsync(TransferStage.HttpWrite, offset, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _declareLength ? _body.LongLength : 0;
        return _declareLength;
    }
}

/// <summary>
/// The body an arm that spooled to disk sends: a file read forward once and
/// pushed onto the connection, with the file handle closed when the content is.
/// </summary>
internal sealed class ObservedStreamContent : HttpContent
{
    private const int WriteChunkBytes = 64 * 1_024;

    private readonly Stream _source;
    private readonly long _length;
    private readonly TransferInterrupter _interrupter;
    private readonly bool _declareLength;

    internal ObservedStreamContent(
        Stream source,
        long length,
        TransferInterrupter interrupter,
        bool declareLength)
    {
        _source = source;
        _length = length;
        _interrupter = interrupter;
        _declareLength = declareLength;
        Headers.ContentType = MailSendMediaType.Json;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        // Pooled on purpose: a per-send array here would be charged to the
        // spool arm as if spooling required it, and it does not.
        var buffer = ArrayPool<byte>.Shared.Rent(WriteChunkBytes);
        try
        {
            long written = 0;
            await _interrupter.ObserveAsync(TransferStage.HttpWrite, 0, cancellationToken);
            while (true)
            {
                var read = await _source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                await _interrupter.ObserveAsync(TransferStage.HttpWrite, written, cancellationToken);
            }

            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _declareLength ? _length : 0;
        return _declareLength;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// The body an arm that holds nothing sends. It opens the attachment when the
/// connection is ready for it and closes it on the way out, cancelled or not,
/// so an interrupted send leaves no read open behind it.
/// </summary>
internal sealed class StreamingMailSendContent : HttpContent
{
    private readonly MailSendBodyLayout _layout;
    private readonly TransferPlan _plan;

    internal StreamingMailSendContent(MailSendBodyLayout layout, TransferPlan plan)
    {
        _layout = layout;
        _plan = plan;
        Headers.ContentType = MailSendMediaType.Json;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
        => ProviderTransferArms.MailSendBodyWriter.WriteAsync(
            stream,
            _layout,
            _plan.Sources,
            _plan.Interrupter,
            observeHttpWrite: true,
            cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = _plan.DeclareContentLength ? _layout.TotalBytes : 0;
        return _plan.DeclareContentLength;
    }
}
