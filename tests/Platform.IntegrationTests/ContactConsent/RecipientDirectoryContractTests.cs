using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.ContactConsent;

/// <summary>
/// Consumes the published contract exactly as a sibling module would: through
/// dependency injection, from outside the module, never touching its store.
/// </summary>
[Collection(ContactConsentApiCollectionDefinition.Name)]
public sealed class RecipientDirectoryContractTests(ContactConsentApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_snapshot_resolves_profile_contacts_consents_and_devices()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
                [
                    ContactConsentApi.ContactPoint("email", $"{recipientId}@example.com", verified: true),
                    ContactConsentApi.ContactPoint("sms", "+5511999990000", verified: false),
                ],
                locale: "pt-BR"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true),
            ContactConsentApi.ConsentEntry("marketing", "sms", granted: false)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ContactConsentApi.PostDeviceAsync(writer, recipientId, $"fcm-{Guid.NewGuid():N}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        Result<RecipientSnapshot> result = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .FindAsync(recipientId, CancellationToken.None));

        result.IsSuccess.ShouldBeTrue();
        RecipientSnapshot snapshot = result.Value!;
        snapshot.RecipientId.ShouldBe(recipientId);
        snapshot.Timezone.ShouldBe("America/Sao_Paulo");
        snapshot.Locale.ShouldBe("pt-BR");

        snapshot.ContactPoints.Count.ShouldBe(2);
        snapshot.ContactPoints.Single(point => point.Channel == "email").Verified.ShouldBeTrue();
        snapshot.ContactPoints.Single(point => point.Channel == "sms").Verified.ShouldBeFalse();

        snapshot.Consents.Count.ShouldBe(2);
        snapshot.Consents.Single(consent => consent.Channel == "email").Granted.ShouldBeTrue();
        snapshot.Consents.Single(consent => consent.Channel == "sms").Granted.ShouldBeFalse();

        snapshot.Devices.Count.ShouldBe(1);
        snapshot.Devices[0].Platform.ShouldBe("android");
    }

    [RequiresDockerFact]
    public async Task A_declared_timezone_replaces_the_default_in_the_snapshot()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", $"{recipientId}@example.com")],
                timezone: "America/Manaus"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        Result<RecipientSnapshot> result = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .FindAsync(recipientId, CancellationToken.None));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Timezone.ShouldBe("America/Manaus");
    }

    [RequiresDockerFact]
    public async Task The_consent_in_force_survives_a_contact_value_change()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", "antigo@example.com")])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: false)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // The e-mail changes: the revocation recorded for the channel stays
        // in force, anchored on the removed value.
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", "novo@example.com")])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        Result<RecipientSnapshot> result = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .FindAsync(recipientId, CancellationToken.None));

        result.IsSuccess.ShouldBeTrue();
        ConsentDecision decision = result.Value!.Consents
            .Single(consent => consent.Purpose == "marketing" && consent.Channel == "email");
        decision.Granted.ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task The_reveal_read_returns_the_plaintext_of_an_active_point_only()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();
        var email = $"{recipientId}@example.com";
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody([ContactConsentApi.ContactPoint("email", email)])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Result<RecipientSnapshot> snapshot = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .FindAsync(recipientId, CancellationToken.None));
        Guid contactPointId = snapshot.Value!.ContactPoints.Single().ContactPointId;

        Result<string> revealed = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .RevealContactValueAsync(recipientId, contactPointId, CancellationToken.None));
        revealed.IsSuccess.ShouldBeTrue();
        revealed.Value.ShouldBe(email);

        // Once removed, the value no longer leaves the module.
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody([]))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Result<string> afterRemoval = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .RevealContactValueAsync(recipientId, contactPointId, CancellationToken.None));
        afterRemoval.IsFailure.ShouldBeTrue();
        afterRemoval.ErrorKind.ShouldBe(ResultErrorKind.NotFound);
    }

    [RequiresDockerFact]
    public async Task An_unknown_recipient_resolves_as_not_found()
    {
        Result<RecipientSnapshot> result = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .FindAsync(ContactConsentApi.NewRecipientId(), CancellationToken.None));

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.NotFound);
    }
}
