using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// The delivery plan as the Policy stage left it for one notification: the
/// published order with every channel the class did not allow, the published
/// version shipped no content for, or the recipient could not be reached on,
/// already removed.
/// <para>
/// It is stored with the notification instead of re-derived at fallback time,
/// because the published policy is a moving target by design: activating a
/// plan and rolling one back are both republications of the class policy. A
/// fallback that re-read the current version would change the plan of
/// notifications already in flight, which turns the documented rollback into a
/// behaviour change for messages already admitted, and would advance to a
/// channel the admission had already refused.
/// </para>
/// <para>
/// What the stored plan deliberately does not freeze is eligibility. Consent
/// and suppression are re-read at the moment the next step is chosen, because
/// a destination that died between the admission and the deadline must not be
/// addressed: the plan says which channels are in play and in what order, and
/// the ledger says whether the one that comes next may still be used.
/// </para>
/// </summary>
internal static class AdmittedDeliveryPlan
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(IReadOnlyList<DeliveryPlanStep> plan)
        => JsonSerializer.Serialize(
            plan.Select(step => new StoredStep(step.Channel.Value, step.Timeout)),
            SerializerOptions);

    /// <summary>
    /// Reads a stored plan back, or null when the notification carries none.
    /// <para>
    /// Null is what every row written before this column existed carries, and
    /// what a document this code can no longer make sense of resolves to. The
    /// caller falls back to the published plan for both: refusing a fallback
    /// the plan owes, over a column the row could not have had, would lose an
    /// authentication code to a migration.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DeliveryPlanStep>? Read(string? admittedPlanJson)
    {
        if (string.IsNullOrWhiteSpace(admittedPlanJson))
        {
            return null;
        }

        StoredStep[]? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredStep[]>(admittedPlanJson, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (stored is null || stored.Length == 0)
        {
            return null;
        }

        List<DeliveryPlanStep> plan = [];
        foreach (StoredStep step in stored)
        {
            Result<Channel> channel = Channel.Create(step.Channel);
            if (channel.IsFailure)
            {
                return null;
            }

            plan.Add(new DeliveryPlanStep(channel.Value!, step.Timeout));
        }

        return plan;
    }

    private sealed record StoredStep(string? Channel, TimeSpan? Timeout);
}
