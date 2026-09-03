using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// A stand-in for the custody, handing out exactly what a test planted under
/// one content handle.
/// <para>
/// It counts what it was asked for, because half of what the composition
/// promises is about what it never asks: a message refused before the call
/// must leave this at zero, and a zero next to a one is the only way to tell a
/// refusal from a composition that could not open anything anyway.
/// </para>
/// </summary>
internal sealed class StubAcceptedAttachmentContent : IAcceptedAttachmentContent
{
    private readonly Dictionary<string, byte[]> _planted = new(StringComparer.Ordinal);

    /// <summary>How many bytes one read hands back at most.</summary>
    internal int ChunkBytes { get; init; } = int.MaxValue;

    /// <summary>Every handle this was asked to open, in order.</summary>
    internal List<string> Opened { get; } = [];

    internal StubAcceptedAttachmentContent Plant(string contentIdentity, byte[] content)
    {
        _planted[contentIdentity] = content;
        return this;
    }

    public Task<AcceptedAttachmentContent> OpenAsync(
        string contentIdentity,
        CancellationToken cancellationToken)
    {
        Opened.Add(contentIdentity);
        return Task.FromResult(_planted.TryGetValue(contentIdentity, out var content)
            ? AcceptedAttachmentContent.Opened(new ForwardOnlyStream(content, ChunkBytes))
            : AcceptedAttachmentContent.Unavailable());
    }
}

/// <summary>
/// A reading that only goes forward, in blocks, with no length and no seek.
/// <para>
/// That is what a remote reading offers, and a stand-in that offered more
/// would let the writer pass while depending on something production never
/// has. The block size is a knob because a writer that encodes in quartets has
/// to carry bytes between reads, and a stand-in that always answered in full
/// would never make it carry any.
/// </para>
/// </summary>
internal sealed class ForwardOnlyStream(byte[] content, int chunkBytes) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var take = Math.Min(Math.Min(buffer.Length, chunkBytes), content.Length - _position);
        content.AsSpan(_position, take).CopyTo(buffer);
        _position += take;
        return take;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
}
