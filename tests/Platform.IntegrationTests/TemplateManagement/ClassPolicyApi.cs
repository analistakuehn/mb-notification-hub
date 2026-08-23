using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>Small request builders shared by the class policy governance tests.</summary>
internal static class ClassPolicyApi
{
    internal const string DefaultClass = "transactional";

    internal static string NewApplication(string prefix = "app")
        => $"{prefix}-{Guid.NewGuid():N}";

    internal static string PolicyUrl(string application, string policyClass = DefaultClass)
        => $"/v1/applications/{application}/classes/{policyClass}/policy";

    /// <summary>A definition that passes the version 1 structural validation.</summary>
    internal static object Definition(
        string defaultTtl = "300s",
        string dedupeWindow = "60s",
        object? quietHours = null,
        string? consentPurpose = null)
        => new
        {
            schemaVersion = 1,
            channelsAllowed = new[] { "push", "sms" },
            deliveryPlan = new object[]
            {
                new { channel = "push", timeout = "30s" },
                new { channel = "sms" },
            },
            defaultTtl,
            dedupeWindow,
            quietHours,
            consentPurpose,
        };

    internal static async Task<(string Application, int Version, string ETag)> CreateDraftAsync(
        HttpClient client,
        string? application = null,
        string policyClass = DefaultClass,
        object? definition = null)
    {
        var app = application ?? NewApplication();
        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(
            $"{PolicyUrl(app, policyClass)}/draft", definition ?? Definition(), ifMatch: null));
        response.EnsureSuccessStatusCode();
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (app, body.GetProperty("version").GetInt32(), response.Headers.ETag!.ToString());
    }

    internal static async Task<int> PublishAsync(
        HttpClient publisherClient,
        string application,
        string policyClass = DefaultClass)
    {
        HttpResponseMessage response = await publisherClient.PostAsync(
            $"{PolicyUrl(application, policyClass)}/publish", content: null);
        response.EnsureSuccessStatusCode();
        return (await TemplateApi.ReadJsonAsync(response)).GetProperty("version").GetInt32();
    }
}
