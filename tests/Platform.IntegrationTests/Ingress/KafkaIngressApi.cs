using System.Net.Http.Json;
using System.Text.Json;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Ingress;

/// <summary>
/// Seeding of the governed catalog the ingress reads and building of the
/// CloudEvents bodies producers publish. Everything goes through the public
/// API with the real four-eyes flow, so the tests measure the same catalog
/// production reads.
/// </summary>
internal static class KafkaIngressApi
{
    private static readonly string[] RequiredCode = ["code"];

    internal static string NewApplication() => $"app-{Guid.NewGuid():N}";

    internal static string NewIdempotencyKey() => $"key-{Guid.NewGuid():N}";

    /// <summary>Publishes a template whose schema declares a required <c>code</c> variable.</summary>
    internal static async Task<(string Key, int Version)> CreatePublishedTemplateAsync(
        KafkaIngressFixture fixture,
        string application,
        string @class,
        string[]? sensitiveVariables = null)
    {
        HttpClient author = fixture.CreateAuthorClient("template-author");
        HttpClient publisher = fixture.CreatePublisherClient("template-publisher");
        var key = TemplateApi.NewKey("ingress");

        HttpResponseMessage created = await author.PostAsJsonAsync("/v1/templates", new
        {
            key,
            application,
            @class,
            ownerTeam = "growth-squad",
            purpose = "transacional",
            legalBasis = "execucao-de-contrato",
            defaultLocale = "pt-BR",
            sensitiveVariables,
        });
        created.EnsureSuccessStatusCode();

        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "push/pt-BR", new
        {
            subject = "Sua operação",
            body = "Operação confirmada com o código {{ code }}.",
        }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { code = new { type = "string" } },
            required = RequiredCode,
        }, etag);
        await TemplateApi.PublishAsync(publisher, key, version);
        return (key, version);
    }

    /// <summary>
    /// One well-formed CloudEvent carrying a notification request.
    /// <paramref name="options"/> groups the optional event variations so a
    /// test can hold the request identity fixed and move only one contract
    /// dimension.
    /// </summary>
    internal static string RequestedEvent(
        string application,
        string templateKey,
        string @class,
        string recipientId,
        string idempotencyKey,
        RequestedEventOptions? options = null)
    {
        RequestedEventOptions eventOptions = options ?? new RequestedEventOptions();
        return JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = $"evt-{Guid.NewGuid():N}",
            source = eventOptions.EventSource,
            type = eventOptions.EventType,
            time = DateTimeOffset.UtcNow,
            subject = recipientId,
            datacontenttype = "application/json",
            data = new
            {
                application,
                recipientId,
                idempotencyKey,
                @class,
                templateKey,
                locale = "pt-BR",
                variables = eventOptions.Variables,
                ttlSeconds = 300,
            },
        });
    }

    internal static Dictionary<string, string> ProducerHeaders(string producer)
        => new(StringComparer.Ordinal) { ["producer"] = producer };

    internal sealed record RequestedEventOptions
    {
        internal string EventType { get; init; } = "araia.notification.requested.v1";

        internal string EventSource { get; init; } = "urn:araia:integration-tests";

        internal object? Variables { get; init; } = new { code = "123456" };
    }
}
