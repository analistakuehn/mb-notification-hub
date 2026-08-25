using System.Net;
using System.Text.Json;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.ContactConsent;

[Collection(ContactConsentApiCollectionDefinition.Name)]
public sealed class ContactConsentAuthorizationTests(ContactConsentApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_principal_without_the_write_role_receives_403_on_every_route()
    {
        HttpClient intruder = fixture.CreateClientWithRoles(
            "notifications-producer", "Notifications.Send.Transactional");
        var recipientId = ContactConsentApi.NewRecipientId();

        (await ContactConsentApi.PutContactPointsAsync(intruder, recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", "intruso@example.com")])))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ContactConsentApi.PutConsentsAsync(intruder, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ContactConsentApi.PostDeviceAsync(intruder, recipientId, "fcm-intruso"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [RequiresDockerFact]
    public async Task An_anonymous_call_receives_401()
    {
        HttpClient anonymous = fixture.CreateClient();

        (await ContactConsentApi.PutContactPointsAsync(anonymous, ContactConsentApi.NewRecipientId(),
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", "anonimo@example.com")])))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [RequiresDockerFact]
    public async Task An_invalid_payload_answers_a_problem_document()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();

        HttpResponseMessage badChannel = await ContactConsentApi.PutContactPointsAsync(
            writer, recipientId, ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("pombo-correio", "valor")]));

        badChannel.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badChannel.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = JsonDocument.Parse(await badChannel.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").EnumerateObject().Any().ShouldBeTrue();

        HttpResponseMessage badTimezone = await ContactConsentApi.PutContactPointsAsync(
            writer, recipientId, ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", "valido@example.com")],
                timezone: "Hora/Inexistente"));
        badTimezone.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        HttpResponseMessage badSource = await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true, source: "telepatia"));
        badSource.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        HttpResponseMessage badPlatform = await ContactConsentApi.PostDeviceAsync(
            writer, recipientId, "fcm-token", platform: "symbian");
        badPlatform.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
