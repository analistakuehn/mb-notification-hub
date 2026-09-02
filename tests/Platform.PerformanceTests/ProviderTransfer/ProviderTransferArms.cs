using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace NotificationHub.PerformanceTests.ProviderTransfer;

/// <summary>Everything one attempt needs, so no signature carries eight things.</summary>
internal sealed record TransferPlan(
    string ArmId,
    MailSendEnvelope Envelope,
    IReadOnlyList<IAttachmentByteSource> Sources,
    string ApiKey,
    string? SpoolRoot,
    bool DeclareContentLength,
    TransferInterrupter Interrupter);

/// <summary>What one attempt did and what the provider answered.</summary>
internal sealed record TransferAttempt(
    string ArmId,
    int StatusCode,
    string Classification,
    long RequestBodyBytes,
    bool ContentLengthDeclared,
    string? ProviderMessageId,
    int TemporaryFilesCreated);

/// <summary>
/// The three ways of getting an attachment to the provider, doing the same
/// work: read the bytes, encode them as base64, compose the Mail Send body and
/// push it over a cancellable content.
/// <list type="bullet">
/// <item>buffer holds the attachment, the base64 and the body in memory;</item>
/// <item>streaming holds none of them and writes as it reads;</item>
/// <item>spool writes the body to a temporary file and sends the file.</item>
/// </list>
/// The bodies are identical by contract, and the provider double is what says
/// whether they are identical in fact.
/// </summary>
internal static class ProviderTransferArms
{
    internal const string BufferArm = "buffer";

    internal const string StreamingArm = "streaming";

    internal const string SpoolArm = "spool";

    /// <summary>A multiple of three, so every encoded block ends on a quartet.</summary>
    internal const int ReadChunkBytes = 48 * 1_024;

    private const int WriteChunkBytes = 64 * 1_024;

    internal static IReadOnlyList<string> All => [BufferArm, StreamingArm, SpoolArm];

    internal static Task<TransferAttempt> SendAsync(
        HttpClient client,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(plan);
        return plan.ArmId switch
        {
            BufferArm => ThroughBufferAsync(client, plan, cancellationToken),
            StreamingArm => ThroughStreamingAsync(client, plan, cancellationToken),
            SpoolArm => ThroughSpoolAsync(client, plan, cancellationToken),
            _ => throw new InvalidOperationException($"Braço de transferência desconhecido: {plan.ArmId}"),
        };
    }

    /// <summary>
    /// Composes the body the way an implementation that holds everything would:
    /// the whole attachment as an array and the serializer over the finished
    /// request, which materializes the whole message before a byte of it moves.
    /// It shares no composition code with the incremental arms, which is what
    /// makes the digest comparison worth running.
    /// </summary>
    private static async Task<TransferAttempt> ThroughBufferAsync(
        HttpClient client,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        TransferInterrupter interrupter = plan.Interrupter;
        var attachments = new MailSendAttachment[plan.Sources.Count];
        for (var index = 0; index < plan.Sources.Count; index++)
        {
            IAttachmentByteSource source = plan.Sources[index];
            await interrupter.ObserveAsync(TransferStage.SourceRead, 0, cancellationToken);
            var raw = await ReadAllAsync(source, interrupter, cancellationToken);
            await interrupter.ObserveAsync(TransferStage.Encode, 0, cancellationToken);
            await interrupter.ObserveAsync(
                TransferStage.Encode, MailSendLimits.Base64Length(raw.LongLength), cancellationToken);
            attachments[index] = new MailSendAttachment(
                new AttachmentContent(raw),
                source.FileName,
                source.ContentType,
                MailSendLimits.AttachmentDisposition);
        }

        var body = MailSendComposer.Serialize(plan.Envelope.Compose(attachments));
        using var content = new ObservedByteArrayContent(body, interrupter, plan.DeclareContentLength);
        return await PostAsync(client, plan, content, body.LongLength, cancellationToken);
    }

    private static async Task<TransferAttempt> ThroughStreamingAsync(
        HttpClient client,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        MailSendBodyLayout layout = MailSendComposer.Layout(plan.Envelope, plan.Sources, NewMarkerPrefix());
        using var content = new StreamingMailSendContent(layout, plan);
        return await PostAsync(client, plan, content, layout.TotalBytes, cancellationToken);
    }

    private static async Task<TransferAttempt> ThroughSpoolAsync(
        HttpClient client,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        var root = plan.SpoolRoot
            ?? throw new InvalidOperationException("O braço spool exige um diretório temporário.");
        MailSendBodyLayout layout = MailSendComposer.Layout(plan.Envelope, plan.Sources, NewMarkerPrefix());
        var path = Path.Combine(
            root,
            string.Create(
                CultureInfo.InvariantCulture,
                $"mail-send-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}.spool"));
        try
        {
            await using (var spool = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                WriteChunkBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await MailSendBodyWriter.WriteAsync(
                    spool, layout, plan.Sources, plan.Interrupter, observeHttpWrite: false, cancellationToken);
                await spool.FlushAsync(cancellationToken);
            }

            var spooled = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                WriteChunkBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var content = new ObservedStreamContent(
                spooled, spooled.Length, plan.Interrupter, plan.DeclareContentLength);
            TransferAttempt attempt = await PostAsync(client, plan, content, layout.TotalBytes, cancellationToken);
            return attempt with { TemporaryFilesCreated = 1 };
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<TransferAttempt> PostAsync(
        HttpClient client,
        TransferPlan plan,
        HttpContent content,
        long bodyBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MailSendComposer.Path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plan.ApiKey);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            return new TransferAttempt(
                plan.ArmId,
                status,
                Classify(response.StatusCode),
                bodyBytes,
                plan.DeclareContentLength,
                response.Headers.TryGetValues("X-Message-Id", out IEnumerable<string>? values)
                    ? values.FirstOrDefault()
                    : null,
                0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client ran out of patience. A cancellation the run asked for
            // is a different thing and is left to propagate.
            return new TransferAttempt(plan.ArmId, 0, "timeout", bodyBytes, plan.DeclareContentLength, null, 0);
        }
        catch (HttpRequestException)
        {
            return new TransferAttempt(plan.ArmId, 0, "network", bodyBytes, plan.DeclareContentLength, null, 0);
        }
    }

    private static string Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Accepted or HttpStatusCode.OK => "accepted",
        HttpStatusCode.TooManyRequests => "throttled",
        >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => "rejected",
        _ => "transient",
    };

