namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Declares a length over a stream that cannot be sought, which is what lets
/// the write travel without buffering the content.
/// <para>
/// It also answers whether the source ran out before delivering that declared
/// length. The answer has one reader, the write path, and one purpose: the
/// transport failure a short body causes is otherwise indistinguishable from a
/// store that could not be reached, and the two belong to different callers.
/// </para>
/// </summary>
internal sealed class DeclaredLengthStream(Stream inner, long declaredLength) : Stream
{
    private long _delivered;
    private bool _sourceEnded;

    /// <summary>
    /// True once the source answered end of stream while it still owed bytes
    /// against the declared length.
    /// </summary>
    internal bool SourceEndedEarly => _sourceEnded && _delivered < declaredLength;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => declaredLength;

    public override long Position
    {
        get => inner.CanSeek ? inner.Position : _delivered;
        set
        {
            if (!inner.CanSeek)
            {
                throw new NotSupportedException();
            }

            inner.Position = value;
            RestartFrom(value);
        }
    }

    public override void Flush()
        => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
        => Track(inner.Read(buffer, offset, count), count);

    public override int Read(Span<byte> buffer)
        => Track(inner.Read(buffer), buffer.Length);

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => Track(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken), count);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
        => Track(await inner.ReadAsync(buffer, cancellationToken), buffer.Length);

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = inner.Seek(offset, origin);
        RestartFrom(position);
        return position;
    }

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _ = disposing;
        base.Dispose(disposing);
    }

    // The client library rewinds this stream and sends it again when it
    // retries, so what was delivered is counted per attempt. Measured against
    // the provider: a body three bytes long that promised four was sent three
    // times, and a running total across the attempts reads as more than the
    // declared length and hides the very shortfall it exists to detect.
    private void RestartFrom(long position)
    {
        _delivered = position;
        _sourceEnded = false;
    }

    // A read that asked for nothing answers nothing, and that is not an ended
    // source. Only a read that had room and came back empty is one.
    private int Track(int read, int requested)
    {
        if (read > 0)
        {
            _delivered += read;
        }
        else if (requested > 0)
        {
            _sourceEnded = true;
        }

        return read;
    }
}
