using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NotificationHub.IntegrationTests.Dispatch;

/// <summary>
/// In-process Kestrel double for provider APIs, bound to a dynamic loopback
/// port. Chosen over an HTTP-mocking package on purpose: the repository
/// already ships the whole ASP.NET stack, so the fake costs zero packages
/// while still exercising real sockets, headers and timeouts. FCM has no
/// sandbox, so a fake like this is the only honest test target for it.
/// </summary>
public sealed class FakeProviderServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _inFlight;
    private int _maxObservedConcurrency;

    private FakeProviderServer(WebApplication app) => _app = app;

    /// <summary>Programmable answer; tests replace it per scenario.</summary>
    public Func<FakeProviderRequest, Task<FakeProviderResponse>> Handler { get; set; } =
        _ => Task.FromResult(new FakeProviderResponse(200, null, null));

    public ConcurrentQueue<FakeProviderRequest> Requests { get; } = new();

    public int RequestCount => Requests.Count;

    public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    public Uri BaseAddress { get; private set; } = null!;

    public static async Task<FakeProviderServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        WebApplication app = builder.Build();
        var server = new FakeProviderServer(app);
        app.Map("/{**path}", server.HandleAsync);
        await app.StartAsync();

        var boundAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        server.BaseAddress = new Uri(boundAddress);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var current = Interlocked.Increment(ref _inFlight);
        RecordMax(current);
        try
        {
            // Read as bytes and decoded afterwards: a body is measured in
            // bytes, and a caller asking how large a message was cannot be
            // answered with a count of characters.
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            var body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var request = new FakeProviderRequest(
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                context.Request.Headers.Authorization.ToString(),
                context.Request.ContentType,
                body,
                context.Request.QueryString.Value ?? string.Empty,
                buffer.Length,
                context.Request.ContentLength);
            Requests.Enqueue(request);

            FakeProviderResponse response = await Handler(request);
            context.Response.StatusCode = response.StatusCode;
            if (response.Headers is not null)
            {
                foreach ((var name, var value) in response.Headers)
                {
                    context.Response.Headers[name] = value;
                }
            }

            if (response.Body is not null)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(response.Body, context.RequestAborted);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void RecordMax(int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxObservedConcurrency);
        }
        while (candidate > observed
            && Interlocked.CompareExchange(ref _maxObservedConcurrency, candidate, observed) != observed);
    }
}

/// <summary>
/// One call the double received. The query string is separate from the path
/// because a provider that is asked a question rather than told to send one
/// carries the whole question there, and a test about what this hub asked has
/// to be able to read it.
/// </summary>
public sealed record FakeProviderRequest(
    string Method,
    string Path,
    string Authorization,
    string? ContentType,
    string Body,
    string Query = "",
    long BodyBytes = 0,
    long? DeclaredContentLength = null);

public sealed record FakeProviderResponse(
    int StatusCode,
    string? Body,
    IReadOnlyDictionary<string, string>? Headers);
