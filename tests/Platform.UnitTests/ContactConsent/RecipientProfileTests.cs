using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.UnitTests.ContactConsent;

public sealed class RecipientProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_profile_without_a_declared_timezone_falls_back_to_the_platform_default()
    {
        var profile = RecipientProfile.Create("cus_01", timezone: null, locale: null, Now);

        profile.Timezone.ShouldBeNull();
        profile.EffectiveTimezone.ShouldBe("America/Sao_Paulo");
    }

    [Fact]
    public void Applying_the_same_preferences_changes_nothing()
    {
        var profile = RecipientProfile.Create("cus_01", "America/Manaus", "pt-BR", Now);

        var changed = profile.ApplyPreferences("America/Manaus", "pt-BR", Now.AddMinutes(5));

        changed.ShouldBeFalse();
        profile.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void An_absent_preference_leaves_the_stored_value_untouched()
    {
        var profile = RecipientProfile.Create("cus_01", "America/Manaus", "pt-BR", Now);

        var changed = profile.ApplyPreferences(timezone: null, locale: null, Now.AddMinutes(5));

        changed.ShouldBeFalse();
        profile.Timezone.ShouldBe("America/Manaus");
        profile.Locale.ShouldBe("pt-BR");
    }

    [Fact]
    public void A_new_preference_applies_and_stamps_the_update_instant()
    {
        var profile = RecipientProfile.Create("cus_01", "America/Manaus", "pt-BR", Now);
        DateTimeOffset later = Now.AddMinutes(5);

        var changed = profile.ApplyPreferences("America/Sao_Paulo", locale: null, later);

        changed.ShouldBeTrue();
        profile.Timezone.ShouldBe("America/Sao_Paulo");
        profile.Locale.ShouldBe("pt-BR");
        profile.UpdatedAt.ShouldBe(later);
    }
}
