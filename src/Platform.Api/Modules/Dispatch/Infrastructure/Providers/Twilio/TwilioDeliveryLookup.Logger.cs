namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

/// <summary>
/// Log of the SMS delivery lookup. Counts, status words, HTTP codes and
/// section names only: this adapter is the one place in the pull path that
/// holds a destination, and no placeholder here may ever carry it.
/// </summary>
internal static partial class TwilioDeliveryLookupLogger
{
    [LoggerMessage(
        EventId = 6320,
        Level = LogLevel.Debug,
        Message = "Consulta ao Twilio não encontrou mensagem correspondente; o attempt segue sem desfecho.")]
    internal static partial void TwilioLookupFoundNothing(this ILogger logger);

    [LoggerMessage(
        EventId = 6321,
        Level = LogLevel.Warning,
        Message = "Consulta ao Twilio recusada (HTTP {StatusCode}); nada foi concluído sobre o attempt.")]
    internal static partial void TwilioLookupRefused(this ILogger logger, int statusCode);

    [LoggerMessage(
        EventId = 6322,
        Level = LogLevel.Warning,
        Message = "Consulta ao Twilio excedeu o timeout de {TimeoutSeconds} segundos.")]
    internal static partial void TwilioLookupTimedOut(this ILogger logger, int timeoutSeconds);

    [LoggerMessage(
        EventId = 6323,
        Level = LogLevel.Warning,
        Message = "Falha de rede na consulta de entrega ao Twilio.")]
    internal static partial void TwilioLookupNetworkFault(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 6324,
        Level = LogLevel.Warning,
        Message = "Resposta da consulta ao Twilio ilegível como recurso de mensagem.")]
    internal static partial void TwilioLookupPayloadUnreadable(this ILogger logger);

    [LoggerMessage(
        EventId = 6325,
        Level = LogLevel.Warning,
        Message = "Status {Status} do Twilio está fora do vocabulário mapeado; a consulta não conclui "
            + "nada sobre o attempt em vez de adivinhar o estado.")]
    internal static partial void TwilioLookupStatusUnmapped(this ILogger logger, string status);

    [LoggerMessage(
        EventId = 6326,
        Level = LogLevel.Warning,
        Message = "A correlação por destino e janela casou {MessageCount} mensagens; nada é concluído, "
            + "porque escolher uma delas atribuiria ao attempt o desfecho de outra mensagem.")]
    internal static partial void TwilioLookupAmbiguous(this ILogger logger, int messageCount);

    [LoggerMessage(
        EventId = 6327,
        Level = LogLevel.Error,
        Message = "Credenciais do Twilio ausentes em {SectionName}; nenhuma consulta de entrega será feita.")]
    internal static partial void TwilioLookupNotConfigured(this ILogger logger, string sectionName);

    [LoggerMessage(
        EventId = 6328,
        Level = LogLevel.Warning,
        Message = "A consulta ao Twilio não trouxe identidade de mensagem nem destino; este provedor "
            + "não busca por outro critério.")]
    internal static partial void TwilioLookupWithoutRoute(this ILogger logger);
}
