namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// Log of the e-mail delivery lookup. Counts, status words, HTTP codes and
/// section names only: this path never holds a destination, and no placeholder
/// here may ever introduce one.
/// </summary>
internal static partial class SendGridDeliveryLookupLogger
{
    [LoggerMessage(
        EventId = 6110,
        Level = LogLevel.Debug,
        Message = "Consulta de atividade ao SendGrid não encontrou mensagem correspondente; "
            + "o attempt segue sem desfecho.")]
    internal static partial void SendGridLookupFoundNothing(this ILogger logger);

    [LoggerMessage(
        EventId = 6111,
        Level = LogLevel.Warning,
        Message = "Consulta de atividade ao SendGrid recusada (HTTP {StatusCode}); nada foi concluído "
            + "sobre o attempt.")]
    internal static partial void SendGridLookupRefused(this ILogger logger, int statusCode);

    [LoggerMessage(
        EventId = 6112,
        Level = LogLevel.Warning,
        Message = "Consulta de atividade ao SendGrid excedeu o timeout de {TimeoutSeconds} segundos.")]
    internal static partial void SendGridLookupTimedOut(this ILogger logger, int timeoutSeconds);

    [LoggerMessage(
        EventId = 6113,
        Level = LogLevel.Warning,
        Message = "Falha de rede na consulta de atividade ao SendGrid.")]
    internal static partial void SendGridLookupNetworkFault(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 6114,
        Level = LogLevel.Warning,
        Message = "Resposta da consulta de atividade ao SendGrid ilegível como página de mensagens.")]
    internal static partial void SendGridLookupPayloadUnreadable(this ILogger logger);

    [LoggerMessage(
        EventId = 6115,
        Level = LogLevel.Warning,
        Message = "Status {Status} da atividade do SendGrid está fora do vocabulário mapeado; "
            + "a consulta não conclui nada sobre o attempt.")]
    internal static partial void SendGridLookupStatusUnmapped(this ILogger logger, string status);

    [LoggerMessage(
        EventId = 6116,
        Level = LogLevel.Information,
        Message = "Mensagem mais antiga que o alcance de {LookbackDays} dia(s) da atividade contratada; "
            + "nenhuma consulta foi feita e o attempt permanece sem desfecho.")]
    internal static partial void SendGridLookupOutOfReach(this ILogger logger, int lookbackDays);

    [LoggerMessage(
        EventId = 6117,
        Level = LogLevel.Error,
        Message = "Alarme: a consulta de atividade do SendGrid respondeu HTTP {StatusCode}; o recurso é "
            + "capacidade contratada e, enquanto responder assim, nenhuma reconciliação de e-mail "
            + "conclui coisa alguma.")]
    internal static partial void SendGridLookupUnavailable(this ILogger logger, int statusCode);

    [LoggerMessage(
        EventId = 6118,
        Level = LogLevel.Error,
        Message = "Chave de API do SendGrid ausente em {SectionName}; nenhuma consulta de entrega será feita.")]
    internal static partial void SendGridLookupNotConfigured(this ILogger logger, string sectionName);
}
