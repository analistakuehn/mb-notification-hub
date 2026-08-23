namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class RegisterDeviceLogger
{
    [LoggerMessage(EventId = 8020, Level = LogLevel.Information, Message = "Device token {DeviceTokenId} registrado para o destinatário {RecipientId} na plataforma {Platform} (re-registro: {ReRegistration}).")]
    internal static partial void DeviceRegistered(this ILogger logger, string recipientId, Guid deviceTokenId, string platform, bool reRegistration);

    [LoggerMessage(EventId = 8021, Level = LogLevel.Warning, Message = "Conflito de escrita concorrente ao registrar device token do destinatário {RecipientId}.")]
    internal static partial void DeviceWriteConflict(this ILogger logger, string recipientId);
}
