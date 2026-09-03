using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// The content source of a send that carries no attachment. It is never asked,
/// and it raises rather than answers if it ever is.
/// <para>
/// A stand-in that answered would turn a composition defect into a message
/// with a field nobody could fill, and the transport would report it as a
/// broken connection. Raising keeps the defect where it happened.
/// </para>
/// </summary>
internal sealed class UnaskedAttachmentContent : IAcceptedAttachmentContent
{
    internal static UnaskedAttachmentContent Instance { get; } = new();

    public Task<AcceptedAttachmentContent> OpenAsync(
        string contentIdentity,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException(
            "Um envio sem conjunto de anexos não abre conteúdo algum; a composição pediu "
            + "bytes que a mensagem não carrega.");
}
