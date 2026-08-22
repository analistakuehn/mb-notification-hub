using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>Small request builders shared by the layout authoring tests.</summary>
internal static class LayoutApi
{
    internal static string NewKey(string prefix = "lay")
        => $"{prefix}.{Guid.NewGuid():N}";

    internal static async Task<string> CreateLayoutAsync(
        HttpClient client,
        string key,
        string? defaultLocale = null)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/layouts",
            new { key, ownerTeam = "design-system", defaultLocale });
        response.EnsureSuccessStatusCode();
        return key;
    }

    internal static async Task<(int Version, string ETag)> CreateDraftAsync(HttpClient client, string key)
    {
        HttpResponseMessage response = await client.PostAsync($"/v1/layouts/{key}/versions", content: null);
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
        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(
            $"/v1/layouts/{key}/versions/{version}/content/{channelLocalePath}", content, etag));
        response.EnsureSuccessStatusCode();
        return response.Headers.ETag!.ToString();
    }

    /// <summary>
    /// Creates a layout plus a draft that passes the layout validation: an
    /// email wrapper for pt-BR whose body and text variant read the content
    /// placeholder.
    /// </summary>
    internal static async Task<(string Key, int Version)> CreatePublishableDraftAsync(
        HttpClient client,
        string body = "<html><header>MB</header>{{ content }}<footer>rodapé</footer></html>",
        string? bodyText = "MB\n{{ content }}\nrodapé")
    {
        var key = await CreateLayoutAsync(client, NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await CreateDraftAsync(client, key);
        await PutContentAsync(client, key, version, "email/pt-BR", new { body, bodyText }, etag);
        return (key, version);
    }

    internal static async Task<int> PublishAsync(HttpClient publisherClient, string key, int version)
    {
        HttpResponseMessage response = await publisherClient.PostAsync(
            $"/v1/layouts/{key}/versions/{version}/publish", content: null);
        response.EnsureSuccessStatusCode();
        return version;
    }

    /// <summary>Layout already published by a second principal, ready to be pinned by a template.</summary>
    internal static async Task<(string Key, int Version)> CreatePublishedLayoutAsync(
        HttpClient authorClient,
        HttpClient publisherClient)
    {
        (var key, var version) = await CreatePublishableDraftAsync(authorClient);
        await PublishAsync(publisherClient, key, version);
        return (key, version);
    }
}
