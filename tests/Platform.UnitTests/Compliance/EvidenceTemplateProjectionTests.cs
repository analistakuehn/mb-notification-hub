using System.Text.Json;
using NotificationHub.Api.Modules.Compliance.Features.Disclosure;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Compliance;

/// <summary>
/// The projection of the historical template version into the answer, held on
/// the one axis where an absence used to carry more than one meaning. The
/// catalog omits the layout for a version that pinned none and for a version
/// whose pin it could not vouch for, so the projection has to carry the pin on
/// its own member or the difference dies at this boundary and an auditor reads
/// "this went out with no wrapper" over a message that had one.
/// </summary>
public sealed class EvidenceTemplateProjectionTests
{
    private static readonly DateTimeOffset Anchor =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The defaults minimal APIs serialize with, so the bytes asserted here are the bytes served.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_version_that_pinned_no_layout_carries_neither_the_pin_nor_the_layout()
    {
        GetNotificationEvidence.TemplateVersionView projected =
            GetNotificationEvidence.ToTemplate(Version(pin: null, layout: null));

        projected.LayoutPin.ShouldBeNull();
        projected.Layout.ShouldBeNull();

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(projected, Options));
        written.RootElement.TryGetProperty("layoutPin", out _).ShouldBeFalse();
        written.RootElement.TryGetProperty("layout", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_pin_that_resolved_carries_the_declaration_beside_the_hash_it_resolved_to()
    {
        GetNotificationEvidence.TemplateVersionView projected =
            GetNotificationEvidence.ToTemplate(Version(Pin(), Layout()));

        projected.LayoutPin.ShouldNotBeNull().LayoutKey.ShouldBe("marca-institucional");
        projected.LayoutPin.Version.ShouldBe(4);
        projected.Layout.ShouldNotBeNull().ContentHash.ShouldBe(LayoutHash);

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(projected, Options));
        written.RootElement.GetProperty("layoutPin").GetProperty("version").GetInt32().ShouldBe(4);
        written.RootElement.GetProperty("layout").GetProperty("contentHash").GetString().ShouldBe(LayoutHash);
    }

    /// <summary>
    /// The state the whole split exists for. The version pinned a wrapper, the
    /// catalog withheld it, and the answer says both things: there was a frame,
    /// and this answer carries no hash for it. Without the pin the bytes here
    /// would be the bytes of a version that framed its message with nothing.
    /// </summary>
    [Fact]
    public void A_pin_the_catalog_withheld_still_states_that_the_message_was_framed()
    {
        GetNotificationEvidence.TemplateVersionView projected =
            GetNotificationEvidence.ToTemplate(Version(Pin(), layout: null));

        projected.LayoutPin.ShouldNotBeNull().LayoutKey.ShouldBe("marca-institucional");
        projected.LayoutPin.Version.ShouldBe(4);
        projected.Layout.ShouldBeNull();

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(projected, Options));
        written.RootElement.GetProperty("layoutPin").GetProperty("layoutKey").GetString()
            .ShouldBe("marca-institucional");
        written.RootElement.TryGetProperty("layout", out _).ShouldBeFalse();
    }

    /// <summary>
    /// The three states are asserted against each other rather than one at a
    /// time, because the defect being closed is that two of them serialized
    /// identically. Comparing the bytes is what states that a reader can tell
    /// them apart without knowing how the answer was built.
    /// </summary>
    [Fact]
    public void The_three_states_of_the_layout_axis_serialize_to_three_different_answers()
    {
        var unframed = Serialize(Version(pin: null, layout: null));
        var resolved = Serialize(Version(Pin(), Layout()));
        var withheld = Serialize(Version(Pin(), layout: null));

        unframed.ShouldNotBe(resolved);
        unframed.ShouldNotBe(withheld);
        resolved.ShouldNotBe(withheld);
    }

    private const string LayoutHash = "b6d1f7a0c3e5498a2f70bd41c8e6039a5471dc82f0b39e6d47a1c05f8e23b9d4";

    private const string VersionHash = "1a2b3c4d5e6f70819293a4b5c6d7e8f90112233445566778899aabbccddeeff0";

    private static string Serialize(HistoricalTemplateVersion version)
        => JsonSerializer.Serialize(GetNotificationEvidence.ToTemplate(version), Options);

    private static HistoricalLayoutPin Pin() => new()
    {
        LayoutKey = "marca-institucional",
        Version = 4,
    };

    private static HistoricalLayoutVersion Layout() => new()
    {
        LayoutKey = "marca-institucional",
        Version = 4,
        VersionStatus = "published",
        ContentHash = LayoutHash,
        PublishedAt = Anchor,
    };

    private static HistoricalTemplateVersion Version(
        HistoricalLayoutPin? pin,
        HistoricalLayoutVersion? layout) => new()
    {
        Application = "araia-cambio",
        TemplateKey = "pedido-atualizado",
        Version = 7,
        VersionStatus = "published",
        TemplateStatus = "active",
        Class = "transactional",
        OwnerTeam = "pagamentos",
        Purpose = "transactional",
        LegalBasis = "contract",
        SensitiveVariables = [],
        ContentHash = VersionHash,
        PublishedAt = Anchor,
        LayoutPin = pin,
        Layout = layout,
    };
}
