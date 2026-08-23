namespace NotificationHub.Api.Infrastructure.Partitioning;

internal static partial class MonthlyPartitionProvisionerLogger
{
    [LoggerMessage(EventId = 5010, Level = LogLevel.Information, Message = "Partição {Partition} criada para a tabela {Table}.")]
    internal static partial void PartitionCreated(this ILogger logger, string partition, string table);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Debug, Message = "Partição {Partition} da tabela {Table} já existe; nada a criar.")]
    internal static partial void PartitionAlreadyExists(this ILogger logger, string partition, string table);

    [LoggerMessage(EventId = 5012, Level = LogLevel.Warning, Message = "Tabela {Table} não existe ou não é particionada; criação de partições ignorada nesta rodada.")]
    internal static partial void TableIsNotAPartitionedParent(this ILogger logger, string table);
}
