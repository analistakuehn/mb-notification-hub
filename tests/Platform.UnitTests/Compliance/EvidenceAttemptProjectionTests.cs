using System.Text.Json;
using NotificationHub.Api.Modules.Compliance.Features.Disclosure;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.UnitTests.Compliance;

/// <summary>
/// The projection of one attempt into the answer. It is the last place where a
/// member of the published contract can be dropped or invented, so the checks
/// are on the projected value and on the serialized bytes, which is what an
/// auditor actually receives.
/// </summary>
public sealed class EvidenceAttemptProjectionTests
{
    private static readonly DateTimeOffset Anchor =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The defaults minimal APIs serialize with, so the bytes asserted here are the bytes served.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_projection_carries_the_provider_feedback_in_the_order_it_received_it()
    {
        GetNotificationEvidence.AttemptView projected = GetNotificationEvidence.ToAttempt(
            Attempt(
                deliveredAt: Anchor.AddMinutes(2),
                Feedback("sg-1", "sent", Anchor),
                Feedback("sg-2", "delivered", Anchor.AddMinutes(2))));

        projected.DeliveredAt.ShouldBe(Anchor.AddMinutes(2));
        projected.DeliveryEvents.Select(feedback => feedback.ProviderEventId)
            .ShouldBe(["sg-1", "sg-2"]);
        projected.DeliveryEvents.Select(feedback => feedback.Kind).ShouldBe(["sent", "delivered"]);
        projected.DeliveryEvents.Select(feedback => feedback.OccurredAt)
            .ShouldBe([Anchor, Anchor.AddMinutes(2)]);
        projected.DeliveryEvents[0].ProviderKey.ShouldBe("sendgrid");
    }

    [Fact]
    public void The_projection_states_an_empty_list_when_no_feedback_was_recorded()
    {
        GetNotificationEvidence.AttemptView projected = GetNotificationEvidence.ToAttempt(
            Attempt(deliveredAt: null));

        // Empty and present, never null and never dropped: the empty array is
        // the assertion that nothing was recorded for this attempt.
        projected.DeliveryEvents.ShouldBeEmpty();
        projected.DeliveredAt.ShouldBeNull();

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(projected, Options));
        written.RootElement.GetProperty("deliveryEvents").GetArrayLength().ShouldBe(0);
        written.RootElement.TryGetProperty("deliveredAt", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_projection_states_the_failure_code_of_the_feedback_that_carries_one()
    {
        GetNotificationEvidence.AttemptView projected = GetNotificationEvidence.ToAttempt(
            Attempt(
                deliveredAt: null,
                Feedback("sg-3", "bounced", Anchor, errorCode: "hard-bounce")));

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(projected, Options));
        JsonElement feedback = written.RootElement.GetProperty("deliveryEvents")[0];

        feedback.GetProperty("errorCode").GetString().ShouldBe("hard-bounce");
        feedback.GetProperty("kind").GetString().ShouldBe("bounced");

        // The attempt failed, so the answer must not claim a delivery instant
        // that nothing stamped.
        written.RootElement.TryGetProperty("deliveredAt", out _).ShouldBeFalse();
    }

    /// <summary>
    /// The named guard of the rule "the provider payload is evidence held, not
    /// evidence served". The payload carries the destination in the clear, and
    /// the only thing keeping it out of the answer is that the projection names
    /// five columns and no more.
    /// </summary>
    [Fact]
    public void The_projected_feedback_names_five_members_and_no_provider_payload()
    {
        GetNotificationEvidence.AttemptView projected = GetNotificationEvidence.ToAttempt(
            Attempt(deliveredAt: null, Feedback("sg-4", "delivered", Anchor, errorCode: "code")));

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(projected, Options));
        var members = written.RootElement.GetProperty("deliveryEvents")[0]
            .EnumerateObject()
            .Select(member => member.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        members.ShouldBe(["errorCode", "kind", "occurredAt", "providerEventId", "providerKey"]);
    }

    private static NotificationAttemptEvidence Attempt(
        DateTimeOffset? deliveredAt,
        params DeliveryEventEvidence[] feedback)
        => new()
        {
            Sequence = 1,
            Channel = "email",
            Status = deliveredAt is null ? "sent" : "delivered",
            ProviderKey = "sendgrid",
            ProviderMessageId = "sg-message-1",
            ContentHashFull = new string('a', 64),
            ContentHashMasked = new string('b', 64),
            SentAt = Anchor,
            DeliveredAt = deliveredAt,
            DeliveryEvents = feedback,
            CreatedAt = Anchor.AddMinutes(-1),
        };

    private static DeliveryEventEvidence Feedback(
        string providerEventId,
        string kind,
        DateTimeOffset occurredAt,
        string? errorCode = null)
        => new()
        {
            ProviderKey = "sendgrid",
            ProviderEventId = providerEventId,
            Kind = kind,
            OccurredAt = occurredAt,
            ErrorCode = errorCode,
        };
}
