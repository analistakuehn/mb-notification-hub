using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// Request builders of the query surface: accepting notifications with an
/// explicit correlation, and reading the routes back with their raw body, so a
/// test can assert on what actually crossed the wire and not on a deserialized
/// shape that hides an omitted member.
/// </summary>
internal static class NotificationQueryApi
{
    internal const string ReadRole = "Notifications.Read";

    internal const string ProducerSubject = "query-producer";

    internal sealed record Accepted(Guid Id, string PublicId, string? CorrelationId);

    /// <summary>
    /// Accepts one notification over the pipeline fixture, whose templates
    /// declare a required <c>code</c> variable.
    /// </summary>
    internal static async Task<Accepted> AcceptAsync(
        CorePipelineFixture fixture,
        string application,
        string templateKey,
        string @class,
        string recipientId,
        string? correlationId = null,
        int ttlSeconds = 300)
        => await AcceptAsync(
            fixture.CreateProducerClient(ProducerSubject, RoleFor(@class)),
            application,
            templateKey,
            @class,
            recipientId,
            new { code = "123456" },
            correlationId,
            ttlSeconds);

    /// <summary>
    /// Accepts one notification over the ingestion-only fixture, whose
    /// templates declare a required <c>orderId</c> variable. The query tests
    /// that need rows and not a pipeline run here on purpose: this fixture has
    /// no queues, so an accepted notification leaves an outbox row that no
    /// other test ever relays into a shared queue.
    /// </summary>
    internal static async Task<Accepted> AcceptAsync(
        NotificationsApiFixture fixture,
        string templateKey,
        string @class,
        string recipientId,
        string? correlationId = null,
        int ttlSeconds = 300)
        => await AcceptAsync(
            fixture.CreateProducerClient(ProducerSubject, RoleFor(@class)),
            NotificationsApi.Application,
            templateKey,
            @class,
            recipientId,
            new { orderId = "ord-1" },
            correlationId,
            ttlSeconds);

    private static string RoleFor(string @class) => @class switch
    {
        "critical" => NotificationsApi.SendCritical,
        "operational" => NotificationsApi.SendOperational,
        _ => NotificationsApi.SendTransactional,
    };

    private static async Task<Accepted> AcceptAsync(
        HttpClient producer,
        string application,
        string templateKey,
        string @class,
        string recipientId,
        object variables,
        string? correlationId,
        int ttlSeconds)
    {
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            new
            {
                application,
                recipientId,
                @class,
                templateKey,
                locale = "pt-BR",
                variables,
                ttlSeconds,
                correlationId,
            },
            Guid.NewGuid().ToString("N"));
        accepted.EnsureSuccessStatusCode();

        JsonElement body = await NotificationsApi.ReadJsonAsync(accepted);
        var publicId = body.GetProperty("notificationId").GetString()!;
        NotificationId.TryParse(publicId, out Guid id).ShouldBeTrue();
        return new Accepted(id, publicId, correlationId);
    }

    /// <summary>Reads a route and returns status, parsed body and the raw text of the body.</summary>
    internal static async Task<(int Status, JsonElement Body, string Raw)> ReadAsync(
        HttpClient client,
        string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        var raw = await response.Content.ReadAsStringAsync();
        JsonElement body = raw.Length == 0
            ? default
            : JsonSerializer.Deserialize<JsonElement>(raw);
        return ((int)response.StatusCode, body, raw);
    }

    internal static string[] ItemIds(JsonElement page)
        => [.. page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)];

    internal static string? NextCursor(JsonElement page)
        => page.TryGetProperty("nextCursor", out JsonElement cursor) ? cursor.GetString() : null;

    internal static bool HasMember(JsonElement element, string name)
        => element.TryGetProperty(name, out _);
}
