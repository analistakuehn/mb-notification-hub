using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Authorization;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.ContactConsent;

/// <summary>
/// The reversal route. A suppression is an automatic decision to stop
/// addressing a person, so the only thing that undoes it is a named human act,
/// and the trail has to be able to say who performed it.
/// </summary>
[Collection(ContactConsentApiCollectionDefinition.Name)]
public sealed class SuppressionRemovalEndpointTests(ContactConsentApiFixture fixture)
{
    private const string Operator = "suppression-operator";

    [RequiresDockerFact]
    public async Task Removing_a_suppression_records_the_action_with_its_actor_and_reopens_the_channel()
    {
        var recipientId = ContactConsentApi.NewRecipientId();
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Email);
        await SuppressAsync(recipientId, contactPointId, ContactChannels.Email);

        HttpResponseMessage response = await RemoveAsync(
            OperatorClient(), recipientId, contactPointId, "cliente confirmou o endereço por telefone");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("contactPointId").GetGuid().ShouldBe(contactPointId);

        // The row is stamped and kept: why a message was never sent has to
        // survive the reversal.
        var removed = await fixture.QueryContactConsentDbAsync(db => db.Suppressions
            .AsNoTracking()
            .Where(suppression => suppression.ContactPointId == contactPointId)
            .Select(suppression => new { suppression.RemovedAt, suppression.RemovedBy })
            .SingleAsync());
        removed.RemovedAt.ShouldNotBeNull();
        removed.RemovedBy.ShouldBe(Operator);

        var trail = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.Action == "suppression.removed"
                && entry.EntityId == contactPointId.ToString())
            .Select(entry => new { entry.ActorId, entry.ActorType, entry.DetailsJson })
            .SingleAsync());
        trail.ActorId.ShouldBe(Operator);
        trail.ActorType.ShouldBe("user");
        trail.DetailsJson.ShouldContain("cliente confirmou o endereço por telefone");

        // The channel is addressable again, in the same read the pipeline makes.
        RecipientSnapshot snapshot = await SnapshotAsync(recipientId);
        snapshot.Suppressions.ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task Removing_what_is_not_suppressed_changes_nothing_and_still_leaves_a_trail()
    {
        var recipientId = ContactConsentApi.NewRecipientId();
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Sms);

        HttpResponseMessage response = await RemoveAsync(
            OperatorClient(), recipientId, contactPointId, "verificação de rotina");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.QueryContactConsentDbAsync(db => db.Suppressions
            .AsNoTracking()
            .CountAsync(suppression => suppression.ContactPointId == contactPointId)))
            .ShouldBe(0);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == "suppression.removed"
                && entry.EntityId == contactPointId.ToString())))
            .ShouldBe(1, "o no-op declarativo grava a própria trilha.");
    }

    [RequiresDockerFact]
    public async Task An_unknown_contact_point_answers_not_found_without_confirming_anything_else()
    {
        HttpResponseMessage response = await RemoveAsync(
            OperatorClient(), ContactConsentApi.NewRecipientId(), Guid.CreateVersion7(), "engano");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("contact-point-not-found");
    }

    [RequiresDockerFact]
    public async Task The_write_role_of_the_registration_system_does_not_carry_the_reversal()
    {
        var recipientId = ContactConsentApi.NewRecipientId();
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Email);
        await SuppressAsync(recipientId, contactPointId, ContactChannels.Email);

        HttpClient registration = fixture.CreateClientWithRoles(
            "registration-system", ContactConsentApi.ContactsWrite);
        HttpResponseMessage response = await RemoveAsync(
            registration, recipientId, contactPointId, "tentativa indevida");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await fixture.QueryContactConsentDbAsync(db => db.Suppressions
            .AsNoTracking()
            .CountAsync(suppression => suppression.ContactPointId == contactPointId
                && suppression.RemovedAt == null)))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_reversal_without_a_justification_is_refused_before_it_reaches_the_ledger()
    {
        var recipientId = ContactConsentApi.NewRecipientId();
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Email);
        await SuppressAsync(recipientId, contactPointId, ContactChannels.Email);

        HttpResponseMessage response = await RemoveAsync(
            OperatorClient(), recipientId, contactPointId, justification: "");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.QueryContactConsentDbAsync(db => db.Suppressions
            .AsNoTracking()
            .CountAsync(suppression => suppression.ContactPointId == contactPointId
                && suppression.RemovedAt == null)))
            .ShouldBe(1);
    }

    private HttpClient OperatorClient()
        => fixture.CreateClientWithRoles(Operator, ContactConsentAuthorizationSetup.SuppressionRemovalRole);

    private static Task<HttpResponseMessage> RemoveAsync(
        HttpClient client,
        string recipientId,
        Guid contactPointId,
        string justification)
        => client.PostAsJsonAsync(
            $"/v1/recipients/{recipientId}/suppressions/{contactPointId}/removal",
            new { justification });

    private async Task SuppressAsync(string recipientId, Guid contactPointId, string channel)
    {
        Result<SuppressionOutcome> reported = await fixture.UsingScopeAsync(services =>
            services.GetRequiredService<ISuppressionLedger>().ReportDeliveryFeedbackAsync(
                new SuppressionReport(
                    recipientId,
                    contactPointId,
                    channel,
                    "hard-bounce",
                    Guid.CreateVersion7(),
                    DateTimeOffset.UtcNow),
                CancellationToken.None));
        reported.Value.ShouldBe(SuppressionOutcome.ContactSuppressed);
    }

    private async Task<RecipientSnapshot> SnapshotAsync(string recipientId)
    {
        Result<RecipientSnapshot> snapshot = await fixture.UsingScopeAsync(services =>
            services.GetRequiredService<IRecipientDirectory>()
                .FindAsync(recipientId, CancellationToken.None));
        snapshot.IsSuccess.ShouldBeTrue();
        return snapshot.Value!;
    }

    private async Task<Guid> SeedContactPointAsync(string recipientId, string channel)
        => await fixture.UsingScopeAsync(async services =>
        {
            Api.Modules.ContactConsent.Infrastructure.Persistence.ContactConsentDbContext db =
                services.GetRequiredService<
                    Api.Modules.ContactConsent.Infrastructure.Persistence.ContactConsentDbContext>();
            db.RecipientProfiles.Add(
                RecipientProfile.Create(recipientId, null, null, DateTimeOffset.UtcNow));
            var point = ContactPoint.Declare(
                recipientId,
                channel,
                RandomNumberGenerator.GetBytes(16),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                verified: true);
            db.ContactPoints.Add(point);
            await db.SaveChangesAsync();
            return point.Id;
        });
}
