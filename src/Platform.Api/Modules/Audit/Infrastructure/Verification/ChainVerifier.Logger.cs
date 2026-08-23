namespace NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

internal static partial class ChainVerifierLogger
{
    [LoggerMessage(EventId = 5030, Level = LogLevel.Information, Message = "Cadeia da partição {Partition} verificada: {ChainedCount} eventos entre os seq {FromSeq} e {ThroughSeq} (verificação integral: {FullReplay}).")]
    internal static partial void ChainVerified(
        this ILogger logger,
        string partition,
        int chainedCount,
        long fromSeq,
        long throughSeq,
        bool fullReplay);

    [LoggerMessage(EventId = 5031, Level = LogLevel.Error, Message = "Cadeia da partição {Partition} quebrada no seq {BrokenSeq}: {Failure}.")]
    internal static partial void ChainVerificationFailed(
        this ILogger logger,
        string partition,
        long brokenSeq,
        string failure);

    [LoggerMessage(EventId = 5032, Level = LogLevel.Information, Message = "Verificação de cadeia desabilitada por configuração; nenhuma rodada será executada.")]
    internal static partial void ChainVerificationDisabled(this ILogger logger);

    [LoggerMessage(EventId = 5033, Level = LogLevel.Information, Message = "Verificação de cadeia iniciada com cadência de {Interval}.")]
    internal static partial void ChainVerificationStarted(this ILogger logger, TimeSpan interval);

    [LoggerMessage(EventId = 5034, Level = LogLevel.Warning, Message = "Rodada de verificação de cadeia falhou; a próxima cadência tenta de novo.")]
    internal static partial void ChainVerificationRoundFailed(this ILogger logger, Exception exception);
}
