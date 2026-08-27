using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;

namespace NotificationHub.UnitTests.ContactConsent;

public sealed class ConsentPurposeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static Consent Record(string purpose, bool granted)
        => Consent.Record(Guid.CreateVersion7(), purpose, granted, ConsentSources.App, "cus_01", "v1", Now);

    [Theory]
    [InlineData("Marketing", "marketing")]
    [InlineData("MARKETING", "marketing")]
    [InlineData(" marketing", "marketing")]
    [InlineData("marketing ", "marketing")]
    [InlineData("\tMarketing-Updates\n", "marketing-updates")]
    public void Casing_and_surrounding_whitespace_never_reach_the_key(string declared, string expected)
        => ConsentPurpose.Canonicalize(declared).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_purpose_that_declares_nothing_canonicalizes_to_the_empty_key(string? declared)
        => ConsentPurpose.Canonicalize(declared).ShouldBe(string.Empty);

    [Fact]
    public void Canonicalizing_an_already_canonical_key_changes_nothing()
    {
        var once = ConsentPurpose.Canonicalize(" Marketing ");

        ConsentPurpose.Canonicalize(once).ShouldBe(once);
    }

    [Fact]
    public void Internal_spacing_stays_because_it_is_a_different_purpose()
        => ConsentPurpose.Canonicalize("market ing").ShouldBe("market ing");

    [Fact]
    public void The_ledger_entry_stores_the_canonical_key_not_the_declared_spelling()
        => Record(" Marketing ", granted: true).Purpose.ShouldBe("marketing");

    [Fact]
    public void An_opt_out_lands_on_the_same_key_as_the_grant_it_revokes()
    {
        Consent grant = Record("marketing", granted: true);
        Consent revocation = Record("Marketing", granted: false);

        revocation.Purpose.ShouldBe(grant.Purpose);
    }

    [Fact]
    public void A_blank_purpose_is_rejected_at_the_aggregate()
        => Should.Throw<ArgumentException>(() => Record("   ", granted: true));

    [Fact]
    public void A_canonical_key_above_the_ledger_ceiling_is_rejected_at_the_aggregate()
        => Should.Throw<ArgumentException>(
            () => Record(new string('a', ConsentPurpose.MaxLength + 1), granted: true));

    [Fact]
    public void Trimming_can_bring_a_declaration_back_under_the_ceiling()
        => Record($"  {new string('a', ConsentPurpose.MaxLength)}  ", granted: true)
            .Purpose.Length.ShouldBe(ConsentPurpose.MaxLength);
}
