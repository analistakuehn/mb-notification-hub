using System.Buffers;
using System.Security.Cryptography;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class AttachmentContentVerificationTests
{
    private const int BufferSize = 81920;

    private static readonly AttachmentObjectLocator Locator =
        AttachmentObjectLocator.FromStoredRow("custody-store", "attachments/6b4c1f7a", "AbCdEf01");

    [Fact]
    public async Task A_generation_the_store_cannot_find_is_told_apart_from_a_store_it_cannot_reach()
    {
        AttachmentContentReading missing = await AttachmentContentVerification.ComputeAsync(
            new AnsweringObjectStore(AttachmentStoreOpen.Missing),
            Locator,
            CancellationToken.None);
        AttachmentContentReading unavailable = await AttachmentContentVerification.ComputeAsync(
            new AnsweringObjectStore(AttachmentStoreOpen.Unavailable),
            Locator,
            CancellationToken.None);

        // Both used to arrive as the same empty answer, which left the caller
        // reporting an unavailable store for a generation that is simply gone.
        missing.Status.ShouldBe(AttachmentContentReadingStatus.Missing);
        missing.Proof.ShouldBeNull();
        unavailable.Status.ShouldBe(AttachmentContentReadingStatus.Unavailable);
        unavailable.Proof.ShouldBeNull();
    }

    [Fact]
    public async Task Clear_attachment_bytes_do_not_travel_back_into_the_shared_pool()
    {
        var sentinel = "attachment-clear-text-8f2c41d9"u8.ToArray();

        // The content is longer than the rented buffer, so the last read only
        // overwrites its own prefix and the tail of the array keeps whatever
        // the read before it left there. The sentinel sits in that tail.
        var content = new byte[150_000];
        RandomNumberGenerator.Fill(content);
        sentinel.CopyTo(content, 100_000);

        AttachmentContentReading reading = await AttachmentContentVerification.ComputeAsync(
            new StoredContentObjectStore(content),
            Locator,
            CancellationToken.None);

        reading.Status.ShouldBe(AttachmentContentReadingStatus.Measured);
        reading.Proof.ShouldNotBeNull().LengthBytes.ShouldBe(content.Length);

        var rented = new List<byte[]>();
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                rented.Add(ArrayPool<byte>.Shared.Rent(BufferSize));
            }

            // The pool is shared by the whole process, so a buffer that goes
            // back holding clear attachment bytes hands them to the next
            // tenant and shows up in any memory dump taken long afterwards.
            var contaminated = rented.Count(buffer => buffer.AsSpan().IndexOf(sentinel) >= 0);
            contaminated.ShouldBe(0);
        }
        finally
        {
            foreach (var buffer in rented)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
    }

    [Fact]
    public async Task The_leading_bytes_are_recognized_in_the_pass_that_measures_them()
    {
        var content = "%PDF-1.7 corpo do documento"u8.ToArray();

        AttachmentContentReading reading = await AttachmentContentVerification.ComputeAsync(
            new StoredContentObjectStore(content),
            Locator,
            CancellationToken.None);

        reading.DetectedContentType.ShouldBe("application/pdf");
        reading.Proof.ShouldNotBeNull().LengthBytes.ShouldBe(content.Length);
    }

    [Fact]
    public async Task Bytes_no_signature_describes_are_measured_and_left_unrecognized()
    {
        AttachmentContentReading reading = await AttachmentContentVerification.ComputeAsync(
            new StoredContentObjectStore("texto simples, sem assinatura"u8.ToArray()),
            Locator,
            CancellationToken.None);

        reading.Status.ShouldBe(AttachmentContentReadingStatus.Measured);
        reading.DetectedContentType.ShouldBeNull();
    }

    /// <summary>
    /// A reading is free to hand over one byte at a time, and the longest
    /// signature is eight bytes long. Taken from the first read alone, the
    /// prefix would be one byte and the type would come back unrecognized for
    /// content that is exactly what it says it is.
    /// </summary>
    [Fact]
    public async Task A_signature_split_across_reads_is_still_recognized()
    {
        byte[] content = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

        AttachmentContentReading reading = await AttachmentContentVerification.ComputeAsync(
            new StoredContentObjectStore(content, bytesPerRead: 1),
            Locator,
            CancellationToken.None);

        reading.DetectedContentType.ShouldBe("image/png");
        reading.Proof.ShouldNotBeNull().LengthBytes.ShouldBe(content.Length);
    }

    /// <summary>Hands out at most a fixed number of bytes per read.</summary>
    private sealed class DrippingStream(byte[] content, int bytesPerRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(Math.Min(bytesPerRead, buffer.Length), content.Length - _position);
            content.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class AnsweringObjectStore(Func<AttachmentStoreOpen> answer)
        : IAttachmentObjectStore
    {
        public Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            return Task.FromResult(answer());
        }

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StoredContentObjectStore(byte[] content, int bytesPerRead = 0)
        : IAttachmentObjectStore
    {
        public Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            return Task.FromResult(AttachmentStoreOpen.Opened(
                bytesPerRead > 0
                    ? new DrippingStream(content, bytesPerRead)
                    : new MemoryStream(content, writable: false),
                owner: null));
        }

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
