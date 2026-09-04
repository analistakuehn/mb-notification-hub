using System.Text.Json;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Compliance.Features.Disclosure;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.UnitTests.Compliance;

/// <summary>
/// The projection of the accepted set into the answer, checked on the bytes an
/// auditor receives.
/// <para>
/// This is the last place a member can be dropped or invented, and the two
/// failures it has to catch are opposite. One is a coordinate reaching the
/// answer, which hands out capacity to fetch content on a surface meant to
/// prove which content went out. The other is a document nobody can read
/// arriving as an empty array, which is the answer stating a fact it does not
/// have.
/// </para>
/// </summary>
public sealed class EvidenceAttachmentProjectionTests
{
    private const string Handle = "aci_2f7c1d0a8b3e4f5a9c6d7e8f0a1b2c3d";

    private const string DigestProbe = "d1e5735a4c0f4a4d9b7f2c8e6a1b3d5f7091a2b3c4d5e6f708192a3b4c5d6e7f";

    private static readonly DateTimeOffset Anchor =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The defaults minimal APIs serialize with, so the bytes asserted here are the bytes served.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_answer_carries_the_frozen_composition_and_the_proof_of_the_bytes()
    {
        JsonElement written = Serialize(GetNotificationEvidence.ToAttachments(
            Evidence([Member(Recorded())], refusal: null)));

        JsonElement member = written.GetProperty("accepted").EnumerateArray().Single();
        member.GetProperty("reference").GetString().ShouldBe("att_alpha");
        member.GetProperty("contentIdentity").GetString().ShouldBe(Handle);
        member.GetProperty("name").GetString().ShouldBe("comprovante.pdf");
        member.GetProperty("mediaType").GetString().ShouldBe("application/pdf");
        member.GetProperty("length").GetInt64().ShouldBe(2048);

        JsonElement recorded = member.GetProperty("recorded");
        recorded.GetProperty("digest").GetString().ShouldBe(DigestProbe);
        recorded.GetProperty("digestAlgorithm").GetString().ShouldBe("sha-256");
        recorded.GetProperty("digestedLengthBytes").GetInt64().ShouldBe(2048);
        recorded.GetProperty("state").GetString().ShouldBe("released");
        recorded.GetProperty("releasedAt").GetDateTimeOffset().ShouldBe(Anchor.AddMinutes(1));
    }

    /// <summary>
    /// The named guard of the rule "the digest travels and the way to the bytes
    /// does not". The record the module holds also names a store, a key and a
    /// generation of the provider, and the only thing keeping them out of the
    /// answer is that this block names twelve members and no more.
    /// </summary>
    [Fact]
    public void The_projected_record_names_its_members_and_no_way_of_reaching_the_content()
    {
        JsonElement written = Serialize(GetNotificationEvidence.ToAttachments(
            Evidence([Member(Recorded())], refusal: null)));

        var members = written.GetProperty("accepted")[0].GetProperty("recorded")
            .EnumerateObject()
            .Select(member => member.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        members.ShouldBe(
        [
            "application",
            "capturedAt",
            "detectedContentType",
            "digest",
            "digestAlgorithm",
            "digestedLengthBytes",
            "reference",
            "releasedAt",
            "state",
        ]);
    }

    /// <summary>
    /// An empty array is a fact and a missing member is ignorance, and this is
    /// the pair that has to be told apart. Both are asserted in one test on
    /// purpose: separately, either could pass over an answer whose shape says
    /// nothing at all.
    /// </summary>
    [Fact]
    public void A_set_nobody_can_read_never_comes_back_as_a_notification_without_attachments()
    {
        JsonElement none = Serialize(GetNotificationEvidence.ToAttachments(
            Evidence([], refusal: null)));
        JsonElement unreadable = Serialize(GetNotificationEvidence.ToAttachments(
            Evidence(null, refusal: "unknown-schema-version")));

        none.GetProperty("accepted").GetArrayLength().ShouldBe(0);
        none.TryGetProperty("unreadable", out _).ShouldBeFalse();

        unreadable.TryGetProperty("accepted", out _).ShouldBeFalse();
        unreadable.GetProperty("unreadable").GetString().ShouldBe("unknown-schema-version");
    }

    /// <summary>
    /// The record can be missing while the composition is not, and the answer
    /// says exactly that: the row still names what was accepted, and the proof
    /// of the bytes is what nobody can produce any more.
    /// </summary>
    [Fact]
    public void A_member_whose_record_is_gone_still_carries_what_the_acceptance_froze()
    {
        JsonElement written = Serialize(GetNotificationEvidence.ToAttachments(
            Evidence([Member(recorded: null)], refusal: null)));

        JsonElement member = written.GetProperty("accepted").EnumerateArray().Single();
        member.TryGetProperty("recorded", out _).ShouldBeFalse();
        member.GetProperty("name").GetString().ShouldBe("comprovante.pdf");
        member.GetProperty("length").GetInt64().ShouldBe(2048);
    }

    private static JsonElement Serialize(GetNotificationEvidence.AttachmentsView view)
        => JsonDocument.Parse(JsonSerializer.Serialize(view, Options)).RootElement.Clone();

    private static NotificationEvidence Evidence(
        IReadOnlyList<AcceptedAttachmentEvidence>? accepted,
        string? refusal)
        => new()
        {
            Id = Guid.Empty,
            Application = "billing",
            RecipientId = "cus_1",
            Class = "transactional",
            Status = "sent",
            TemplateKey = "order-updates",
            TemplateVersion = 1,
            RequestedBy = "producer",
            ExpiresAt = Anchor.AddDays(1),
            CreatedAt = Anchor,
            VariablesMasked = JsonDocument.Parse("{}").RootElement,
            Attempts = [],
            PolicyEvaluations = [],
            AcceptedAttachments = accepted,
            AcceptedAttachmentsRefusal = refusal,
        };

    private static AcceptedAttachmentEvidence Member(AttachmentEvidence? recorded)
        => new()
        {
            Reference = "att_alpha",
            ContentIdentity = Handle,
            Name = "comprovante.pdf",
            MediaType = "application/pdf",
            Length = 2048,
            Recorded = recorded,
        };

    private static AttachmentEvidence Recorded()
        => new()
        {
            ContentIdentity = Handle,
            Reference = "att_alpha",
            Application = "billing",
            State = "released",
            DigestAlgorithm = "sha-256",
            Digest = DigestProbe,
            DigestedLengthBytes = 2048,
            DetectedContentType = "application/pdf",
            CapturedAt = Anchor,
            ReleasedAt = Anchor.AddMinutes(1),
        };
}
