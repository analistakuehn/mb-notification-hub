using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

/// <summary>One fixed counting window of the ingestion rate limit.</summary>
public sealed record RateWindow
{
    [Range(1, int.MaxValue)]
    public required int PermitLimit { get; init; }

    [Range(1, 604_800)]
    public required int WindowSeconds { get; init; }
}

/// <summary>
/// Ingestion rate limits in two dimensions, both keyed by canonical class:
/// per producer principal (protects the platform from a compromised or
/// runaway producer) and per recipient (protects one person from message
/// bombing). A class without a configured entry has no limit in that
/// dimension. The values live in configuration because tuning them is an
/// operational decision, not a deploy.
/// </summary>
public sealed class IngestionRateLimitOptions : IValidatableObject
{
    public const string SectionName = "Modules:Notifications:RateLimits";

    /// <summary>One window per class, keyed by canonical class value.</summary>
    public Dictionary<string, RateWindow> PerPrincipal { get; init; } = [];

    /// <summary>
    /// Cumulative windows per class, keyed by canonical class value; every
    /// window must hold for the request to pass.
    /// </summary>
    public Dictionary<string, List<RateWindow>> PerRecipient { get; init; } = [];

    /// <summary>
    /// Validates every window both maps hold, which the registration of this
    /// type does not reach: a range on a dictionary value or on a list item is
    /// never evaluated, so a permit limit of zero boots the host with the
    /// control off while the source still reads as enforcing it. The failure
    /// compounds here, because this limiter already fails open when Redis
    /// misbehaves: nothing distinguishes a budget switched off by a typo from
    /// one suspended by an outage, and the protection against notification
    /// bombing stops existing without a single signal.
    /// <para>
    /// An entry with no key is refused for the same reason: both maps are
    /// looked up by canonical class, an empty key matches no class, and the
    /// window applies to nothing while reading as configured.
    /// </para>
    /// <para>
    /// Every member path is built to match the configuration key an operator
    /// wrote, list index included, so the failure points at the line to fix.
    /// </para>
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (KeyValuePair<string, RateWindow> entry in PerPrincipal)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                yield return new ValidationResult(
                    "Há uma janela por principal sem classe; nenhuma requisição a encontraria.",
                    [nameof(PerPrincipal)]);
                continue;
            }

            foreach (ValidationResult result in NestedOptionsValidation.Validate(
                entry.Value, $"{nameof(PerPrincipal)}:{entry.Key}"))
            {
                yield return result;
            }
        }

        foreach (KeyValuePair<string, List<RateWindow>> entry in PerRecipient)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                yield return new ValidationResult(
                    "Há janelas por destinatário sem classe; nenhuma requisição as encontraria.",
                    [nameof(PerRecipient)]);
                continue;
            }

            for (var index = 0; index < entry.Value.Count; index++)
            {
                foreach (ValidationResult result in NestedOptionsValidation.Validate(
                    entry.Value[index], $"{nameof(PerRecipient)}:{entry.Key}:{index}"))
                {
                    yield return result;
                }
            }
        }
    }
}
