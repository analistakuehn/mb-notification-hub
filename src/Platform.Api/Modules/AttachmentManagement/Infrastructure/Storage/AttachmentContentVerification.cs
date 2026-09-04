using System.Buffers;
using System.Security.Cryptography;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Measures a generation by reading it back. What was counted while writing
/// describes the bytes that entered the wire; only a reading of the pinned
/// generation describes the bytes that stayed.
/// <para>
/// The leading bytes are recognized in the same pass, because the pass already
/// walks every byte and keeping the first few of them costs one copy of eight
/// bytes and one walk of a four entry table. A pass of its own would read the
/// whole object a third time, and a check that had to open the content would
/// have to buffer it, which is what the transfer measurement ruled out.
/// </para>
/// <para>
/// Cost of that choice, measured and not yet budgeted anywhere. The live set
/// stays constant because nothing here materializes the content, but the
/// allocation rate does not: the reading the client library hands back does
/// not override the asynchronous read over a memory region, so every buffer
/// handed to it is copied.
/// </para>
/// <para>
/// Measured against the provider double: one pass over a one mebibyte object
/// allocated 1,516,608 bytes and one pass over a four mebibyte object
/// allocated 5,848,072 bytes, a slope of 1.377 bytes allocated for every byte
/// read. The same pass over a plain synchronous stream allocated a flat 5,872
/// bytes at both sizes, so the slope belongs to the provider reading and not
/// to this loop. Extrapolated to the admitted size ceiling, one upload leaves
/// about forty megabytes of transient garbage inside the request path, and no
/// budget or concurrency limit describes it.
/// </para>
/// </summary>
internal static class AttachmentContentVerification
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Returns the proof of the pinned generation, or says why there is none.
    /// </summary>
    internal static async Task<AttachmentContentReading> ComputeAsync(
        IAttachmentObjectStore store,
        AttachmentObjectLocator locator,
        CancellationToken cancellationToken)
    {
        using AttachmentStoreOpen reading = await store.OpenAsync(locator, cancellationToken);
        if (reading is not { Status: AttachmentStoreOpenStatus.Opened, Content: { } content })
        {
            return reading.Status == AttachmentStoreOpenStatus.Missing
                ? AttachmentContentReading.Missing()
                : AttachmentContentReading.Unavailable();
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        // What the content starts with, kept while the pass runs so the table
        // can be asked once at the end. A first read may hand over fewer bytes
        // than the longest signature needs, so it is filled across reads
        // instead of taken from the first one.
        var prefix = new byte[AttachmentContentSignatures.MaxPrefixLength];
        var prefixLength = 0;
        try
        {
            long lengthBytes = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (prefixLength < prefix.Length)
                {
                    var take = Math.Min(read, prefix.Length - prefixLength);
                    buffer.AsSpan(0, take).CopyTo(prefix.AsSpan(prefixLength));
                    prefixLength += take;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                lengthBytes += read;
            }

            return AttachmentContentReading.Measured(
                AttachmentContentProof.Sha256Of(hash.GetHashAndReset(), lengthBytes),
                AttachmentContentSignatures.Detect(prefix.AsSpan(0, prefixLength)));
        }
        finally
        {
            // The buffer held clear attachment bytes and goes back to a pool
            // the whole process shares, so it is wiped on the way back. The
            // window is the whole rented array and not the size asked for,
            // because the last read only overwrites its own prefix, and the
            // array outlives the request in any memory dump taken afterwards.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
