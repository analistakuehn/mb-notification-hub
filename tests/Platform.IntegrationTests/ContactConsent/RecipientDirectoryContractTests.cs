using System.Net;
using Microsoft.EntityFrameworkCore;
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
    public async Task A_record_written_before_the_key_was_canonical_resolves_into_one_decision()
    {
        // The ledger rejects UPDATE, so the rows a looser write path left
        // behind are repaired where they are read, not where they are stored.
        // This is that repair: one lineage per canonical purpose, whatever
        // spelling each record carries.
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", $"{recipientId}@example.com")])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid contactPointId = await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == recipientId)
            .Select(point => point.Id)
            .SingleAsync());
        await AppendLegacyGrantAsync(contactPointId, " Marketing ");

        (await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: false)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        Result<RecipientSnapshot> result = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .FindAsync(recipientId, CancellationToken.None));

        result.IsSuccess.ShouldBeTrue();
        ConsentDecision decision = result.Value!.Consents.ShouldHaveSingleItem();
        decision.Purpose.ShouldBe("marketing");
        decision.Granted.ShouldBeFalse();

        // The record keeps the spelling it was written with: the resolution
        // canonicalizes the key, it never rewrites the declaration.
        List<string> stored = await fixture.QueryContactConsentDbAsync(db => db.Consents
            .AsNoTracking()
            .Where(consent => consent.ContactPointId == contactPointId)
            .Select(consent => consent.Purpose)
            .ToListAsync());
        stored.ShouldBe([" Marketing ", "marketing"], ignoreOrder: true);
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

    /// <summary>
    /// Appends a grant straight to the table, bypassing the aggregate, to
    /// stand for a row the ledger already holds under a spelling no write path
    /// produces any more.
    /// </summary>
    private Task<int> AppendLegacyGrantAsync(Guid contactPointId, string purpose)
    {
        var id = Guid.CreateVersion7();
        DateTimeOffset recordedAt = DateTimeOffset.UtcNow.AddDays(-1);
        return fixture.QueryContactConsentDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO contactconsent.consent
                (id, contact_point_id, purpose, granted, source, actor_id, terms_version, recorded_at)
            VALUES
                ({id}, {contactPointId}, {purpose}, true, 'importacao', 'legado', 'v1', {recordedAt})
            """));
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
