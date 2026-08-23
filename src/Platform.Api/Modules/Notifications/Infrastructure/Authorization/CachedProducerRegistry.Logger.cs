namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;

internal static partial class CachedProducerRegistryLogger
{
    [LoggerMessage(EventId = 7040, Level = LogLevel.Information, Message = "Registro de produtores recarregado com {GrantCount} concessões.")]
    internal static partial void ProducerRegistryRefreshed(this ILogger logger, int grantCount);

    [LoggerMessage(EventId = 7041, Level = LogLevel.Error, Message = "Alarme: o registro de produtores nunca foi lido com sucesso; a entrada pelo barramento não autoriza ninguém enquanto isso.")]
    internal static partial void ProducerRegistryUnavailable(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7042, Level = LogLevel.Warning, Message = "A recarga do registro de produtores falhou; o snapshot anterior continua servindo.")]
    internal static partial void ProducerRegistryRefreshFailed(this ILogger logger, Exception exception);
}
