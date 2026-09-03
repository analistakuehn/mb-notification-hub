namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

internal static partial class AttachmentDisposalLogger
{
    [LoggerMessage(
        EventId = 2420,
        Level = LogLevel.Information,
        Message = "Descarte do anexo {Reference} recusado: {LiveDependencies} dependências ativas.")]
    internal static partial void AttachmentDisposalHeld(
        this ILogger logger,
        string reference,
        int liveDependencies);

    [LoggerMessage(
        EventId = 2421,
        Level = LogLevel.Information,
        Message = "Anexo {Reference} descartado: {DiscardedGenerations} gerações removidas.")]
    internal static partial void AttachmentDisposalCompleted(
        this ILogger logger,
        string reference,
        int discardedGenerations);

    [LoggerMessage(
        EventId = 2422,
        Level = LogLevel.Warning,
        Message = "Descarte do anexo {Reference} incompleto: {DiscardedGenerations} "
            + "gerações removidas e {UnconfirmedRemovals} sem confirmação do armazenamento.")]
    internal static partial void AttachmentDisposalUnconfirmed(
        this ILogger logger,
        string reference,
        int discardedGenerations,
        int unconfirmedRemovals);
}
