using System.Diagnostics;

namespace NotificationHub.Api.Infrastructure.EndpointFilters;

/// <summary>
/// Endpoint filter that emits start/complete/failed structured logs around
/// the inner handler call. Logging methods live in
/// <c>RequestLoggingFilter.Logger.cs</c> as source-generated partials.
/// </summary>
public sealed partial class RequestLoggingFilter(ILogger<RequestLoggingFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        string endpoint = context.HttpContext.GetEndpoint()?.DisplayName ?? "(unknown)";
        Stopwatch stopwatch = Stopwatch.StartNew();

        EndpointInvocationStarted(endpoint);

        try
        {
            object? result = await next(context);
            EndpointInvocationCompleted(endpoint, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception exception)
        {
            EndpointInvocationFailed(endpoint, stopwatch.ElapsedMilliseconds, exception);
            throw;
        }
    }
}
