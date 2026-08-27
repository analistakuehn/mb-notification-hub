using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.ContactConsent;

[Collection(ContactConsentApiCollectionDefinition.Name)]
public sealed class DeclareConsentsEndpointTests(ContactConsentApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_new_consent_state_appends_a_ledger_record_with_trail_and_event()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = await SeedContactAsync(writer);

        HttpResponseMessage response = await ContactConsentApi.PutConsentsAsync(
            writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true, source: "app", termsVersion: "v3"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement consent = body.RootElement.GetProperty("consents")[0];
        consent.GetProperty("purpose").GetString().ShouldBe("marketing");
        consent.GetProperty("channel").GetString().ShouldBe("email");
        consent.GetProperty("granted").GetBoolean().ShouldBeTrue();
        consent.GetProperty("termsVersion").GetString().ShouldBe("v3");

        List<LedgerRecord> ledger = await LedgerAsync(recipientId);
        ledger.Count.ShouldBe(1);
        ledger[0].Granted.ShouldBeTrue();
        ledger[0].Source.ShouldBe("app");

        var outboxCount = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId && message.EventType == "consent.changed"));
        outboxCount.ShouldBe(1);

        var auditCount = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "consents.declared" && audit.EntityId == recipientId));
        auditCount.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task An_identical_declaration_is_a_no_op_that_answers_the_state_in_force()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = await SeedContactAsync(writer);
        var entry = ContactConsentApi.ConsentEntry("marketing", "email", granted: true);

        (await ContactConsentApi.PutConsentsAsync(writer, recipientId, entry))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpResponseMessage replay = await ContactConsentApi.PutConsentsAsync(writer, recipientId, entry);

        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("consents").GetArrayLength().ShouldBe(1);
        body.RootElement.GetProperty("consents")[0].GetProperty("granted").GetBoolean().ShouldBeTrue();

        List<LedgerRecord> ledger = await LedgerAsync(recipientId);
        ledger.Count.ShouldBe(1);

        var outboxCount = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId && message.EventType == "consent.changed"));
        outboxCount.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_revocation_appends_a_new_record_and_preserves_the_grant_history()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = await SeedContactAsync(writer);

        (await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpResponseMessage revocation = await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry(
                "marketing", "email", granted: false, source: "atendimento", termsVersion: "v2"));

        revocation.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await revocation.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("consents")[0].GetProperty("granted").GetBoolean().ShouldBeFalse();

        List<LedgerRecord> ledger = await LedgerAsync(recipientId);
        ledger.Count.ShouldBe(2);
        ledger.OrderBy(record => record.RecordedAt).First().Granted.ShouldBeTrue();
        ledger.OrderBy(record => record.RecordedAt).Last().Granted.ShouldBeFalse();
        ledger.OrderBy(record => record.RecordedAt).Last().Source.ShouldBe("atendimento");
    }

    [RequiresDockerFact]
    public async Task A_revocation_spelled_in_another_case_revokes_the_grant_it_names()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = await SeedContactAsync(writer);

        (await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpResponseMessage revocation = await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry(" Marketing ", "email", granted: false));

        revocation.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await revocation.Content.ReadAsStringAsync());
        JsonElement consents = body.RootElement.GetProperty("consents");
        consents.GetArrayLength().ShouldBe(1);
        consents[0].GetProperty("purpose").GetString().ShouldBe("marketing");
        consents[0].GetProperty("granted").GetBoolean().ShouldBeFalse();

        (await LedgerAsync(recipientId)).Count.ShouldBe(2);
    }

    [RequiresDockerFact]
    public async Task A_grant_redeclared_in_another_case_is_the_same_state_and_records_nothing()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = await SeedContactAsync(writer);

        (await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpResponseMessage replay = await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("MARKETING", "email", granted: true));

        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LedgerAsync(recipientId)).Count.ShouldBe(1);

        var outboxCount = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId && message.EventType == "consent.changed"));
        outboxCount.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task One_request_declaring_two_spellings_of_a_purpose_is_refused_whole()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = await SeedContactAsync(writer);

        HttpResponseMessage response = await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true),
            ContactConsentApi.ConsentEntry("Marketing", "email", granted: false));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LedgerAsync(recipientId)).ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task The_announcement_carries_the_canonical_key_the_domains_correlate_on()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = await SeedContactAsync(writer);

        (await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry(" Marketing ", "email", granted: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var payload = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.MessageKey == recipientId
                && message.EventType == "araia.notification.consent_changed.v1")
            .Select(message => message.PayloadJson)
            .SingleAsync());

        var announced = JsonDocument.Parse(payload);
        announced.RootElement.GetProperty("data").GetProperty("purpose").GetString().ShouldBe("marketing");
    }

    [RequiresDockerFact]
    public async Task A_consent_for_a_channel_without_an_active_contact_point_is_rejected()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = await SeedContactAsync(writer);

        HttpResponseMessage response = await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "whatsapp", granted: true));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("type").GetString().ShouldBe("no-contact-point-for-channel");
        (await LedgerAsync(recipientId)).ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task An_unknown_recipient_is_rejected_as_not_found()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);

        HttpResponseMessage response = await ContactConsentApi.PutConsentsAsync(
            writer, ContactConsentApi.NewRecipientId(),
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("type").GetString().ShouldBe("recipient-not-found");
    }

    [RequiresDockerFact]
    public async Task The_consent_table_rejects_updates_by_construction()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = await SeedContactAsync(writer);
        (await ContactConsentApi.PutConsentsAsync(writer, recipientId,
            ContactConsentApi.ConsentEntry("marketing", "email", granted: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid consentId = (await LedgerAsync(recipientId)).Single().Id;

        PostgresException rejection = await Should.ThrowAsync<PostgresException>(
            () => fixture.QueryContactConsentDbAsync(db => db.Database.ExecuteSqlAsync(
                $"UPDATE contactconsent.consent SET granted = false WHERE id = {consentId}")));

        rejection.Message.ShouldContain("append-only");
    }

    private static async Task<string> SeedContactAsync(HttpClient writer)
    {
        var recipientId = ContactConsentApi.NewRecipientId();
        HttpResponseMessage seeded = await ContactConsentApi.PutContactPointsAsync(
            writer, recipientId, ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", $"{recipientId}@example.com")]));
        seeded.StatusCode.ShouldBe(HttpStatusCode.OK);
        return recipientId;
    }

    private Task<List<LedgerRecord>> LedgerAsync(string recipientId)
        => fixture.QueryContactConsentDbAsync(db => db.Consents
            .AsNoTracking()
            .Join(
                db.ContactPoints.AsNoTracking().Where(point => point.RecipientId == recipientId),
                consent => consent.ContactPointId,
                point => point.Id,
                (consent, point) => new LedgerRecord(
                    consent.Id, consent.Granted, consent.Source, consent.RecordedAt))
            .ToListAsync());

    private sealed record LedgerRecord(Guid Id, bool Granted, string Source, DateTimeOffset RecordedAt);
}
