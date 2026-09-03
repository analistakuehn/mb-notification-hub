using System.Globalization;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

/// <summary>
/// A stand-in for the custody of attachment bytes, handing out exactly what a
/// test planted under one content handle.
/// <para>
/// It counts what it was asked for. Half of what the composition promises is
/// about what it never asks: a message refused before the call has to leave
/// this at zero, and a zero that sits next to a one is the only way to tell a
/// refusal from a host that could not have opened anything anyway.
/// </para>
/// </summary>
public sealed class AttachmentCustodyDouble : IAcceptedAttachmentContent
{
    private readonly Dictionary<string, byte[]> _planted = new(StringComparer.Ordinal);

    /// <summary>Every handle this was asked to open, in order.</summary>
    public List<string> Opened { get; } = [];

    /// <summary>
    /// How long each read of the content takes. It is the knob that turns a
    /// custody into a slow one, which is the only way to ask what the deadline
    /// of a send covers: the body is read while the request is being written,
    /// so a reading that drags is a request that drags.
    /// </summary>
    public TimeSpan ReadDelay { get; init; }

    /// <summary>The handle of the attachment planted at one position.</summary>
    public static string HandleOf(int index)
        => "aci_" + index.ToString(CultureInfo.InvariantCulture);

    public AttachmentCustodyDouble Plant(int index, byte[] content)
    {
        _planted[HandleOf(index)] = content;
        return this;
    }

    public Task<AcceptedAttachmentContent> OpenAsync(
        string contentIdentity,
        CancellationToken cancellationToken)
    {
        lock (Opened)
        {
            Opened.Add(contentIdentity);
        }

        return Task.FromResult(_planted.TryGetValue(contentIdentity, out var content)
            ? AcceptedAttachmentContent.Opened(new ForwardOnlyContentStream(content, ReadDelay))
            : AcceptedAttachmentContent.Unavailable());
    }
}

/// <summary>
/// A reading that only goes forward, with no length and no seek, which is what
/// a remote reading offers. A stand-in that offered more would let a writer
/// pass here while depending on something production never has.
/// </summary>
internal sealed class ForwardOnlyContentStream(byte[] content, TimeSpan readDelay) : Stream
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
        var take = Math.Min(buffer.Length, content.Length - _position);
        content.AsSpan(_position, take).CopyTo(buffer);
        _position += take;
        return take;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (readDelay > TimeSpan.Zero)
        {
            await Task.Delay(readDelay, cancellationToken);
        }

        return Read(buffer.Span);
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
}
