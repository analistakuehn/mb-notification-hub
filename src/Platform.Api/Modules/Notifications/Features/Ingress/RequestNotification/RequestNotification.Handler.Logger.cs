using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

namespace NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

internal static partial class RequestNotificationLogger
{
    [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "Notificação {NotificationId} aceita para a aplicação {Application} na classe {Class} com o template {TemplateKey} versão {TemplateVersion}.")]
    internal static partial void NotificationAccepted(this ILogger logger, Guid notificationId, string application, string @class, string templateKey, int templateVersion);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Replay idempotente respondido com a notificação {NotificationId} da aplicação {Application} para o template {TemplateKey}.")]
    internal static partial void NotificationReplayed(this ILogger logger, Guid notificationId, string application, string templateKey);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Solicitação rejeitada na ingestão para a aplicação {Application} e template {TemplateKey} na classe {Class}: {Reason}.")]
    internal static partial void IngressRejected(this ILogger logger, string application, string templateKey, string @class, string reason);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "Conflito de idempotência na aplicação {Application} para o template {TemplateKey}: mesma chave com corpo diferente.")]
    internal static partial void IdempotencyConflictDetected(this ILogger logger, string application, string templateKey);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Warning, Message = "Solicitação limitada na ingestão para a aplicação {Application} na classe {Class} pela dimensão {Dimension}; retry em {RetryAfterSeconds} s.")]
    internal static partial void RateLimitedAtIngress(this ILogger logger, string application, string @class, RateLimitedDimension dimension, int retryAfterSeconds);
}
