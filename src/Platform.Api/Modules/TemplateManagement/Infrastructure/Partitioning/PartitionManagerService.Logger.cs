namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Partitioning;

internal static partial class PartitionManagerServiceLogger
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "Gerenciador de partições iniciado; próximas rodadas a cada {Interval}.")]
    internal static partial void PartitionManagerStarted(this ILogger logger, TimeSpan interval);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Gerenciador de partições desabilitado pela configuração; nenhuma partição será criada por este host.")]
    internal static partial void PartitionManagerDisabled(this ILogger logger);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Error, Message = "Falha na rodada de manutenção de partições; nova tentativa na próxima rodada agendada.")]
    internal static partial void PartitionMaintenanceRoundFailed(this ILogger logger, Exception exception);
}
