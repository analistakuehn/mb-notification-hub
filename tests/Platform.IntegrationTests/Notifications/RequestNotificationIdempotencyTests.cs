using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Mutations;
using NotificationHub.IntegrationTests.TemplateManagement;
using StackExchange.Redis;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class RequestNotificationIdempotencyTests(NotificationsApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_replay_with_the_same_body_answers_200_with_the_same_notification_id()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-replay", NotificationsApi.SendTransactional);
        var idempotencyKey = $"replay-{Guid.NewGuid():N}";
        var body = NotificationsApi.RequestBody(templateKey, recipientId: $"cus_{Guid.NewGuid():N}");

        HttpResponseMessage first = await NotificationsApi.PostNotificationAsync(producer, body, idempotencyKey);
        HttpResponseMessage replay = await NotificationsApi.PostNotificationAsync(producer, body, idempotencyKey);

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstId = (await NotificationsApi.ReadJsonAsync(first)).GetProperty("notificationId").GetString();
        var replayId = (await NotificationsApi.ReadJsonAsync(replay)).GetProperty("notificationId").GetString();
        replayId.ShouldBe(firstId);

        // A replay creates nothing: one notification, one registration.
        var notifications = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey));
        notifications.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_replay_beyond_the_fast_path_is_answered_by_the_database_authority()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-dbreplay", NotificationsApi.SendTransactional);
        var idempotencyKey = $"dbreplay-{Guid.NewGuid():N}";
        var body = NotificationsApi.RequestBody(templateKey, recipientId: $"cus_{Guid.NewGuid():N}");

        HttpResponseMessage first = await NotificationsApi.PostNotificationAsync(producer, body, idempotencyKey);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await RemoveFastPathEntryAsync(idempotencyKey);

        HttpResponseMessage replay = await NotificationsApi.PostNotificationAsync(producer, body, idempotencyKey);

        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstId = (await NotificationsApi.ReadJsonAsync(first)).GetProperty("notificationId").GetString();
        (await NotificationsApi.ReadJsonAsync(replay)).GetProperty("notificationId").GetString().ShouldBe(firstId);

        // The database round trip is audited as a duplicate.
        NotificationId.TryParse(firstId, out Guid storedId).ShouldBeTrue();
        var duplicates = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(candidate => candidate.Action == "notification.duplicate"
                && candidate.EntityId == storedId.ToString()));
        duplicates.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The locale reaches no decision of the hub, so two attempts that differ
    /// only in it are the same notification. A retry that corrected the field,
    /// or a client library that filled its default differently between the
    /// attempt and the retry, must replay and never conflict.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_retry_that_only_changes_the_locale_replays_instead_of_conflicting()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-locale", NotificationsApi.SendTransactional);
        var idempotencyKey = $"locale-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpResponseMessage first = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId, locale: "pt-BR"),
            idempotencyKey);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The fast path and the database authority must give the same answer,
        // because both compare the same canonical hash.
        HttpResponseMessage corrected = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId, locale: "pt-br"),
            idempotencyKey);
        await RemoveFastPathEntryAsync(idempotencyKey);
        HttpResponseMessage omitted = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId, locale: null),
            idempotencyKey);

        corrected.StatusCode.ShouldBe(HttpStatusCode.OK);
        omitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstId = (await NotificationsApi.ReadJsonAsync(first)).GetProperty("notificationId").GetString();
        (await NotificationsApi.ReadJsonAsync(corrected))
            .GetProperty("notificationId").GetString().ShouldBe(firstId);
        (await NotificationsApi.ReadJsonAsync(omitted))
            .GetProperty("notificationId").GetString().ShouldBe(firstId);

        // Falsification of the replay itself: a field the hub does decide on
        // still conflicts under the same key, so the two 200s above are the
        // locale leaving the hash and not the comparison having stopped.
        HttpResponseMessage divergent = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(
                templateKey, recipientId: recipientId, variables: new { orderId = "ord-2" }),
            idempotencyKey);
        divergent.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // One notification, whatever the locale of each attempt.
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey)))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task The_same_key_with_a_different_body_is_a_conflict()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-conflict", NotificationsApi.SendTransactional);
        var idempotencyKey = $"conflict-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpResponseMessage first = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId),
            idempotencyKey);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var divergent = NotificationsApi.RequestBody(
            templateKey, recipientId: recipientId, variables: new { orderId = "ord-2" });

        // The fast path answers the conflict first; the database authority
        // must give the same answer once the cached entry is gone.
        HttpResponseMessage cachedConflict = await NotificationsApi.PostNotificationAsync(
            producer, divergent, idempotencyKey);
        cachedConflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await RemoveFastPathEntryAsync(idempotencyKey);
        HttpResponseMessage databaseConflict = await NotificationsApi.PostNotificationAsync(
            producer, divergent, idempotencyKey);
        databaseConflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(databaseConflict);
        problem.GetProperty("type").GetString().ShouldBe("idempotency-key-conflict");
    }

    [RequiresDockerFact]
    public async Task A_lost_insert_race_resolves_to_the_registration_that_won()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-race", NotificationsApi.SendTransactional);
        var idempotencyKey = $"race-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var winnerId = Guid.CreateVersion7();

        // The winner's registration is already in the table and the fast path
        // knows nothing about it: the insert must hit the unique key, reread,
        // and answer with the winner's id.
        var winnersHash = RequestNotification.ComputePayloadHash(new RequestNotification.Command(
            Application: NotificationsApi.Application,
            RecipientId: recipientId,
            Class: "transactional",
            TemplateKey: templateKey,
            TtlSeconds: 300)
        {
            Locale = "pt-BR",
            Variables = JsonDocument.Parse("""{"orderId":"ord-1"}""").RootElement.Clone(),
        });
        await fixture.ExecuteNotificationsDbAsync(async db =>
        {
            db.IdempotencyRegistrations.Add(IdempotencyRegistration.Register(
                NotificationsApi.Application, idempotencyKey, winnersHash, winnerId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        HttpResponseMessage samePayload = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId),
            idempotencyKey);

        samePayload.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NotificationsApi.ReadJsonAsync(samePayload))
            .GetProperty("notificationId").GetString()
            .ShouldBe(NotificationId.Format(winnerId));

        // No orphan notification row survived the aborted transaction.
        var notifications = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey));
        notifications.ShouldBe(0);

        await RemoveFastPathEntryAsync(idempotencyKey);
        HttpResponseMessage differentPayload = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId, variables: new { orderId = "ord-9" }),
            idempotencyKey);
        differentPayload.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private async Task RemoveFastPathEntryAsync(string idempotencyKey)
    {
        ConfigurationOptions options = ConfigurationOptions.Parse(fixture.RedisConnectionString);
        options.AbortOnConnectFail = false;
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(options);
        await connection.GetDatabase().KeyDeleteAsync(
            $"{NotificationsApiFixture.RedisKeyPrefix}idem:{NotificationsApi.Application}:{idempotencyKey}");
    }
}
