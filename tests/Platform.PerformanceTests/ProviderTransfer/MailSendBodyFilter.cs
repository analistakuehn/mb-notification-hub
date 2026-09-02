using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace NotificationHub.PerformanceTests.ProviderTransfer;

/// <summary>How much of the captured body the double reconstructs.</summary>
internal enum CaptureDepth
{
    /// <summary>Only the digest and the length of the raw body.</summary>
    BodyDigest,

    /// <summary>Also the members, and each attachment decoded from its base64.</summary>
    Decoded,
}

/// <summary>One string value that was too long to keep, decoded as it went by.</summary>
internal sealed record CapturedLargeValue(
    string Marker,
    long Base64Bytes,
    long DecodedBytes,
    string DecodedSha256,
    bool DecodedSuccessfully);

/// <summary>What the filter produced once the body ended.</summary>
internal sealed record MailSendBodyContent(
    long BodyBytes,
    string BodySha256,
    byte[] ShrunkJson,
    IReadOnlyList<CapturedLargeValue> LargeValues);

/// <summary>
/// Reads a Mail Send body without keeping it. Every byte feeds the digest of
/// the whole body; every string value shorter than the inline limit is copied
/// into a shrunk copy of the document, and every value longer than it is
/// decoded from base64 on the way past and replaced by a marker. What is left
/// to parse is a few hundred bytes of JSON whatever the attachment weighs, so
/// the double can answer for name, type, order, length and digest without ever
/// holding the twenty megabytes it was sent.
/// </summary>
internal sealed class MailSendBodyFilter : IDisposable
{
    private const int InlineStringLimit = 4_096;

    private readonly IncrementalHash _bodyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly ArrayBufferWriter<byte> _shrunk = new(4 * 1_024);
    private readonly List<CapturedLargeValue> _large = [];
    private readonly byte[] _pending = new byte[InlineStringLimit];
    private readonly string _marker;
    private readonly bool _reconstruct;

    private long _bodyBytes;
    private int _pendingCount;
    private bool _inString;
    private bool _escaped;
    private Base64Sink? _sink;

    internal MailSendBodyFilter(string marker, CaptureDepth depth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        _marker = marker;
        _reconstruct = depth is CaptureDepth.Decoded;
    }

    public void Dispose()
    {
        _bodyHash.Dispose();
        _sink?.Dispose();
    }

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        _bodyBytes += bytes.Length;
        _bodyHash.AppendData(bytes);
        if (!_reconstruct)
        {
            return;
        }

