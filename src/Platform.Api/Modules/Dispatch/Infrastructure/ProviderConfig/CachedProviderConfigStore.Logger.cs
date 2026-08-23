namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;

internal static partial class CachedProviderConfigStoreLogger
{
    [LoggerMessage(EventId = 6000, Level = LogLevel.Debug, Message = "Configuração de provedores recarregada; {ChannelCount} canais mapeados.")]
    internal static partial void ProviderConfigRefreshed(this ILogger logger, int channelCount);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "Falha ao recarregar a configuração de provedores; o snapshot anterior continua em uso.")]
    internal static partial void ProviderConfigRefreshFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Error, Message = "Configuração de provedores indisponível e sem snapshot anterior; resoluções de canal falharão até a próxima leitura bem-sucedida.")]
    internal static partial void ProviderConfigUnavailable(this ILogger logger, Exception exception);
}
