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
    /// <summary>
    /// Stands in for the refused word when the document carries none worth
    /// quoting: a body that does not parse at all, and a step whose channel
    /// property is absent or null. Both leave the trail with nothing to name,
    /// so they share one stable stand-in instead of an empty witness.
    /// </summary>
    internal const string RefusedMalformedDocument = "malformed-document";

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(IReadOnlyList<DeliveryPlanStep> plan)
        => JsonSerializer.Serialize(
            plan.Select(step => new StoredStep(step.Channel.Value, step.Timeout)),
            SerializerOptions);

    /// <summary>
    /// Reads a stored plan back as one of three outcomes the caller can tell
    /// apart: the plan itself, the absence of one, or a document this code can
    /// no longer make sense of.
    /// <para>
    /// The absence and the unreadable document resolve to the same fallback,
    /// the published plan, and they are still separate answers. Absence is
    /// what every row written before this column existed carries, and refusing
    /// a fallback the plan owes over a column the row could not have had would
    /// lose an authentication code to a migration. An unreadable document is
    /// not that row: falling back there advances the notification on the
    /// published order, which may name a channel the admission had already
    /// removed, so the caller owes the trail a witness that the two cases are
    /// not the same event.
    /// </para>
    /// </summary>
    public static AdmittedPlanRead Read(string? admittedPlanJson)
    {
        if (string.IsNullOrWhiteSpace(admittedPlanJson))
        {
            return new AdmittedPlanRead.Absent();
        }

        StoredStep[]? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredStep[]>(admittedPlanJson, SerializerOptions);
        }
        catch (JsonException)
        {
            return new AdmittedPlanRead.Unreadable(RefusedMalformedDocument);
        }

        // A literal null and an empty array collapse into absence on purpose.
        // No producer emits either: the plan is only stored once the channel
        // selection rule has already rejected the request whose surviving set
        // came out empty. A fourth outcome for a document nothing writes would
        // add an arm no test can exercise honestly.
        if (stored is null || stored.Length == 0)
        {
            return new AdmittedPlanRead.Absent();
        }

        List<DeliveryPlanStep> plan = [];
        foreach (StoredStep step in stored)
        {
            Result<Channel> channel = Channel.Create(step.Channel);
            if (channel.IsFailure)
            {
                // The refused word travels raw, never the formatted error. The
                // error is the HTTP boundary codec of the module that owns the
                // vocabulary and carries a unit separator inside it; neither
                // the control character nor another module's error codec
                // belongs in this module's trail.
                return new AdmittedPlanRead.Unreadable(step.Channel ?? RefusedMalformedDocument);
            }

            plan.Add(new DeliveryPlanStep(channel.Value!, step.Timeout));
        }

        return new AdmittedPlanRead.Present(plan);
    }

    private sealed record StoredStep(string? Channel, TimeSpan? Timeout);
}

/// <summary>
/// What reading the stored delivery plan of one notification answered. The
/// three cases are deliberately distinguishable: two of them lead to the same
/// fallback today, and collapsing them would leave the one that is an anomaly
/// indistinguishable from the one that is the ordinary history of a migrated
/// row.
/// </summary>
internal abstract record AdmittedPlanRead
{
    private AdmittedPlanRead()
    {
    }

    /// <summary>The notification carries a plan and it reads back whole.</summary>
    public sealed record Present(IReadOnlyList<DeliveryPlanStep> Plan) : AdmittedPlanRead;

    /// <summary>The notification carries no plan, which is what a row older than the column carries.</summary>
    public sealed record Absent : AdmittedPlanRead;

    /// <summary>
    /// The notification carries a document this code cannot make sense of.
    /// <see cref="Refused"/> is the raw word the vocabulary turned down, or
    /// <see cref="AdmittedDeliveryPlan.RefusedMalformedDocument"/> when the
    /// document names none.
    /// </summary>
    public sealed record Unreadable(string Refused) : AdmittedPlanRead;
}
