namespace NotificationHub.Api.Infrastructure.EndpointFilters;

internal static partial class RequestLoggingFilterLogger
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Invocação do endpoint {Endpoint} iniciada.")]
    internal static partial void EndpointInvocationStarted(this ILogger logger, string endpoint);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Invocação do endpoint {Endpoint} concluída em {ElapsedMs}ms.")]
    internal static partial void EndpointInvocationCompleted(this ILogger logger, string endpoint, long elapsedMs);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Invocação do endpoint {Endpoint} falhou após {ElapsedMs}ms.")]
    internal static partial void EndpointInvocationFailed(this ILogger logger, Exception exception, string endpoint, long elapsedMs);
}
