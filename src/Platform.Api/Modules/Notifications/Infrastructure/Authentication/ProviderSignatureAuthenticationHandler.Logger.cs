namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authentication;

/// <summary>
/// Security log of the provider-signature scheme. Nothing derived from the
/// callback itself is ever a placeholder here: the body, the destination and
/// the presented signature are either personal data or attacker supplied, and
/// the provider key is the only value that came from the route this hub
/// published.
/// </summary>
internal static partial class ProviderSignatureAuthenticationHandlerLogger
{
    [LoggerMessage(
        EventId = 7180,
        Level = LogLevel.Warning,
        Message = "Callback de provedor recusado por origem fora da allowlist em '{ProviderKey}'; "
            + "trate como tentativa de forjação.")]
    internal static partial void ProviderWebhookOriginRejected(this ILogger logger, string providerKey);

    [LoggerMessage(
        EventId = 7181,
        Level = LogLevel.Warning,
        Message = "Callback de provedor recusado em '{ProviderKey}' com o motivo '{Refusal}'.")]
    internal static partial void ProviderWebhookRefused(
        this ILogger logger, string providerKey, string refusal);

    [LoggerMessage(
        EventId = 7182,
        Level = LogLevel.Warning,
        Message = "Callback endereçado ao provedor desconhecido '{ProviderKey}'.")]
    internal static partial void ProviderWebhookProviderUnknown(this ILogger logger, string providerKey);

    [LoggerMessage(
        EventId = 7183,
        Level = LogLevel.Warning,
        Message = "Callback de '{ProviderKey}' recusado por exceder o limite de {MaxBodyBytes} bytes.")]
    internal static partial void ProviderWebhookBodyTooLarge(
        this ILogger logger, string providerKey, int maxBodyBytes);

    [LoggerMessage(
        EventId = 7184,
        Level = LogLevel.Debug,
        Message = "Callback de '{ProviderKey}' autenticado pela assinatura do provedor.")]
    internal static partial void ProviderWebhookVerified(this ILogger logger, string providerKey);
}
