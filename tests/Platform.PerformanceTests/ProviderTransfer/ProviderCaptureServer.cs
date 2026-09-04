using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NotificationHub.PerformanceTests.ProviderTransfer;

/// <summary>One attachment as the double read it off the wire.</summary>
internal sealed record CapturedAttachment(
    int Order,
    string? FileName,
    string? Type,
    string? Disposition,
    string? ContentId,
    long Base64Bytes,
    long DecodedBytes,
    string DecodedSha256,
    bool DecodedSuccessfully);

/// <summary>One Mail Send call as the double read it off the wire.</summary>
internal sealed record CapturedMailSend(
    int Ordinal,
    string Method,
    string Path,
    string? Authorization,
    string? ContentType,
    long? DeclaredContentLength,
    bool Chunked,
    long BodyBytes,
    string BodySha256,
    bool? BodyIsWellFormedJson,
    string? Subject,
    IReadOnlyList<CapturedAttachment> Attachments);

/// <summary>
/// What the double answers, including the failures a provider is entitled to
/// produce. Dropping the connection is separate from every status because it
/// is the one answer that is not an answer.
/// </summary>
internal sealed record ProviderAnswer(
    int StatusCode,
    string? Body,
    IReadOnlyDictionary<string, string>? Headers,
    TimeSpan Delay = default,
    long DropAfterBodyBytes = -1)
{
    internal static ProviderAnswer Accept()
        => new(202, null, new Dictionary<string, string> { ["X-Message-Id"] = "probe-message" });

    internal static ProviderAnswer Reject()
        => new(
            400,
            """{"errors":[{"message":"the attachment content is not valid","field":"attachments.0.content"}]}""",
            null);

    internal static ProviderAnswer Throttle(TimeSpan retryAfter)
        => new(
            429,
            """{"errors":[{"message":"too many requests","field":null}]}""",
            new Dictionary<string, string>
            {
                ["Retry-After"] = ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            });

    internal static ProviderAnswer ServerFault() => new(500, null, null);

    /// <summary>Reads the whole body, then never answers within the client's patience.</summary>
    internal static ProviderAnswer Stall(TimeSpan delay) => new(202, null, null, delay);

    /// <summary>Resets the connection after that many bytes of the body arrived.</summary>
    internal static ProviderAnswer Drop(long afterBodyBytes)
        => new(0, null, null, default, Math.Max(afterBodyBytes, 0));
}

