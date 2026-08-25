using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.ContactConsent;

[Collection(ContactConsentApiCollectionDefinition.Name)]
public sealed class RegisterDeviceEndpointTests(ContactConsentApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Registering_a_device_creates_one_row_and_the_profile_on_first_contact()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();
        var token = $"fcm-{Guid.NewGuid():N}";

        HttpResponseMessage response = await ContactConsentApi.PostDeviceAsync(
            writer, recipientId, token, platform: "android", appVersion: "3.1.0");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.ShouldNotContain(token);
        var body = JsonDocument.Parse(responseBody);
        Guid deviceTokenId = body.RootElement.GetProperty("deviceTokenId").GetGuid();

        var stored = await fixture.QueryContactConsentDbAsync(db => db.DeviceTokens
            .AsNoTracking()
            .Where(device => device.RecipientId == recipientId)
            .Select(device => new
            {
                device.Id,
                device.Token,
                device.Platform,
                device.AppVersion,
                device.RegisteredAt,
                device.LastSeenAt,
                device.InvalidatedAt,
            })
            .SingleAsync());
        stored.Id.ShouldBe(deviceTokenId);
        stored.Token.ShouldBe(token);
        stored.Platform.ShouldBe("android");
        stored.AppVersion.ShouldBe("3.1.0");
        stored.LastSeenAt.ShouldBe(stored.RegisteredAt);
        stored.InvalidatedAt.ShouldBeNull();

        var profileExists = await fixture.QueryContactConsentDbAsync(db => db.RecipientProfiles
            .AsNoTracking()
            .AnyAsync(profile => profile.RecipientId == recipientId));
        profileExists.ShouldBeTrue();

        var auditCount = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "device.registered"
                && audit.EntityId == deviceTokenId.ToString()));
        auditCount.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task Reposting_the_same_token_refreshes_last_seen_without_duplicating()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();
        var token = $"fcm-{Guid.NewGuid():N}";

        HttpResponseMessage first = await ContactConsentApi.PostDeviceAsync(
            writer, recipientId, token, appVersion: "3.1.0");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Guid deviceTokenId = firstBody.RootElement.GetProperty("deviceTokenId").GetGuid();
        DateTimeOffset firstSeen = await fixture.QueryContactConsentDbAsync(db => db.DeviceTokens
            .AsNoTracking()
            .Where(device => device.Id == deviceTokenId)
            .Select(device => device.LastSeenAt)
            .SingleAsync());

        HttpResponseMessage second = await ContactConsentApi.PostDeviceAsync(
            writer, recipientId, token, appVersion: "3.2.0");

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        secondBody.RootElement.GetProperty("deviceTokenId").GetGuid().ShouldBe(deviceTokenId);

        var rows = await fixture.QueryContactConsentDbAsync(db => db.DeviceTokens
            .AsNoTracking()
            .Where(device => device.RecipientId == recipientId)
            .Select(device => new { device.LastSeenAt, device.AppVersion })
            .ToListAsync());
        rows.Count.ShouldBe(1);
        rows[0].LastSeenAt.ShouldBeGreaterThan(firstSeen);
        rows[0].AppVersion.ShouldBe("3.2.0");
    }

    [RequiresDockerFact]
    public async Task A_second_token_registers_as_another_device_of_the_same_recipient()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();

        (await ContactConsentApi.PostDeviceAsync(writer, recipientId, $"fcm-{Guid.NewGuid():N}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ContactConsentApi.PostDeviceAsync(writer, recipientId, $"fcm-{Guid.NewGuid():N}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var count = await fixture.QueryContactConsentDbAsync(db => db.DeviceTokens
            .AsNoTracking()
            .CountAsync(device => device.RecipientId == recipientId));
        count.ShouldBe(2);
    }
}
