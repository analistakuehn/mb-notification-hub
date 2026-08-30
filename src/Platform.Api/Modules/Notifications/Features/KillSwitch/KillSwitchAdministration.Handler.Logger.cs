namespace NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

/// <summary>
/// Log of the manual kill switch administration. The threat model names this
/// switch as the control that is left standing when the rate limit fails
/// open, and the automatic sibling ends its own alarm by sending the operator
/// here, so every command that reaches this handler leaves a line naming the
/// actor, the scope and the key.
/// <para>
/// Only the address of the switch and the directory id of the operator become
/// placeholders. That id is already the actor of the <c>kill_switch.changed</c>
/// trail entry, and nothing derived from a notification, a recipient or a
/// rendered body passes through this slice at all.
/// </para>
/// </summary>
internal static partial class KillSwitchAdministrationLogger
{
    [LoggerMessage(
        EventId = 7223,
        Level = LogLevel.Warning,
        Message = "Kill switch do escopo {Scope} ligado para a chave '{Key}' pelo ator {Actor}: "
            + "o tráfego protegido por esse escopo deixa de sair até alguém desligar.")]
    internal static partial void KillSwitchActivated(
        this ILogger logger, string scope, string key, string actor);

    [LoggerMessage(
        EventId = 7224,
        Level = LogLevel.Warning,
        Message = "Kill switch do escopo {Scope} desligado para a chave '{Key}' pelo ator {Actor}: "
            + "o tráfego desse escopo volta a sair, inclusive o que ficou retido enquanto ele esteve ligado.")]
    internal static partial void KillSwitchDeactivated(
        this ILogger logger, string scope, string key, string actor);

    [LoggerMessage(
        EventId = 7225,
        Level = LogLevel.Information,
        Message = "Kill switch do escopo {Scope} para a chave '{Key}' já estava em '{State}': "
            + "o pedido do ator {Actor} não mudou nada e, por não haver transição, não deixou trilha.")]
    internal static partial void KillSwitchUnchanged(
        this ILogger logger, string scope, string key, string state, string actor);

    [LoggerMessage(
        EventId = 7226,
        Level = LogLevel.Error,
        Message = "Alarme: conflito de concorrência ao mudar o kill switch do escopo {Scope} para a "
            + "chave '{Key}'; o pedido do ator {Actor} foi desfeito e o estado continua o que era. "
            + "Quem pediu precisa reler o estado e tentar de novo.")]
    internal static partial void KillSwitchConcurrencyConflict(
        this ILogger logger, string scope, string key, string actor);

    [LoggerMessage(
        EventId = 7227,
        Level = LogLevel.Error,
        Message = "Alarme: colisão de unicidade ao criar o kill switch do escopo {Scope} para a "
            + "chave '{Key}'; outro pedido para a mesma chave chegou primeiro, o do ator {Actor} foi "
            + "desfeito e o estado continua o que era. Quem pediu precisa reler o estado e tentar de novo.")]
    internal static partial void KillSwitchUniqueViolationConflict(
        this ILogger logger, string scope, string key, string actor);
}
