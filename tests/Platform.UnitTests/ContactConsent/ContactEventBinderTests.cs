using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Features.Ingress;
using NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

namespace NotificationHub.UnitTests.ContactConsent;

/// <summary>
/// The binder is permissive about values and strict about the declared
/// collection. The asymmetry is the whole point: a bad value has a validator
/// to answer it, while an absent collection has no safe reading at all in a
/// declarative write.
/// </summary>
public sealed class ContactEventBinderTests
{
    [Fact]
    public void A_body_without_the_contact_point_collection_binds_to_nothing()
    {
        // Reading the absence as an empty declaration would remove every
        // contact point of the recipient on behalf of a producer that said
        // nothing about them.
        ContactEventBinder.BindContactPoints(Parse("""{ "timezone": "America/Sao_Paulo" }""")).ShouldBeNull();
        ContactEventBinder.BindContactPoints(Parse("""{ "contactPoints": "email" }""")).ShouldBeNull();
    }

    [Fact]
    public void An_explicitly_empty_collection_binds_to_an_empty_declaration()
    {
        DeclareContactPoints.Command? command =
            ContactEventBinder.BindContactPoints(Parse("""{ "contactPoints": [] }"""));

        command.ShouldNotBeNull();
        command.ContactPoints.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_field_inside_an_entry_becomes_an_empty_value_for_the_validator()
    {
        DeclareContactPoints.Command? command = ContactEventBinder.BindContactPoints(Parse("""
            { "contactPoints": [ { "value": 42 } ] }
            """));

        command.ShouldNotBeNull();
        DeclareContactPoints.ContactPointDeclaration declaration = command.ContactPoints.ShouldHaveSingleItem();
        declaration.Channel.ShouldBe(string.Empty);
        declaration.Value.ShouldBe(string.Empty);
        declaration.Verified.ShouldBeFalse();
    }

    [Fact]
    public void Profile_preferences_ride_along_and_stay_absent_when_absent()
    {
        DeclareContactPoints.Command? withPreferences = ContactEventBinder.BindContactPoints(Parse("""
            { "timezone": "America/Manaus", "locale": "pt-BR", "contactPoints": [] }
            """));
        DeclareContactPoints.Command? without =
            ContactEventBinder.BindContactPoints(Parse("""{ "contactPoints": [] }"""));

        withPreferences!.Timezone.ShouldBe("America/Manaus");
        withPreferences.Locale.ShouldBe("pt-BR");
        without!.Timezone.ShouldBeNull();
        without.Locale.ShouldBeNull();
    }

    [Fact]
    public void A_body_without_the_consent_collection_binds_to_nothing()
        => ContactEventBinder.BindConsents(Parse("""{ "purpose": "marketing" }""")).ShouldBeNull();

    [Fact]
    public void A_consent_entry_binds_every_field_the_ledger_records()
    {
        DeclareConsents.Command? command = ContactEventBinder.BindConsents(Parse("""
            {
              "consents": [
                {
                  "purpose": "marketing",
                  "channel": "email",
                  "granted": true,
                  "source": "atendimento",
                  "termsVersion": "v3"
                }
              ]
            }
            """));

        DeclareConsents.ConsentDeclaration declaration = command!.Consents.ShouldHaveSingleItem();
        declaration.Purpose.ShouldBe("marketing");
        declaration.Channel.ShouldBe("email");
        declaration.Granted.ShouldBeTrue();
        declaration.Source.ShouldBe("atendimento");
        declaration.TermsVersion.ShouldBe("v3");
    }

    [Fact]
    public void A_consent_entry_without_a_stance_binds_as_not_granted()
    {
        DeclareConsents.Command? command = ContactEventBinder.BindConsents(Parse("""
            { "consents": [ { "purpose": "marketing", "channel": "email" } ] }
            """));

        command!.Consents.ShouldHaveSingleItem().Granted.ShouldBeFalse();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
