using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Notifications.DeliveryTracking;

/// <summary>
/// What a refused destination costs the recipient, from the provider callback
/// to the contact ledger. The chain is exercised whole on purpose: the signal
/// was already being classified at ingestion and thrown away before it could
/// reach anybody, so a test that started at the ledger would have passed over
/// the very gap this closes.
/// </summary>
[Collection(DeliveryTrackingCollectionDefinition.Name)]
public sealed class SuppressionLedgerTests(DeliveryTrackingFixture fixture)
{
    private const string ContactSuppressedType = "araia.notification.contact_suppressed.v1";
    private const string HardBounce = "hard-bounce";

    [RequiresDockerFact]
    public async Task A_definitive_email_bounce_suppresses_the_contact_on_the_first_occurrence()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, NewMessageId());
        var recipientId = await RecipientAsync(seeded.NotificationId);
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Email);
        await StampContactPointAsync(seeded.AttemptId, contactPointId);
        var eventId = NewEventId();

        await CallbackAsync(DeliveryEventBatch.Bounce(eventId, NewMessageId(), seeded, "bounce"));
        await ApplyAsync();

        // The evidence carries the classification the provider adapters made.
        // Without the column the consumer rebuilt a neutral signal and no
        // bounce ever reached the ledger.
        EvidenceRow evidence = await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId);
        (await SuppressionSignalOfAsync(evidence.Id)).ShouldBe(HardBounce);

        SuppressionRow suppression = await SingleSuppressionAsync(contactPointId);
        suppression.Channel.ShouldBe(ContactChannels.Email);
        suppression.Reason.ShouldBe(HardBounce);
        suppression.Source.ShouldBe(SuppressionSources.ProviderFeedback);
        suppression.ActorId.ShouldBe("delivery-tracker");
        suppression.RemovedAt.ShouldBeNull();

        (await CountSignalsAsync(contactPointId)).ShouldBe(1);
        (await CountTrailAsync(contactPointId, "suppression.added")).ShouldBe(1);

        // Announced once, and with what a consuming domain can act on.
        List<string> announced = await BusEventPayloadsAsync(recipientId, ContactSuppressedType);
        announced.Count.ShouldBe(
            1,
            "a supressão é anunciada uma vez por decisão, nunca uma vez por sinal relatado.");
        using JsonDocument data = JsonDocument.Parse(announced[0]);
        JsonElement payload = data.RootElement.GetProperty("data");
        payload.GetProperty("recipientId").GetString().ShouldBe(recipientId);
        payload.GetProperty("channel").GetString().ShouldBe(ContactChannels.Email);
        payload.GetProperty("reason").GetString().ShouldBe(HardBounce);

        // And the snapshot the pipeline reads now says the channel is closed.
        RecipientSnapshot snapshot = await SnapshotAsync(recipientId);
        snapshot.Suppressions.ShouldHaveSingleItem().ContactPointId.ShouldBe(contactPointId);
    }

    [RequiresDockerFact]
    public async Task A_repeated_report_of_the_same_source_event_leaves_the_ledger_untouched_and_a_trail_behind()
    {
        var recipientId = NewRecipientId();
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Email);
        var sourceEventId = Guid.CreateVersion7();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;

        Result<SuppressionOutcome> first = await ReportAsync(
            recipientId, contactPointId, ContactChannels.Email, sourceEventId, observedAt);
        Result<SuppressionOutcome> repeated = await ReportAsync(
            recipientId, contactPointId, ContactChannels.Email, sourceEventId, observedAt);

        first.Value.ShouldBe(SuppressionOutcome.ContactSuppressed);
        repeated.Value.ShouldBe(SuppressionOutcome.AlreadyApplied);

        (await CountSignalsAsync(contactPointId)).ShouldBe(
            1,
            "a reentrega da mensagem interna carrega o mesmo evento de origem e não pode "
            + "contar uma segunda recusa que nunca aconteceu.");
        (await CountSuppressionsAsync(contactPointId)).ShouldBe(1);
        (await CountTrailAsync(contactPointId, "suppression.added")).ShouldBe(1);
        (await CountTrailAsync(contactPointId, "suppression.signal.recorded")).ShouldBe(
            1,
            "o no-op é declarativo, não silencioso: ele grava a própria trilha.");
    }

    /// <summary>
    /// The guard against a doubled refusal is the unique key, not the check
    /// that runs before the insert: concurrent redeliveries both read absent
    /// and both would count. The channel here is the one whose threshold is
    /// two, so a duplicated signal is a suppression the recipient did not earn.
    /// </summary>
    [RequiresDockerFact]
    public async Task Concurrent_redeliveries_of_one_source_event_record_a_single_refusal()
    {
        var recipientId = NewRecipientId();
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Sms);
        var sourceEventId = Guid.CreateVersion7();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;

        using ServiceProvider tracker = fixture.BuildDeliveryTrackerProvider();
        Task<Result<SuppressionOutcome>>[] racing =
        [
            .. Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                using IServiceScope scope = tracker.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ISuppressionLedger>()
                    .ReportDeliveryFeedbackAsync(
                        new SuppressionReport(
                            recipientId,
                            contactPointId,
                            ContactChannels.Sms,
                            HardBounce,
                            sourceEventId,
                            observedAt),
                        CancellationToken.None);
            })),
        ];
        await Task.WhenAll(racing);

        (await CountSignalsAsync(contactPointId)).ShouldBe(
            1,
            "o mesmo evento de origem não pode contar duas recusas: com duas, o canal "
            + "atinge um limite de duas ocorrências que o destinatário nunca sofreu.");
        (await CountSuppressionsAsync(contactPointId)).ShouldBe(
            0,
            "uma única recusa não fecha um canal cujo limite é de duas ocorrências.");
    }

    [RequiresDockerFact]
    public async Task An_sms_refusal_suppresses_only_on_the_second_occurrence_inside_the_week()
    {
        var recipientId = NewRecipientId();
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Sms);
        DateTimeOffset first = DateTimeOffset.UtcNow.AddDays(-2);

        Result<SuppressionOutcome> one = await ReportAsync(
            recipientId, contactPointId, ContactChannels.Sms, Guid.CreateVersion7(), first);
        one.Value.ShouldBe(
            SuppressionOutcome.SignalRecorded,
            "um número pode ser recusado por condição temporária; a primeira recusa "
            + "é registrada e não fecha o canal.");
        (await CountSuppressionsAsync(contactPointId)).ShouldBe(0);

        Result<SuppressionOutcome> two = await ReportAsync(
            recipientId, contactPointId, ContactChannels.Sms, Guid.CreateVersion7(), first.AddDays(1));

        two.Value.ShouldBe(SuppressionOutcome.ContactSuppressed);
        (await SingleSuppressionAsync(contactPointId)).Channel.ShouldBe(ContactChannels.Sms);
        (await CountSignalsAsync(contactPointId)).ShouldBe(2);
    }

    [RequiresDockerFact]
    public async Task A_second_sms_refusal_outside_the_week_leaves_the_channel_open()
    {
        var recipientId = NewRecipientId();
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Sms);
        DateTimeOffset first = DateTimeOffset.UtcNow.AddDays(-30);

        await ReportAsync(
            recipientId, contactPointId, ContactChannels.Sms, Guid.CreateVersion7(), first);
        Result<SuppressionOutcome> two = await ReportAsync(
            recipientId, contactPointId, ContactChannels.Sms, Guid.CreateVersion7(), first.AddDays(8));

        two.Value.ShouldBe(
            SuppressionOutcome.SignalRecorded,
            "a janela é contada a partir da recusa mais nova, então uma recusa isolada "
            + "envelhece em vez de esperar indefinidamente por uma parceira.");
        (await CountSuppressionsAsync(contactPointId)).ShouldBe(0);
        (await CountSignalsAsync(contactPointId)).ShouldBe(2);
    }

    [RequiresDockerFact]
    public async Task A_report_for_a_channel_the_contact_point_does_not_serve_is_refused()
    {
        var recipientId = NewRecipientId();
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Sms);

        Result<SuppressionOutcome> reported = await ReportAsync(
            recipientId, contactPointId, ContactChannels.Email, Guid.CreateVersion7(), DateTimeOffset.UtcNow);

        // The e-mail rule closes the channel on the first refusal and the SMS
        // rule does not: a mismatched report would settle one channel under
        // the rule of another.
        reported.IsFailure.ShouldBeTrue();
        (await CountSignalsAsync(contactPointId)).ShouldBe(0);
        (await CountSuppressionsAsync(contactPointId)).ShouldBe(0);
    }

    /// <summary>
    /// The forgery path stops at authentication and never reaches the ledger.
    /// The gap it guards is real: a caller able to post a bounce for someone
    /// else's address would be able to cut that person off from the hub.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_bounce_from_an_address_outside_the_allowlist_reaches_neither_evidence_nor_ledger()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, NewMessageId());
        var recipientId = await RecipientAsync(seeded.NotificationId);
        Guid contactPointId = await SeedContactPointAsync(recipientId, ContactChannels.Email);
        await StampContactPointAsync(seeded.AttemptId, contactPointId);
        var eventId = NewEventId();
        var capturing = new CapturingLoggerProvider();

        using WebApplicationFactory<Program> pinned = fixture.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(capturing));
            builder.UseSetting("Modules:Dispatch:Webhooks:SendGrid:AllowedIpPrefixes:0", "198.51.100.");
        });

        HttpResponseMessage response = await pinned.CreateClient().SendAsync(
            DeliveryTrackingApi.SendGridCallback(
                fixture, DeliveryEventBatch.Bounce(eventId, NewMessageId(), seeded, "bounce")));
        await ApplyAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DeliveryTrackingApi.CountEvidenceAsync(fixture, eventId)).ShouldBe(0);
        (await CountSignalsAsync(contactPointId)).ShouldBe(
            0,
            "um evento recusado por origem não pode produzir efeito algum no ledger de contatos.");
        (await CountSuppressionsAsync(contactPointId)).ShouldBe(0);
        capturing.Lines.ShouldContain(
            line => line.Contains("forjação", StringComparison.Ordinal),
            "origem fora da allowlist precisa gerar alarme de segurança próprio.");
    }

    private async Task<Result<SuppressionOutcome>> ReportAsync(
        string recipientId,
        Guid contactPointId,
        string channel,
        Guid sourceEventId,
        DateTimeOffset observedAt)
    {
        // Through the role's own composition: the contract the delivery
        // tracker holds is the contract under test.
        using ServiceProvider tracker = fixture.BuildDeliveryTrackerProvider();
        using IServiceScope scope = tracker.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISuppressionLedger>()
            .ReportDeliveryFeedbackAsync(
                new SuppressionReport(
                    recipientId, contactPointId, channel, HardBounce, sourceEventId, observedAt),
                CancellationToken.None);
    }

    private async Task<RecipientSnapshot> SnapshotAsync(string recipientId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        Result<RecipientSnapshot> snapshot = await scope.ServiceProvider
            .GetRequiredService<IRecipientDirectory>()
            .FindAsync(recipientId, CancellationToken.None);
        snapshot.IsSuccess.ShouldBeTrue();
        return snapshot.Value!;
    }

    private async Task<Guid> SeedContactPointAsync(string recipientId, string channel)
    {
        Guid contactPointId = Guid.Empty;
        await fixture.ExecuteContactConsentDbAsync(async db =>
        {
            if (!await db.RecipientProfiles.AnyAsync(profile => profile.RecipientId == recipientId))
            {
                db.RecipientProfiles.Add(
                    RecipientProfile.Create(recipientId, null, null, DateTimeOffset.UtcNow));
            }

            ContactPoint point = ContactPoint.Declare(
                recipientId,
                channel,
                RandomNumberGenerator.GetBytes(16),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                verified: true);
            db.ContactPoints.Add(point);
            await db.SaveChangesAsync();
            contactPointId = point.Id;
        });
        return contactPointId;
    }

    private async Task StampContactPointAsync(Guid attemptId, Guid contactPointId)
        => await fixture.ExecuteNotificationsDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""
            UPDATE notifications.notification_attempt
            SET contact_point_id = {contactPointId}
            WHERE id = {attemptId}
            """));

    private async Task CallbackAsync(string body)
    {
        HttpResponseMessage response = await fixture.CreateClient()
            .SendAsync(DeliveryTrackingApi.SendGridCallback(fixture, body));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    /// <summary>Moves the callback through the relay and the tracker, as the two deployed roles would.</summary>
    private async Task ApplyAsync(int passes = 3)
    {
        using ServiceProvider relay = fixture.BuildRelayProvider();
        using ServiceProvider tracker = fixture.BuildDeliveryTrackerProvider();
        for (var pass = 0; pass < passes; pass++)
        {
            await DeliveryTrackingFixture.RunRelayPassAsync(relay);
            await DeliveryTrackingFixture.RunTrackerPassAsync(tracker);
        }
    }

    private async Task<string> SuppressionSignalOfAsync(Guid deliveryEventId)
        => await fixture.QueryNotificationsDbAsync(db => db.DeliveryEvents
            .AsNoTracking()
            .Where(evidence => evidence.Id == deliveryEventId)
            .Select(evidence => evidence.SuppressionSignal)
            .SingleAsync());

    private async Task<SuppressionRow> SingleSuppressionAsync(Guid contactPointId)
        => await fixture.QueryContactConsentDbAsync(db => db.Suppressions
            .AsNoTracking()
            .Where(suppression => suppression.ContactPointId == contactPointId)
            .Select(suppression => new SuppressionRow(
                suppression.Channel,
                suppression.Reason,
                suppression.Source,
                suppression.ActorId,
                suppression.RemovedAt))
            .SingleAsync());

    private async Task<int> CountSignalsAsync(Guid contactPointId)
        => await fixture.QueryContactConsentDbAsync(db => db.SuppressionSignals
            .AsNoTracking()
            .CountAsync(signal => signal.ContactPointId == contactPointId));

    private async Task<int> CountSuppressionsAsync(Guid contactPointId)
        => await fixture.QueryContactConsentDbAsync(db => db.Suppressions
            .AsNoTracking()
            .CountAsync(suppression => suppression.ContactPointId == contactPointId
                && suppression.RemovedAt == null));

    private async Task<int> CountTrailAsync(Guid contactPointId, string action)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == action
                && entry.EntityId == contactPointId.ToString()));

    private async Task<List<string>> BusEventPayloadsAsync(string recipientId, string eventType)
        => await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.EventType == eventType && message.MessageKey == recipientId)
            .Select(message => message.PayloadJson)
            .ToListAsync());

    private async Task<string> RecipientAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.RecipientId)
            .SingleAsync());

    private static string NewRecipientId() => $"rcp-{Guid.NewGuid():N}";

    private static string NewEventId() => $"evt-{Guid.NewGuid():N}";

    private static string NewMessageId() => $"msg-{Guid.NewGuid():N}";

    private sealed record SuppressionRow(
        string Channel,
        string Reason,
        string Source,
        string ActorId,
        DateTimeOffset? RemovedAt);
}
