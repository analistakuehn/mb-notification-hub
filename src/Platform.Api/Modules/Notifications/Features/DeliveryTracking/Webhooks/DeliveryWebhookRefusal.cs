namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Webhooks;

/// <summary>
/// Reasons this module refuses a callback that the provider adapters already
/// proved authentic.
/// <para>
/// Separate from the adapters catalogue on purpose. Those codes say the bytes
/// were not proven to come from the provider; these say the bytes were proven
/// and this hub still declines to take them. Folding the two together would
/// make a security alarm keyed on the adapter catalogue fire for a capacity
/// decision, and would put a code Notifications raises inside a contract
/// Dispatch publishes.
/// </para>
/// <para>
/// The codes are stable because they are read by machines, and they carry no
/// appended detail for the same reason the adapter codes do not: everything
/// available at that point is either personal data or caller-supplied.
/// </para>
/// </summary>
internal static class DeliveryWebhookRefusal
{
    /// <summary>The callback carries more tracked events than one request accepts.</summary>
    internal const string BatchTooLarge = "batch-too-large";
}
