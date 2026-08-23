using System.Numerics;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// Public form of a notification identity: <c>ntf_</c> followed by the 26
/// Crockford base32 characters of the stored UUID. The mapping is
/// deterministic and reversible: the 128 bits of the UUID are read in
/// big-endian (RFC 4122) byte order and encoded exactly like a ULID, so a
/// version-7 UUID yields a public id whose lexicographic order follows its
/// creation time. Parsing is strict: lowercase prefix, uppercase Crockford
/// alphabet without I, L, O or U, and a first character no greater than 7
/// (128 bits in 26 characters leave the top character 3 bits wide).
/// </summary>
internal static class NotificationId
{
    internal const string Prefix = "ntf_";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int EncodedLength = 26;

    /// <summary>Formats the stored UUID as its public <c>ntf_</c> form.</summary>
    internal static string Format(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);

        Span<char> encoded = stackalloc char[EncodedLength];
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        for (var position = EncodedLength - 1; position >= 0; position--)
        {
            encoded[position] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return $"{Prefix}{new string(encoded)}";
    }

    /// <summary>Parses a public <c>ntf_</c> form back into the stored UUID.</summary>
    internal static bool TryParse(string? value, out Guid id)
    {
        id = Guid.Empty;
        if (value is null
            || value.Length != Prefix.Length + EncodedLength
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        BigInteger accumulated = 0;
        foreach (var character in value.AsSpan(Prefix.Length))
        {
            var digit = Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (digit < 0)
            {
                return false;
            }

            accumulated = (accumulated << 5) | digit;
        }

        // 26 characters carry 130 bits; anything above 128 bits is invalid.
        if (accumulated >> 128 != 0)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!accumulated.TryWriteBytes(bytes, out var written, isUnsigned: true, isBigEndian: true))
        {
            return false;
        }

        // Right-align the value: leading zero bytes are not emitted.
        if (written < 16)
        {
            bytes[..written].CopyTo(bytes[(16 - written)..]);
            bytes[..(16 - written)].Clear();
        }

        id = new Guid(bytes, bigEndian: true);
        return true;
    }
}
