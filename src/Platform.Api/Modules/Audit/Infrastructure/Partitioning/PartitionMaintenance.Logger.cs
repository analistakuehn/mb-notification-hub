namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

internal static partial class PartitionMaintenanceLogger
{
    [LoggerMessage(EventId = 5013, Level = LogLevel.Information, Message = "Etapa de REVOKE em partições fechadas inativa: o gate está desligado e a etapa depende das roles de banco de uma fase posterior.")]
    internal static partial void RevokeStepInactive(this ILogger logger);

    [LoggerMessage(EventId = 5014, Level = LogLevel.Warning, Message = "Etapa de REVOKE em partições fechadas habilitada na configuração, porém ainda não implementada; nenhuma permissão foi alterada.")]
    internal static partial void RevokeStepEnabledButUnavailable(this ILogger logger);

    [LoggerMessage(EventId = 5015, Level = LogLevel.Information, Message = "Ciclo de retenção (DETACH, export WORM e drop) inativo: o gate está desligado e o ciclo depende do bucket WORM de uma fase posterior.")]
    internal static partial void RetentionCycleInactive(this ILogger logger);

    [LoggerMessage(EventId = 5016, Level = LogLevel.Warning, Message = "Ciclo de retenção (DETACH, export WORM e drop) habilitado na configuração, porém ainda não implementado; nenhuma partição foi destacada, exportada ou removida.")]
    internal static partial void RetentionCycleEnabledButUnavailable(this ILogger logger);
}
