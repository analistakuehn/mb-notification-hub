using System.IO.Compression;
using System.Security.Cryptography;

namespace NotificationHub.Api.Modules.Audit.Domain;

/// <summary>Digest of exported evidence, in the hexadecimal form the manifest records.</summary>
internal static class AuditDigest
{
    internal static byte[] Compute(byte[] content) => SHA256.HashData(content);

    internal static string Hex(byte[] content) => AuditHex.ToHex(SHA256.HashData(content));
}

/// <summary>
/// Compression of the exported streams. It uses the runtime's own gzip, with
/// no dependency to keep alive for decades, and a fixed compression level, so
/// the same input always produces the same object and a rerun of an export
/// recognizes its own bytes instead of writing a second copy.
/// </summary>
internal static class AuditGzip
{
    internal static byte[] Compress(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content, 0, content.Length);
        }

        return output.ToArray();
    }

    internal static byte[] Decompress(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var input = new MemoryStream(content, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
