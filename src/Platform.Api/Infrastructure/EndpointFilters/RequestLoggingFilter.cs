using System.Diagnostics;

namespace NotificationHub.Api.Infrastructure.EndpointFilters;

/// <summary>
/// Endpoint filter that emits start/complete/failed structured logs around
/// the inner handler call. Logging methods live in
/// <c>RequestLoggingFilter.Logger.cs</c> as source-generated extension methods.
/// </summary>
public sealed class RequestLoggingFilter(ILogger<RequestLoggingFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        string endpoint = context.HttpContext.GetEndpoint()?.DisplayName ?? "(unknown)";
        var stopwatch = Stopwatch.StartNew();

        logger.EndpointInvocationStarted(endpoint);

        try
        {
            object? result = await next(context);
            logger.EndpointInvocationCompleted(endpoint, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception exception)
        {
            logger.EndpointInvocationFailed(exception, endpoint, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
