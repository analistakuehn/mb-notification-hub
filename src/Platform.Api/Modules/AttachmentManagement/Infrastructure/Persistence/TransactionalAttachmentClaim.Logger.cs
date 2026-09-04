namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// The one event of the claim. It names the notification the acceptance was
/// being taken for and how many references were asked for, and never a
/// reference: the caller already knows the set it sent, and the refusal that
/// reaches a producer deliberately names no member.
/// </summary>
internal static partial class TransactionalAttachmentClaimLogger
{
    [LoggerMessage(
        EventId = 2501,
        Level = LogLevel.Information,
        Message = "Claim de anexos recusado para a notificação {NotificationId} com "
            + "{ReferenceCount} referências: a capacidade de anexos não está habilitada nesta "
            + "implantação. Não é bloqueio de emergência, nada foi gravado e o que já estava "
            + "aceito segue processável.")]
    internal static partial void AttachmentClaimNotEnabled(
        this ILogger logger,
        Guid notificationId,
        int referenceCount);
}
