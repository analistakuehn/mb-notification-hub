using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.TemplateManagement;
using StackExchange.Redis;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// The manifest inside the identity of a request. Two attempts under one
/// idempotency key are the same request only when they ask for the same
/// delivery, and the files a request names are part of what it asks for: a
/// producer that retries with another manifest is asking for something else
/// and has to be told so, instead of receiving the answer of the request it
/// replaced.
/// </summary>
[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class AttachmentManifestIdempotencyTests(NotificationsApiFixture fixture)
{
    /// <summary>
    /// The window this closes: a second attempt that adds files under the key
    /// of an attempt that had none. Answering it with the first attempt tells
    /// the producer that a delivery carrying the files already happened, and
    /// no delivery carrying them was ever created.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attempt_that_adds_a_manifest_is_not_a_repetition_of_the_one_without_it()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-manifest-conflict", NotificationsApi.SendTransactional);
        var idempotencyKey = $"manifest-added-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpResponseMessage first = await PostAsync(
            producer, Body(templateKey, recipientId), idempotencyKey);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Both authorities answer the same question: the cached one first, the
        // database once the cached entry is gone.
        HttpResponseMessage cached = await PostAsync(
            producer,
            Body(templateKey, recipientId, attachments: ["att_alpha"]),
            idempotencyKey);
        await RemoveFastPathEntryAsync(idempotencyKey);
        HttpResponseMessage stored = await PostAsync(
            producer,
            Body(templateKey, recipientId, attachments: ["att_alpha"]),
            idempotencyKey);

        cached.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        stored.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(stored);
        problem.GetProperty("type").GetString().ShouldBe("idempotency-key-conflict");

        // The refused attempts left nothing behind: the key still answers for
        // the one notification the first attempt created.
        (await NotificationsDbCountAsync(idempotencyKey)).ShouldBe(1);
    }

    /// <summary>
    /// The manifest is ordered, so the same files in another sequence are
    /// another delivery and another request under the same key.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attempt_that_reorders_the_manifest_is_not_a_repetition()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-manifest-order", NotificationsApi.SendTransactional);
        var idempotencyKey = $"manifest-reordered-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        SeededAttachment alpha = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);
        SeededAttachment beta = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);

        HttpResponseMessage first = await PostAsync(
            producer,
            Body(templateKey, recipientId, attachments: [alpha.Reference, beta.Reference]),
            idempotencyKey);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await RemoveFastPathEntryAsync(idempotencyKey);

        HttpResponseMessage reordered = await PostAsync(
            producer,
            Body(templateKey, recipientId, attachments: [beta.Reference, alpha.Reference]),
            idempotencyKey);

        reordered.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await NotificationsDbCountAsync(idempotencyKey)).ShouldBe(1);
    }

    /// <summary>
    /// The other half of the rule: a client library that serializes an
    /// optional list as a JSON null is retrying the request it already sent,
    /// and owes the producer the answer of that request. The attempt that adds
    /// files at the end is the falsification of the two repetitions above it:
    /// the comparison is running, and it answered repetition because the
    /// requests are the same one.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attempt_that_writes_the_manifest_as_null_repeats_the_one_that_omitted_it()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-manifest-null", NotificationsApi.SendTransactional);
        var idempotencyKey = $"manifest-null-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        Dictionary<string, object?> nullManifest = Body(templateKey, recipientId);
        nullManifest["attachments"] = null;

        HttpResponseMessage first = await PostAsync(
            producer, Body(templateKey, recipientId), idempotencyKey);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        HttpResponseMessage cached = await PostAsync(producer, nullManifest, idempotencyKey);
        await RemoveFastPathEntryAsync(idempotencyKey);
        HttpResponseMessage stored = await PostAsync(producer, nullManifest, idempotencyKey);
        HttpResponseMessage divergent = await PostAsync(
            producer,
            Body(templateKey, recipientId, attachments: ["att_alpha"]),
            idempotencyKey);

        cached.StatusCode.ShouldBe(HttpStatusCode.OK);
        stored.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstId = (await NotificationsApi.ReadJsonAsync(first))
            .GetProperty("notificationId").GetString();
        (await NotificationsApi.ReadJsonAsync(cached))
            .GetProperty("notificationId").GetString().ShouldBe(firstId);
        (await NotificationsApi.ReadJsonAsync(stored))
            .GetProperty("notificationId").GetString().ShouldBe(firstId);
        divergent.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await NotificationsDbCountAsync(idempotencyKey)).ShouldBe(1);
    }

    /// <summary>
    /// A body that never names the member, so a manifest written as a JSON
    /// null stays distinguishable from an omitted one on the wire.
    /// </summary>
    private static Dictionary<string, object?> Body(
        string templateKey,
        string recipientId,
        IReadOnlyList<string>? attachments = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["application"] = NotificationsApi.Application,
            ["recipientId"] = recipientId,
            ["class"] = "transactional",
            ["templateKey"] = templateKey,
            ["variables"] = new { orderId = "ord-1" },
            ["ttlSeconds"] = 300,
        };
        if (attachments is not null)
        {
            body["attachments"] = attachments;
        }

        return body;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/notifications")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private async Task<int> NotificationsDbCountAsync(string idempotencyKey)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey));

    private async Task RemoveFastPathEntryAsync(string idempotencyKey)
    {
        var options = ConfigurationOptions.Parse(fixture.RedisConnectionString);
        options.AbortOnConnectFail = false;
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(options);
        await connection.GetDatabase().KeyDeleteAsync(
            $"{NotificationsApiFixture.RedisKeyPrefix}idem:{NotificationsApi.Application}:{idempotencyKey}");
    }
}
