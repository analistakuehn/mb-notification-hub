namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;

internal static partial class AttachmentProducerRegistryLogger
{
    [LoggerMessage(
        EventId = 2420,
        Level = LogLevel.Error,
        Message = "Registro de autorização de anexos indisponível.")]
    internal static partial void RegistryUnavailable(
        this ILogger<AttachmentProducerRegistry> logger,
        Exception exception);
}
