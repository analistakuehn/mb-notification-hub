using System.Net.Http.Json;
using System.Text.Json;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>Request builders and template seeding shared by the ingestion tests.</summary>
internal static class NotificationsApi
{
    internal const string Application = "araia-cambio";
    internal const string SendCritical = "Notifications.Send.Critical";
    internal const string SendTransactional = "Notifications.Send.Transactional";
    internal const string SendOperational = "Notifications.Send.Operational";

    private static readonly string[] RequiredOrderId = ["orderId"];

    /// <summary>
    /// One request body. A null <paramref name="locale"/> and a null
    /// <paramref name="metadata"/> omit the member entirely instead of sending
    /// a JSON null, because the two are different requests on the wire and the
    /// optional field has to be provably absent.
    /// </summary>
    internal static object RequestBody(
        string templateKey,
        string @class = "transactional",
        string recipientId = "cus_01J5X9",
        object? variables = null,
        int ttlSeconds = 300,
        string? correlationId = null,
        string? locale = "pt-BR",
        object? metadata = null)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["application"] = Application,
            ["recipientId"] = recipientId,
            ["class"] = @class,
            ["templateKey"] = templateKey,
            ["variables"] = variables ?? new { orderId = "ord-1" },
            ["ttlSeconds"] = ttlSeconds,
            ["correlationId"] = correlationId,
        };
        if (locale is not null)
        {
            body["locale"] = locale;
        }

        if (metadata is not null)
        {
            body["metadata"] = metadata;
        }

        return body;
    }

    internal static async Task<HttpResponseMessage> PostNotificationAsync(
        HttpClient client,
        object body,
        string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/notifications")
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Publishes a template whose schema declares a required <c>orderId</c>
    /// plus an optional <c>code</c>, so the ingestion tests can exercise both
    /// plain and sensitive variables against a governed catalog entry.
    /// </summary>
    internal static async Task<(string Key, int Version)> CreatePublishedTemplateAsync(
        NotificationsApiFixture fixture,
        string @class = "transactional",
        string purpose = "order-updates",
        string[]? sensitiveVariables = null)
    {
        HttpClient author = fixture.CreateAuthorClient("template-author");
        HttpClient publisher = fixture.CreatePublisherClient("template-publisher");
        var key = TemplateApi.NewKey("ntf");

        HttpResponseMessage created = await author.PostAsJsonAsync("/v1/templates", new
        {
            key,
            application = Application,
            @class,
            ownerTeam = "growth-squad",
            purpose,
            legalBasis = "execucao-de-contrato",
            defaultLocale = "pt-BR",
            sensitiveVariables,
        });
        created.EnsureSuccessStatusCode();

        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} atualizado.</p>",
            bodyText = "Pedido {{ orderId }} atualizado.",
        }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new
            {
                orderId = new { type = "string" },
                code = new { type = "string" },
            },
            required = RequiredOrderId,
        }, etag);
        await TemplateApi.PublishAsync(publisher, key, version);
        return (key, version);
    }

    internal static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();
}
