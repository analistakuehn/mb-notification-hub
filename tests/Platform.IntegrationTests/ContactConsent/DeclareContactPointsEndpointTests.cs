using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.ContactConsent;

[Collection(ContactConsentApiCollectionDefinition.Name)]
public sealed class DeclareContactPointsEndpointTests(ContactConsentApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Declaring_a_contact_point_stores_ciphertext_and_keyed_hash_with_trail_and_event()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();
        const string EmailValue = "cliente@example.com";

        HttpResponseMessage response = await ContactConsentApi.PutContactPointsAsync(
            writer, recipientId, ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", EmailValue)],
                timezone: "America/Manaus",
                locale: "pt-BR"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.ShouldNotContain(EmailValue);
        var body = JsonDocument.Parse(responseBody);
        body.RootElement.GetProperty("timezone").GetString().ShouldBe("America/Manaus");
        Guid contactPointId = body.RootElement.GetProperty("contactPoints")[0]
            .GetProperty("contactPointId").GetGuid();

        // The stored row carries ciphertext plus the deterministic keyed hash,
        // never the plaintext value.
        var stored = await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == recipientId)
            .Select(point => new { point.Id, point.ValueEncrypted, point.ValueHash, point.Verified })
            .SingleAsync());
        stored.Id.ShouldBe(contactPointId);
        stored.ValueEncrypted.ShouldNotBe(Encoding.UTF8.GetBytes(EmailValue));
        Encoding.UTF8.GetString(stored.ValueEncrypted).ShouldNotContain(EmailValue);
        stored.ValueHash.Length.ShouldBe(64);
        stored.ValueHash.ShouldAllBe(character => char.IsAsciiHexDigitLower(character));

        // The plaintext only leaves through the explicit read of the contract.
        Result<string> revealed = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IRecipientDirectory>()
            .RevealContactValueAsync(recipientId, contactPointId, CancellationToken.None));
        revealed.IsSuccess.ShouldBeTrue();
        revealed.Value.ShouldBe(EmailValue);

        // Audit event and outbox message committed with the write.
        var auditCount = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "contact.points.declared" && audit.EntityId == recipientId));
        auditCount.ShouldBe(1);

        var outbox = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.MessageKey == recipientId)
            .Select(message => new { message.Destination, message.EventType, message.PayloadJson })
            .SingleAsync());
        outbox.Destination.ShouldBe("contacts-changed");
        outbox.EventType.ShouldBe("contact.changed");
        outbox.PayloadJson.ShouldNotContain(EmailValue);
        var payload = JsonDocument.Parse(outbox.PayloadJson);
        payload.RootElement.GetProperty("type").GetString().ShouldBe("contact.changed");
        payload.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        payload.RootElement.GetProperty("priorityClass").GetString().ShouldBe("transactional");
        payload.RootElement.GetProperty("payload").GetProperty("recipientId").GetString().ShouldBe(recipientId);
        payload.RootElement.GetProperty("payload").GetProperty("contactPointId").GetGuid().ShouldBe(contactPointId);
    }

    [RequiresDockerFact]
    public async Task The_same_value_hashes_identically_for_different_recipients()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var firstRecipient = ContactConsentApi.NewRecipientId();
        var secondRecipient = ContactConsentApi.NewRecipientId();
        const string SharedValue = "compartilhado@example.com";

        (await ContactConsentApi.PutContactPointsAsync(writer, firstRecipient,
            ContactConsentApi.ContactPointsBody([ContactConsentApi.ContactPoint("email", SharedValue)])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ContactConsentApi.PutContactPointsAsync(writer, secondRecipient,
            ContactConsentApi.ContactPointsBody([ContactConsentApi.ContactPoint("email", SharedValue)])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        List<string> hashes = await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == firstRecipient || point.RecipientId == secondRecipient)
            .Select(point => point.ValueHash)
            .ToListAsync());
        hashes.Count.ShouldBe(2);
        hashes[0].ShouldBe(hashes[1]);
    }

    [RequiresDockerFact]
    public async Task A_failing_audit_append_rolls_back_every_row_of_the_declaration()
    {
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuditTrail>();
                services.AddSingleton<IAuditTrail>(new FailingAuditTrail());
            }));
        HttpClient writer = fixture.CreateClientWithRoles(
            host, "contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();

        HttpResponseMessage failed = await ContactConsentApi.PutContactPointsAsync(
            writer, recipientId, ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", "rollback@example.com")]));

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        var profiles = await fixture.QueryContactConsentDbAsync(db => db.RecipientProfiles
            .AsNoTracking()
            .CountAsync(profile => profile.RecipientId == recipientId));
        profiles.ShouldBe(0);
        var points = await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .CountAsync(point => point.RecipientId == recipientId));
        points.ShouldBe(0);
        var outboxMessages = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId));
        outboxMessages.ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_changed_value_creates_a_new_row_and_an_absent_value_is_removed_never_deleted()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();

        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
            [
                ContactConsentApi.ContactPoint("email", "antigo@example.com"),
                ContactConsentApi.ContactPoint("sms", "+5511999990000"),
            ]))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The e-mail changes, the phone stays.
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody(
            [
                ContactConsentApi.ContactPoint("email", "novo@example.com"),
                ContactConsentApi.ContactPoint("sms", "+5511999990000"),
            ]))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == recipientId)
            .Select(point => new { point.Channel, point.RemovedAt })
            .ToListAsync());
        rows.Count.ShouldBe(3);
        rows.Count(row => row.Channel == "email" && row.RemovedAt != null).ShouldBe(1);
        rows.Count(row => row.Channel == "email" && row.RemovedAt == null).ShouldBe(1);
        rows.Count(row => row.Channel == "sms" && row.RemovedAt == null).ShouldBe(1);

        // Declaring the empty set removes everything and still deletes nothing.
        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId,
            ContactConsentApi.ContactPointsBody([]))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var afterClear = await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == recipientId)
            .Select(point => new { point.RemovedAt })
            .ToListAsync());
        afterClear.Count.ShouldBe(3);
        afterClear.ShouldAllBe(row => row.RemovedAt != null);
    }

    [RequiresDockerFact]
    public async Task An_identical_declaration_is_a_no_op_that_emits_no_event()
    {
        HttpClient writer = fixture.CreateClientWithRoles("contacts-writer", ContactConsentApi.ContactsWrite);
        var recipientId = ContactConsentApi.NewRecipientId();
        var body = ContactConsentApi.ContactPointsBody(
            [ContactConsentApi.ContactPoint("email", "idempotente@example.com")]);

        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId, body))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var outboxAfterFirst = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId));

        (await ContactConsentApi.PutContactPointsAsync(writer, recipientId, body))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var outboxAfterSecond = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId));
        outboxAfterSecond.ShouldBe(outboxAfterFirst);

        // The no-op still leaves its trail.
        var auditCount = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(audit => audit.Action == "contact.points.declared" && audit.EntityId == recipientId));
        auditCount.ShouldBe(2);
    }

    private sealed class FailingAuditTrail : IAuditTrail
    {
        public Task AppendAsync(DbTransaction transaction, AuditEntry entry, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Falha induzida no append da trilha de auditoria.");

        public Task RecordApprovalAsync(DbTransaction transaction, ApprovalGrant grant, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Falha induzida no registro de aprovação.");
    }
}
