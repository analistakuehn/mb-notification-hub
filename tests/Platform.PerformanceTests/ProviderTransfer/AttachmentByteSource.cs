using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NotificationHub.PerformanceTests.ProviderTransfer;

/// <summary>
/// Where an arm reads the attachment bytes from. The probe never depends on
/// the module that owns the object store, nor on a bucket: the shape below is
/// what a remote read offers, a forward-only stream of unknown length that
/// arrives in chunks and can stall, and an implementation backed by real
/// object storage would sit behind this same interface without any arm
/// noticing.
/// </summary>
internal interface IAttachmentByteSource
{
    string FileName { get; }

    string ContentType { get; }

    /// <summary>Bytes the registration recorded, known before the read starts.</summary>
    long Length { get; }

    /// <summary>Digest of those bytes, the oracle the provider double is compared against.</summary>
    string ContentSha256 { get; }

    /// <summary>Streams still open, which has to be zero once an operation ends.</summary>
    int OpenStreams { get; }

    ValueTask<Stream> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What the attachment bytes look like. The shape is not decoration: the
/// readable one carries no character the JSON encoder would escape, so a body
/// measurement over it comes out the same whichever writer call emitted the
/// field, and a run over it alone cannot tell a safe implementation from an
/// exploitable one.
/// </summary>
internal enum AttachmentContentShape
{
    /// <summary>
    /// Readable filler seeded from the file name. Its base64 is drawn from
    /// letters, digits and the slash, none of which any encoder escapes.
    /// </summary>
    Readable,

    /// <summary>
    /// The three bytes whose base64 is four plus signs, repeated. Every
    /// character of the encoded content is one the default JSON encoder
    /// escapes into six bytes, so this content and no other separates the
    /// writer call that encodes at the writer from the one that hands the
    /// alphabet to the encoder.
    /// </summary>
    Escapable,
}

/// <summary>
/// Deterministic stand-in for the remote read. It generates the bytes instead
/// of holding them, so a seven mebibyte attachment costs the probe nothing
/// to keep, and it hands them out in the chunk size and with the per-chunk
/// latency the run asks for.
/// </summary>
internal sealed class SyntheticAttachmentByteSource : IAttachmentByteSource
{
    /// <summary>Base64 of this triple is the plus sign four times over.</summary>
    private static readonly byte[] EscapablePattern = [0xFB, 0xEF, 0xBE];

    private readonly byte[] _pattern;
    private readonly int _chunkBytes;
    private readonly TimeSpan _latencyPerChunk;
    private int _openStreams;
    private int _streamsOpened;

    internal SyntheticAttachmentByteSource(
        long length,
        string fileName,
        string contentType,
        int chunkBytes,
        TimeSpan latencyPerChunk,
        AttachmentContentShape shape = AttachmentContentShape.Readable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
        Length = length;
        FileName = fileName;
        ContentType = contentType;
        Shape = shape;
        _chunkBytes = chunkBytes;
        _latencyPerChunk = latencyPerChunk;
        _pattern = shape is AttachmentContentShape.Escapable
            ? EscapablePattern
            : Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"attachment:{fileName};0123456789abcdef|"));
        ContentSha256 = Digest();
    }

    internal AttachmentContentShape Shape { get; }

    public string FileName { get; }

    public string ContentType { get; }

    public long Length { get; }

    public string ContentSha256 { get; }

    public int OpenStreams => Volatile.Read(ref _openStreams);

    /// <summary>Reads opened over the life of the source, residual or not.</summary>
    internal int StreamsOpened => Volatile.Read(ref _streamsOpened);

    public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _openStreams);
        Interlocked.Increment(ref _streamsOpened);
        Stream stream = new PatternStream(this);
        return ValueTask.FromResult(stream);
    }

    private void Release() => Interlocked.Decrement(ref _openStreams);

    private void Fill(Span<byte> destination, long offset)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = _pattern[(int)((offset + index) % _pattern.Length)];
        }
    }

    private string Digest()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Math.Min(Length, 64 * 1_024)];
        long produced = 0;
        while (produced < Length)
        {
            var take = (int)Math.Min(buffer.Length, Length - produced);
            Fill(buffer.AsSpan(0, take), produced);
            hash.AppendData(buffer.AsSpan(0, take));
            produced += take;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Forward-only, no length and no seek, because that is what a remote read
    /// offers. An arm that wants the size has to take it from the registration,
    /// and an arm that wants to rewind has to spool.
    /// </summary>
    private sealed class PatternStream(SyntheticAttachmentByteSource owner) : Stream
    {
        private long _position;
        private bool _released;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (owner._latencyPerChunk > TimeSpan.Zero)
            {
                Thread.Sleep(owner._latencyPerChunk);
            }

            var take = Produce(buffer);
            return take;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner._latencyPerChunk > TimeSpan.Zero)
            {
                // The floor is the platform timer, around fifteen milliseconds on
                // Windows: a latency asked for below that is served above it, and
                // the report says what was asked for and not what was slept.
                await Task.Delay(owner._latencyPerChunk, cancellationToken);
            }

            return Produce(buffer.Span);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_released)
            {
                _released = true;
                owner.Release();
            }

            base.Dispose(disposing);
        }

        private int Produce(Span<byte> buffer)
        {
            var remaining = owner.Length - _position;
            if (remaining <= 0 || buffer.IsEmpty)
            {
                return 0;
            }

            var take = (int)Math.Min(Math.Min(buffer.Length, owner._chunkBytes), remaining);
            owner.Fill(buffer[..take], _position);
            _position += take;
            return take;
        }
    }
}
