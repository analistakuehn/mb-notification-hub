namespace NotificationHub.Api.Infrastructure.EndpointFilters;

/// <summary>
/// Convenience builders for attaching the standard endpoint filter set
/// (validation, request logging) to a Minimal API endpoint.
/// </summary>
public static class EndpointFilterExtensions
{
    /// <summary>
    /// Attaches <see cref="ValidationFilter{TRequest}"/> to the endpoint so a
    /// registered <c>IValidator&lt;TRequest&gt;</c> short-circuits the request
    /// with <c>ValidationProblem</c> when validation fails.
    /// </summary>
    public static RouteHandlerBuilder WithValidation<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class
        => builder.AddEndpointFilter<ValidationFilter<TRequest>>();

    /// <summary>
    /// Attaches <see cref="RequestLoggingFilter"/> to the endpoint.
    /// </summary>
    public static RouteHandlerBuilder WithRequestLogging(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<RequestLoggingFilter>();
}