/// <summary>
/// In-process double of the provider's Mail Send endpoint, bound to a dynamic
/// loopback port. It follows the shape the module's contract tests already
/// use, and differs in the one thing this probe needs: it never materializes
/// the body it captures, because a body of twenty megabytes held by the double
/// would land in the same process counters the arms are measured with.
/// <para>
/// The request-size ceiling is the provider's documented one, so a message
/// over it is refused here for the same reason it would be refused there.
/// </para>
/// </summary>
internal sealed class ProviderCaptureServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<CapturedMailSend> _calls = new();
    private int _received;
    private int _inFlight;
    private int _peakConcurrency;

    private ProviderCaptureServer(WebApplication app) => _app = app;

    /// <summary>Programmable answer, keyed by the ordinal of the call.</summary>
    internal Func<int, ProviderAnswer> Answer { get; set; } = _ => ProviderAnswer.Accept();

    /// <summary>How much of the body the double reconstructs.</summary>
    internal CaptureDepth Depth { get; set; } = CaptureDepth.Decoded;

    /// <summary>Pause between reads of the body, which is backpressure on the sender.</summary>
    internal TimeSpan BodyReadDelay { get; set; }

    internal Uri BaseAddress { get; private set; } = null!;

    internal int CallCount => Volatile.Read(ref _received);

    internal int PeakConcurrency => Volatile.Read(ref _peakConcurrency);

    internal IReadOnlyList<CapturedMailSend> Calls => [.. _calls];

    internal static async Task<ProviderCaptureServer> StartAsync(CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = MailSendLimits.MaxMessageBytes;

            // The run injects stalls on purpose, and the default minimum data
            // rate would read one of them as an abusive client and kill the
            // connection before the injection proved anything.
            options.Limits.MinRequestBodyDataRate = null;
        });

        WebApplication app = builder.Build();
        var server = new ProviderCaptureServer(app);
        app.MapPost(MailSendComposer.Path, server.HandleAsync);
        app.Map("/{**path}", static context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        await app.StartAsync(cancellationToken);

        var bound = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        server.BaseAddress = new Uri(bound);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static async Task<MailSendBodyContent> ReadBodyAsync(
        PipeReader reader,
        MailSendBodyFilter filter,
        TimeSpan readDelay,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
            {
                filter.Append(segment.Span);
            }

            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                return filter.Complete();
            }

            if (readDelay > TimeSpan.Zero)
            {
                await Task.Delay(readDelay, cancellationToken);
            }
        }
    }

    private static async Task DrainAsync(PipeReader reader, long bytes, CancellationToken cancellationToken)
    {
        long seen = 0;
        while (seen < bytes)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            seen += result.Buffer.Length;
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                return;
            }
        }
    }

    private static CapturedMailSend Describe(
        HttpContext context,
        int ordinal,
        MailSendBodyContent body,
        CaptureDepth depth)
    {
        bool? wellFormed = null;
        string? subject = null;
        IReadOnlyList<CapturedAttachment> attachments = [];
        if (depth is CaptureDepth.Decoded)
        {
            try
            {
                using var document = JsonDocument.Parse(body.ShrunkJson);
                wellFormed = true;
                subject = document.RootElement.TryGetProperty("subject", out JsonElement value)
                    ? value.GetString()
                    : null;
                attachments = ReadAttachments(document.RootElement, body.LargeValues);
            }
            catch (JsonException)
            {
                wellFormed = false;
            }
        }

        return new CapturedMailSend(
            ordinal,
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            context.Request.Headers.Authorization.ToString(),
            context.Request.ContentType,
            context.Request.ContentLength,
            context.Request.ContentLength is null,
            body.BodyBytes,
            body.BodySha256,
            wellFormed,
            subject,
            attachments);
    }

    private static List<CapturedAttachment> ReadAttachments(
        JsonElement root,
        IReadOnlyList<CapturedLargeValue> large)
    {
        if (!root.TryGetProperty("attachments", out JsonElement array)
            || array.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var captured = new List<CapturedAttachment>(array.GetArrayLength());
        var order = 0;
        foreach (JsonElement attachment in array.EnumerateArray())
        {
            var content = Text(attachment, "content");
            CapturedLargeValue? streamed = content is null
                ? null
                : large.FirstOrDefault(value => string.Equals(value.Marker, content, StringComparison.Ordinal));

            // An attachment small enough to have stayed inline never reached the
            // streaming decoder, so it is decoded here. The two paths have to
            // agree on every field, and a run whose attachment crosses the
            // inline limit is what exercises both against the same oracle.
            CapturedLargeValue decoded = streamed ?? DecodeInline(content);
            captured.Add(new CapturedAttachment(
                order,
                Text(attachment, "filename"),
                Text(attachment, "type"),
                Text(attachment, "disposition"),
                Text(attachment, "content_id"),
                decoded.Base64Bytes,
                decoded.DecodedBytes,
                decoded.DecodedSha256,
                decoded.DecodedSuccessfully));
            order++;
        }

        return captured;
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static CapturedLargeValue DecodeInline(string? content)
    {
        if (content is null)
        {
            return new CapturedLargeValue(string.Empty, 0, 0, string.Empty, DecodedSuccessfully: false);
        }

        try
        {
            var bytes = Convert.FromBase64String(content);
            return new CapturedLargeValue(
                string.Empty,
                content.Length,
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)),
                DecodedSuccessfully: true);
        }
        catch (FormatException)
        {
            return new CapturedLargeValue(string.Empty, content.Length, 0, string.Empty, DecodedSuccessfully: false);
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var ordinal = Interlocked.Increment(ref _received);
        var current = Interlocked.Increment(ref _inFlight);
        RecordPeak(current);
        try
        {
            ProviderAnswer answer = Answer(ordinal);
            if (answer.DropAfterBodyBytes >= 0)
            {
                await DrainAsync(context.Request.BodyReader, answer.DropAfterBodyBytes, context.RequestAborted);
                context.Abort();
                return;
            }

            CaptureDepth depth = Depth;
            using var filter = new MailSendBodyFilter(
                $"captured-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}",
                depth);
            MailSendBodyContent body = await ReadBodyAsync(
                context.Request.BodyReader, filter, BodyReadDelay, context.RequestAborted);
            _calls.Enqueue(Describe(context, ordinal, body, depth));

            if (answer.Delay > TimeSpan.Zero)
            {
                await Task.Delay(answer.Delay, context.RequestAborted);
            }

            context.Response.StatusCode = answer.StatusCode;
            if (answer.Headers is not null)
            {
                foreach ((var name, var value) in answer.Headers)
                {
                    context.Response.Headers[name] = value;
                }
            }

            if (answer.Body is not null)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(answer.Body, context.RequestAborted);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void RecordPeak(int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _peakConcurrency);
        }
        while (candidate > observed
            && Interlocked.CompareExchange(ref _peakConcurrency, candidate, observed) != observed);
    }
}
