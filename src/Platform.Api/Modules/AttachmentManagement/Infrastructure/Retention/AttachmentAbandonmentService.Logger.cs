namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;

/// <summary>Events of the scheduler that drives the sweep of abandoned attachments.</summary>
internal static partial class AttachmentAbandonmentServiceLogger
{
    [LoggerMessage(
        EventId = 2490,
        Level = LogLevel.Warning,
        Message = "O descarte de anexos abandonados está desligado nesta implantação, "
            + "portanto o conteúdo abandonado permanece armazenado sem que ninguém o remova.")]
    internal static partial void AttachmentAbandonmentDisabled(this ILogger logger);

    [LoggerMessage(
        EventId = 2491,
        Level = LogLevel.Information,
        Message = "Descarte de anexos abandonados iniciado com intervalo de {Interval} e lote "
            + "de {BatchSize} candidatos por rodada.")]
    internal static partial void AttachmentAbandonmentStarted(
        this ILogger logger,
        TimeSpan interval,
        int batchSize);

    [LoggerMessage(
        EventId = 2492,
        Level = LogLevel.Error,
        Message = "A rodada de descarte de anexos abandonados falhou; nada foi marcado como "
            + "descartado além do que já havia sido confirmado, e a próxima rodada encontra "
            + "os candidatos restantes.")]
    internal static partial void AttachmentAbandonmentRoundFailed(
        this ILogger logger,
        Exception exception);
}