        while (!bytes.IsEmpty)
        {
            if (!_inString)
            {
                var quote = bytes.IndexOf((byte)'"');
                if (quote < 0)
                {
                    _shrunk.Write(bytes);
                    return;
                }

                _shrunk.Write(bytes[..quote]);
                bytes = bytes[(quote + 1)..];
                _inString = true;
                _pendingCount = 0;
                continue;
            }

            if (_escaped)
            {
                Accept(bytes[..1]);
                _escaped = false;
                bytes = bytes[1..];
                continue;
            }

            var stop = bytes.IndexOfAny((byte)'"', (byte)'\\');
            if (stop < 0)
            {
                Accept(bytes);
                return;
            }

            Accept(bytes[..stop]);
            if (bytes[stop] == (byte)'\\')
            {
                Accept(bytes.Slice(stop, 1));
                _escaped = true;
            }
            else
            {
                CloseString();
            }

            bytes = bytes[(stop + 1)..];
        }
    }

    internal MailSendBodyContent Complete()
    {
        if (_inString)
        {
            // A body that ends inside a string is a truncated body. The opening
            // quote goes in without its closing pair on purpose: the shrunk copy
            // has to stay unparseable, or a connection that died mid-attachment
            // would read as a well-formed message.
            _shrunk.Write("\""u8);
            _shrunk.Write(_pending.AsSpan(0, _pendingCount));
            _inString = false;
        }

        return new MailSendBodyContent(
            _bodyBytes,
            Convert.ToHexString(_bodyHash.GetHashAndReset()),
            _reconstruct ? _shrunk.WrittenSpan.ToArray() : [],
            _large);
    }

    private void Accept(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        if (_sink is not null)
        {
            _sink.Append(value);
            return;
        }

        if (_pendingCount + value.Length <= InlineStringLimit)
        {
            value.CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += value.Length;
            return;
        }

        _sink = new Base64Sink();
        _sink.Append(_pending.AsSpan(0, _pendingCount));
        _pendingCount = 0;
        _sink.Append(value);
    }

    private void CloseString()
    {
        _inString = false;
        _escaped = false;
        _shrunk.Write("\""u8);
        if (_sink is null)
        {
            _shrunk.Write(_pending.AsSpan(0, _pendingCount));
        }
        else
        {
            var marker = $"{_marker}-{_large.Count}";
            _large.Add(_sink.Complete(marker));
            _sink.Dispose();
            _sink = null;
            _shrunk.Write(Encoding.UTF8.GetBytes(marker));
        }

        _shrunk.Write("\""u8);
        _pendingCount = 0;
    }

    /// <summary>
    /// Decodes base64 as it arrives, in quartets, holding the last one back so
    /// that padding only ever meets the final block. It keeps the digest and
    /// the length of what it decoded and nothing else.
    /// </summary>
    private sealed class Base64Sink : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _decoded = ArrayPool<byte>.Shared.Rent(64 * 1_024);
        private readonly byte[] _carry = new byte[4];
        private int _carryCount;
        private long _base64Bytes;
        private long _decodedBytes;
        private bool _valid = true;

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_decoded);
            _hash.Dispose();
        }

        internal void Append(ReadOnlySpan<byte> utf8)
        {
            _base64Bytes += utf8.Length;
            if (!_valid)
            {
                return;
            }

            while (!utf8.IsEmpty)
            {
                if (_carryCount > 0)
                {
                    var take = Math.Min(4 - _carryCount, utf8.Length);
                    utf8[..take].CopyTo(_carry.AsSpan(_carryCount));
                    _carryCount += take;
                    utf8 = utf8[take..];
                    if (_carryCount < 4 || utf8.IsEmpty)
                    {
                        return;
                    }

                    if (!Decode(_carry.AsSpan(0, 4), finalBlock: false))
                    {
                        return;
                    }

                    _carryCount = 0;
                    continue;
                }

                if (utf8.Length <= 4)
                {
                    utf8.CopyTo(_carry);
                    _carryCount = utf8.Length;
                    return;
                }

                var usable = (utf8.Length - 1) / 4 * 4;
                if (!Decode(utf8[..usable], finalBlock: false))
                {
                    return;
                }

                utf8 = utf8[usable..];
            }
        }

        internal CapturedLargeValue Complete(string marker)
        {
            if (_valid && _carryCount > 0)
            {
                Decode(_carry.AsSpan(0, _carryCount), finalBlock: true);
            }

            return new CapturedLargeValue(
                marker,
                _base64Bytes,
                _decodedBytes,
                Convert.ToHexString(_hash.GetHashAndReset()),
                _valid);
        }

        private bool Decode(ReadOnlySpan<byte> block, bool finalBlock)
        {
            var maxInput = _decoded.Length / 3 * 4;
            while (!block.IsEmpty)
            {
                var take = Math.Min(block.Length, maxInput);
                OperationStatus status = Base64.DecodeFromUtf8(
                    block[..take],
                    _decoded,
                    out var consumed,
                    out var written,
                    isFinalBlock: finalBlock && take == block.Length);
                _hash.AppendData(_decoded.AsSpan(0, written));
                _decodedBytes += written;
                if (status is OperationStatus.InvalidData || consumed == 0)
                {
                    _valid = false;
                    return false;
                }

                block = block[consumed..];
            }

            return true;
        }
    }
}
