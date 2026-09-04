using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

internal static class AttachmentApi
{
    internal const string Application = "billing-app";
    internal const string FileName = "invoice-private-name.pdf";
    internal const string ContentType = "application/pdf";

    /// <summary>
    /// The ceiling a seeded registration is handed when size is not what the
    /// oracle reads. The aggregate takes a ceiling instead of holding one, so a
    /// seed that restated the approved capacity would tie itself to a number it
    /// never measures.
    /// </summary>
    internal const long SeedSizeCeiling = 1_048_576;

    internal static object Registration(
        long sizeBytes,
        string application = Application,
        string fileName = FileName,
        string contentType = ContentType)
        => new { application, fileName, contentType, sizeBytes };

    internal static async Task<(HttpResponseMessage Response, ApiResponse Body)> RegisterAsync(
        HttpClient client,
        long sizeBytes,
        string contentType = ContentType)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/attachments",
            Registration(sizeBytes, contentType: contentType));
        ApiResponse body = await ReadMinimalResponseAsync(response);
        return (response, body);
    }

    internal static async Task<HttpResponseMessage> PutContentAsync(
        HttpClient client,
        string reference,
        string content)
    {
        var body = new StreamContent(new MemoryStream(
            Encoding.UTF8.GetBytes(content),
            writable: false));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await client.PutAsync($"/v1/attachments/{reference}/content", body);
    }

    internal static async Task<ApiResponse> ReadMinimalResponseAsync(
        HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        root.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["reference", "state"]);
        return new ApiResponse(
            root.GetProperty("reference").GetString().ShouldNotBeNull(),
            root.GetProperty("state").GetString().ShouldNotBeNull(),
            body);
    }

    internal static IEnumerable<string> ResponseFragments(
        HttpResponseMessage response,
        string body)
        =>
        [
            body,
            .. response.Headers.SelectMany(header => header.Value.Prepend(header.Key)),
            .. response.Content.Headers.SelectMany(header => header.Value.Prepend(header.Key)),
        ];

    internal static IEnumerable<string> LogFragments(SentinelCapturedLogEvent log)
        =>
        [
            log.Message,
            log.Exception ?? string.Empty,
            .. log.State.Select(value => value.Value),
            .. log.Scopes.SelectMany(scope =>
                scope.State.Select(value => value.Value).Prepend(scope.Formatted)),
        ];

    internal sealed record ApiResponse(string Reference, string State, string Body);
}
