namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Devices;

internal static partial class DeviceTokenInvalidationLogger
{
    [LoggerMessage(EventId = 8030, Level = LogLevel.Information, Message = "Registro de dispositivo {DeviceTokenId} do destinatário {RecipientId} invalidado por feedback do provedor ({Reason}).")]
    internal static partial void DeviceInvalidated(this ILogger logger, string recipientId, Guid deviceTokenId, string reason);

    [LoggerMessage(EventId = 8031, Level = LogLevel.Information, Message = "Invalidação repetida do registro de dispositivo {DeviceTokenId} do destinatário {RecipientId} ({Reason}); nenhum efeito novo.")]
    internal static partial void DeviceInvalidationRepeated(this ILogger logger, string recipientId, Guid deviceTokenId, string reason);
}
