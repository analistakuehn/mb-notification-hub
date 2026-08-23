using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class CanonicalHashTests
{
    [Fact]
    public void A_field_containing_a_separator_byte_does_not_collide_with_two_fields()
    {
        // The 0x1F byte framed fields in the previous canonical form, so a
        // value embedding it could forge a field boundary.
        CanonicalHash.OfFields("a\u001Fb").ShouldNotBe(CanonicalHash.OfFields("a", "b"));
    }

    [Fact]
    public void An_absent_field_does_not_collide_with_an_empty_field()
    {
        CanonicalHash.OfFields((string?)null).ShouldNotBe(CanonicalHash.OfFields(""));
    }

    [Theory]
    [InlineData("V1:a", "a")]
    [InlineData("V0:", "")]
    public void A_field_spelling_the_frame_markers_literally_does_not_collide(string forged, string plain)
    {
        CanonicalHash.OfFields(forged).ShouldNotBe(CanonicalHash.OfFields(plain));
    }

    [Fact]
    public void A_marker_valued_field_next_to_an_absent_field_does_not_collide()
    {
        CanonicalHash.OfFields("A", "x").ShouldNotBe(CanonicalHash.OfFields(null, "x"));
    }

    [Fact]
    public void Digits_cannot_migrate_across_a_field_boundary()
    {
        CanonicalHash.OfFields("1", "23").ShouldNotBe(CanonicalHash.OfFields("12", "3"));
    }

    [Fact]
    public void The_hash_of_a_field_with_html_a_plus_sign_and_non_ascii_text_is_stable()
    {
        // Pinned snapshot: the canonical form must never shift with encoder or
        // framing changes, because persisted hashes vouch for approved content.
        CanonicalHash.OfFields("<b>Olá</b> + café")
            .ShouldBe("76b4109a0927a37e3f66e5b387b9f2fb70bbc6e06ce4aa0268c5c0d7dff99ae6");
    }

    [Fact]
    public void The_same_fields_always_produce_the_same_hash()
    {
        CanonicalHash.OfFields("subject", "body", null)
            .ShouldBe(CanonicalHash.OfFields("subject", "body", null));
    }
}
