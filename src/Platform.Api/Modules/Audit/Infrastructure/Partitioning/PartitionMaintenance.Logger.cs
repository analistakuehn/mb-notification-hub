namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

internal static partial class PartitionMaintenanceLogger
{
    [LoggerMessage(EventId = 5013, Level = LogLevel.Information, Message = "Etapa de REVOKE em partições fechadas inativa: o gate está desligado e o ciclo de fechamento não avança sem ela.")]
    internal static partial void RevokeStepInactive(this ILogger logger);

    [LoggerMessage(EventId = 5015, Level = LogLevel.Information, Message = "Ciclo de retenção inativo: o gate está desligado e nenhuma partição será fechada nesta rodada.")]
    internal static partial void RetentionCycleInactive(this ILogger logger);

    [LoggerMessage(EventId = 5017, Level = LogLevel.Information, Message = "Escrita revogada na partição fechada {Partition}; o gatilho de partição fechada passa a recusar inserções.")]
    internal static partial void PartitionWritesRevoked(this ILogger logger, string partition);

    [LoggerMessage(EventId = 5018, Level = LogLevel.Information, Message = "Partição {Partition} destacada da trilha após a verificação da cópia WORM.")]
    internal static partial void PartitionDetached(this ILogger logger, string partition);

    [LoggerMessage(EventId = 5019, Level = LogLevel.Warning, Message = "Partição destacada {Partition} removida do banco; a evidência permanece apenas no armazenamento imutável.")]
    internal static partial void PartitionDropped(this ILogger logger, string partition);

    [LoggerMessage(EventId = 5024, Level = LogLevel.Warning, Message = "Ciclo de fechamento da partição {Partition} interrompido na etapa {Stage}: {Failure}. Nada foi destacado nem removido.")]
    internal static partial void ClosingAborted(
        this ILogger logger,
        string partition,
        string stage,
        string failure);

    [LoggerMessage(EventId = 5025, Level = LogLevel.Information, Message = "Remoção da partição destacada {Partition} inativa: o gate próprio de descarte está desligado.")]
    internal static partial void DropStepInactive(this ILogger logger, string partition);
}
