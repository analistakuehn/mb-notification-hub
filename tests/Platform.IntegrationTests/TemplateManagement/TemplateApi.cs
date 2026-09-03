using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>Small request builders shared by the template authoring tests.</summary>
internal static class TemplateApi
{
    internal static string NewKey(string prefix = "it")
        => $"{prefix}.{Guid.NewGuid():N}";

    internal static object TemplateBody(
        string key,
        string application = "araia-cambio",
        string @class = "transactional",
        string ownerTeam = "growth-squad",
        string? defaultLocale = null,
        string[]? linkDomainsAllowed = null)
        => new
        {
            key,
            application,
            @class,
            ownerTeam,
            purpose = "order-updates",
            legalBasis = "execucao-de-contrato",
            defaultLocale,
            linkDomainsAllowed,
        };

    internal static async Task<string> CreateTemplateAsync(
        HttpClient client,
        string key,
        string? defaultLocale = null,
        string[]? linkDomainsAllowed = null)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/templates",
            TemplateBody(
                key,
                defaultLocale: defaultLocale,
                linkDomainsAllowed: linkDomainsAllowed));
        response.EnsureSuccessStatusCode();
        return key;
    }

    internal static async Task<(int Version, string ETag)> CreateDraftAsync(HttpClient client, string key)
    {
        HttpResponseMessage response = await client.PostAsync($"/v1/templates/{key}/versions", content: null);
        response.EnsureSuccessStatusCode();
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("version").GetInt32(), response.Headers.ETag!.ToString());
    }

    internal static async Task<string> PutContentAsync(
        HttpClient client,
        string key,
        int version,
        string channelLocalePath,
        object content,
        string etag)
    {
        HttpResponseMessage response = await client.SendAsync(PutJson(
            $"/v1/templates/{key}/versions/{version}/content/{channelLocalePath}", content, etag));
        response.EnsureSuccessStatusCode();
        return response.Headers.ETag!.ToString();
    }

    /// <summary>
    /// Declares which variables of the draft carry sensitive data. The
    /// declaration belongs to the version, so it travels through the draft
    /// like the content and the schema and reaches publication under the same
    /// four eyes.
    /// </summary>
    internal static async Task<string> PutSensitiveVariablesAsync(
        HttpClient client,
        string key,
        int version,
        string[] sensitiveVariables,
        string etag)
    {
        HttpResponseMessage response = await client.SendAsync(PutJson(
            $"/v1/templates/{key}/versions/{version}/sensitive-variables",
            new { sensitiveVariables },
            etag));
        response.EnsureSuccessStatusCode();
        return response.Headers.ETag!.ToString();
    }

    internal static async Task<string> PutSchemaAsync(
        HttpClient client,
        string key,
        int version,
        object schema,
        string etag)
    {
        HttpResponseMessage response = await client.SendAsync(PutJson(
            $"/v1/templates/{key}/versions/{version}/variables-schema", schema, etag));
        response.EnsureSuccessStatusCode();
        return response.Headers.ETag!.ToString();
    }

    private static readonly string[] RequiredOrderId = ["orderId"];

    /// <summary>
    /// Creates a template plus a draft that passes the integral validation:
    /// email content for the default locale, plain-text version, and every
    /// used variable declared in the schema.
    /// </summary>
    internal static async Task<(string Key, int Version)> CreatePublishableDraftAsync(HttpClient client)
    {
        var key = await CreateTemplateAsync(client, NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await CreateDraftAsync(client, key);
        etag = await PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} atualizado.</p>",
            bodyText = "Pedido {{ orderId }} atualizado.",
        }, etag);
        await PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { orderId = new { type = "string" } },
            required = RequiredOrderId,
        }, etag);
        return (key, version);
    }

    internal static async Task<int> PublishAsync(HttpClient publisherClient, string key, int version)
    {
        HttpResponseMessage response = await publisherClient.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);
        if (!response.IsSuccessStatusCode)
        {
            // A recusa de publicação carrega o relatório de verificações no
            // corpo. Sem ele, a falha chega ao autor do teste como um código
            // de status e obriga uma investigação que a resposta já respondeu.
            var report = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"A publicação de {key} v{version} foi recusada com {(int)response.StatusCode}: {report}");
        }

        return version;
    }

    internal static HttpRequestMessage PutJson(string url, object body, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    internal static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();
}