    private static string NewMarkerPrefix()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"probe-slot-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}");

    private static async Task<byte[]> ReadAllAsync(
        IAttachmentByteSource source,
        TransferInterrupter interrupter,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await source.OpenAsync(cancellationToken);
        var raw = new byte[source.Length];
        var offset = 0;
        while (offset < raw.Length)
        {
            var read = await stream.ReadAsync(raw.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException(
                    $"A fonte {source.FileName} entregou {offset} bytes e prometera {source.Length}.");
            }

            offset += read;
            await interrupter.ObserveAsync(TransferStage.SourceRead, offset, cancellationToken);
        }

        return raw;
    }

    /// <summary>
    /// Writes the body one segment and one attachment at a time. The encoder
    /// keeps at most two bytes between chunks, so an attachment of any size
    /// costs the same fixed buffers, and the base64 it produces is identical to
    /// the one a single call would produce.
    /// </summary>
    internal static class MailSendBodyWriter
    {
        internal static async Task WriteAsync(
            Stream destination,
            MailSendBodyLayout layout,
            IReadOnlyList<IAttachmentByteSource> sources,
            TransferInterrupter interrupter,
            bool observeHttpWrite,
            CancellationToken cancellationToken)
        {
            long written = 0;
            for (var index = 0; index < sources.Count; index++)
            {
                written += await WriteSegmentAsync(
                    destination, layout.Segments[index], written, interrupter, observeHttpWrite, cancellationToken);
                written += await WriteAttachmentAsync(
                    destination, sources[index], written, interrupter, observeHttpWrite, cancellationToken);
            }

            await WriteSegmentAsync(
                destination, layout.Segments[^1], written, interrupter, observeHttpWrite, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        private static async Task<long> WriteSegmentAsync(
            Stream destination,
            byte[] segment,
            long written,
            TransferInterrupter interrupter,
            bool observeHttpWrite,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync(segment, cancellationToken);
            if (observeHttpWrite)
            {
                await interrupter.ObserveAsync(
                    TransferStage.HttpWrite, written + segment.Length, cancellationToken);
            }

            return segment.Length;
        }

        private static async Task<long> WriteAttachmentAsync(
            Stream destination,
            IAttachmentByteSource source,
            long written,
            TransferInterrupter interrupter,
            bool observeHttpWrite,
            CancellationToken cancellationToken)
        {
            var raw = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
            var encoded = ArrayPool<byte>.Shared.Rent(Base64.GetMaxEncodedToUtf8Length(ReadChunkBytes));
            try
            {
                await using Stream content = await source.OpenAsync(cancellationToken);
                await interrupter.ObserveAsync(TransferStage.SourceRead, 0, cancellationToken);
                await interrupter.ObserveAsync(TransferStage.Encode, 0, cancellationToken);
                long read = 0;
                long produced = 0;
                var carry = 0;
                while (true)
                {
                    var take = await content.ReadAsync(
                        raw.AsMemory(carry, ReadChunkBytes - carry), cancellationToken);
                    if (take == 0)
                    {
                        break;
                    }

                    read += take;
                    await interrupter.ObserveAsync(TransferStage.SourceRead, read, cancellationToken);

                    var available = carry + take;
                    var aligned = available - (available % 3);
                    Base64.EncodeToUtf8(
                        raw.AsSpan(0, aligned), encoded, out _, out var wrote, isFinalBlock: false);
                    produced += wrote;
                    await interrupter.ObserveAsync(TransferStage.Encode, produced, cancellationToken);
                    await destination.WriteAsync(encoded.AsMemory(0, wrote), cancellationToken);
                    if (observeHttpWrite)
                    {
                        await interrupter.ObserveAsync(
                            TransferStage.HttpWrite, written + produced, cancellationToken);
                    }

                    carry = available - aligned;
                    if (carry > 0)
                    {
                        raw.AsSpan(aligned, carry).CopyTo(raw);
                    }
                }

                if (carry > 0)
                {
                    Base64.EncodeToUtf8(
                        raw.AsSpan(0, carry), encoded, out _, out var tail, isFinalBlock: true);
                    produced += tail;
                    await destination.WriteAsync(encoded.AsMemory(0, tail), cancellationToken);
                }

                if (read != source.Length)
                {
                    throw new IOException(
                        $"A fonte {source.FileName} entregou {read} bytes e prometera {source.Length}.");
                }

                return produced;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
                ArrayPool<byte>.Shared.Return(encoded);
            }
        }
    }
}
