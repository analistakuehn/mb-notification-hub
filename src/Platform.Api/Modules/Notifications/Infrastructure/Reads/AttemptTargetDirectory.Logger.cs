namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;

internal static partial class AttemptTargetDirectoryLogger
{
    [LoggerMessage(
        EventId = 7151,
        Level = LogLevel.Warning,
        Message = "Alvos de contato do destinatário {RecipientId} indisponíveis para a consulta: {Reason}. A resposta segue sem a forma mascarada.")]
    internal static partial void ContactTargetsUnavailable(
        this ILogger logger,
        string recipientId,
        string reason);

    [LoggerMessage(
        EventId = 7152,
        Level = LogLevel.Warning,
        Message = "Falha ao mascarar os alvos de contato do destinatário {RecipientId}; a resposta segue sem a forma mascarada.")]
    internal static partial void ContactTargetsFailed(
        this ILogger logger,
        string recipientId,
        Exception exception);

    [LoggerMessage(
        EventId = 7153,
        Level = LogLevel.Warning,
        Message = "Registros de dispositivo do destinatário {RecipientId} indisponíveis para a consulta: {Reason}. A resposta segue sem a plataforma.")]
    internal static partial void DeviceTargetsUnavailable(
        this ILogger logger,
        string recipientId,
        string reason);

    [LoggerMessage(
        EventId = 7154,
        Level = LogLevel.Warning,
        Message = "Falha ao resolver os registros de dispositivo do destinatário {RecipientId}; a resposta segue sem a plataforma.")]
    internal static partial void DeviceTargetsFailed(
        this ILogger logger,
        string recipientId,
        Exception exception);
}
