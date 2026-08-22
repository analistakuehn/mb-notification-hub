using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>Small request builders shared by the template authoring tests.</summary>
internal static class TemplateApi
{
    internal static string NewKey(string prefix = "it")
        => $"{prefix}.{Guid.NewGuid():N}";

    internal static object TemplateBody(string key, string application = "araia-cambio", string @class = "transactional", string ownerTeam = "growth-squad")
        => new
        {
            key,
            application,
            @class,
            ownerTeam,
            purpose = "order-updates",
            legalBasis = "execucao-de-contrato",
        };

    internal static async Task<string> CreateTemplateAsync(HttpClient client, string key)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/templates", TemplateBody(key));
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
