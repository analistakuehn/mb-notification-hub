using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// The manifest of attachment references over the wire. It is an optional
/// member of the one request contract: naming it asks for the files it names,
/// omitting it is the request every producer already sends, and naming it
/// without a usable reference is refused before the request becomes a
/// notification.
/// </summary>
[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class AttachmentContractIngressTests(NotificationsApiFixture fixture)
{
    private const string IngestionRoute = "/v1/notifications";

    [RequiresDockerFact]
    public async Task A_request_that_names_a_manifest_is_accepted_on_the_ingestion_route()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-manifest", NotificationsApi.SendTransactional);
        var idempotencyKey = $"manifest-{Guid.NewGuid():N}";

        // The references are attachments this application holds and had
        // released. A manifest is claimed on the way in, so text that names no
        // attachment is no longer a request the route accepts.
        SeededAttachment first = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);
        SeededAttachment second = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);

        HttpResponseMessage response = await PostAsync(
            producer,
            IngestionRoute,
            Body(templateKey, attachments: [first.Reference, second.Reference]),
            idempotencyKey);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement body = await NotificationsApi.ReadJsonAsync(response);
        var publicId = body.GetProperty("notificationId").GetString()!;
        body.GetProperty("status").GetString().ShouldBe("accepted");

        // The accepted notification is read where every notification is read.
        response.Headers.Location!.ToString().ShouldBe($"/v1/notifications/{publicId}");
        (await RegisteredAsync(idempotencyKey)).ShouldBeTrue();
    }

    /// <summary>
    /// The other half of the rule above: the member is optional, so a request
    /// that never names it meets no rule the manifest brought with it.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_request_that_names_no_manifest_is_accepted_unchanged()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-no-manifest", NotificationsApi.SendTransactional);

        HttpResponseMessage response = await PostAsync(
            producer, IngestionRoute, Body(templateKey), $"no-manifest-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    /// <summary>
    /// The contract still carries a member it does not name, so a producer
    /// that reads ahead of the hub is not refused for it.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_request_that_carries_an_unrelated_unknown_member_is_still_accepted()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-unknown-member", NotificationsApi.SendTransactional);
        Dictionary<string, object?> body = Body(templateKey);
        body["deliveryWindow"] = new { from = "08:00" };

        HttpResponseMessage response = await PostAsync(
            producer, IngestionRoute, body, $"unknown-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [RequiresDockerFact]
    public async Task A_manifest_that_names_no_reference_is_refused_before_the_notification_exists()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-empty-manifest", NotificationsApi.SendTransactional);
        var idempotencyKey = $"empty-manifest-{Guid.NewGuid():N}";

        HttpResponseMessage response = await PostAsync(
            producer, IngestionRoute, Body(templateKey, attachments: []), idempotencyKey);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("payload-invalid");
        problem.GetProperty("errors").GetProperty("Attachments")[0].GetString()
            .ShouldBe("Attachments must name at least one attachment reference.");
        (await RegisteredAsync(idempotencyKey)).ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task A_manifest_that_repeats_a_reference_is_refused_before_the_notification_exists()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-repeated-reference", NotificationsApi.SendTransactional);
        var idempotencyKey = $"repeated-reference-{Guid.NewGuid():N}";

        HttpResponseMessage response = await PostAsync(
            producer,
            IngestionRoute,
            Body(templateKey, attachments: ["att_alpha", "att_alpha"]),
            idempotencyKey);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("errors").GetProperty("Attachments")[0].GetString()
            .ShouldBe("Attachments must not repeat the same attachment reference.");
        (await RegisteredAsync(idempotencyKey)).ShouldBeFalse();
    }

    /// <summary>
    /// The request that carries a manifest is behind the same door as every
    /// other request for a notification, because it is the same request.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_anonymous_caller_never_reaches_the_route_that_carries_a_manifest()
    {
        HttpClient anonymous = fixture.CreateClient();

        HttpResponseMessage response = await PostAsync(
            anonymous,
            IngestionRoute,
            Body(TemplateApi.NewKey("ntf"), attachments: ["att_alpha"]),
            $"anonymous-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static Dictionary<string, object?> Body(
        string templateKey,
        IReadOnlyList<string>? attachments = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["application"] = NotificationsApi.Application,
            ["recipientId"] = $"cus_{Guid.NewGuid():N}",
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
        string route,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private async Task<bool> RegisteredAsync(string idempotencyKey)
        => await fixture.QueryNotificationsDbAsync(db => db.IdempotencyRegistrations
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Application == NotificationsApi.Application
                && candidate.IdempotencyKey == idempotencyKey));
}
