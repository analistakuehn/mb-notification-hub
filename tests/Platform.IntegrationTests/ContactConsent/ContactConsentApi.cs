using System.Net.Http.Json;

namespace NotificationHub.IntegrationTests.ContactConsent;

/// <summary>Request builders shared by the contact and consent tests.</summary>
internal static class ContactConsentApi
{
    internal const string ContactsWrite = "Contacts.Write";

    internal static string NewRecipientId() => $"cus_{Guid.NewGuid():N}";

    internal static object ContactPoint(string channel, string value, bool verified = true)
        => new { channel, value, verified };

    internal static object ContactPointsBody(
        object[] contactPoints,
        string? timezone = null,
        string? locale = null)
        => new { timezone, locale, contactPoints };

    internal static object ConsentEntry(
        string purpose,
        string channel,
        bool granted,
        string source = "app",
        string termsVersion = "v1")
        => new { purpose, channel, granted, source, termsVersion };

    internal static Task<HttpResponseMessage> PutContactPointsAsync(
        HttpClient client,
        string recipientId,
        object body)
        => client.PutAsJsonAsync($"/v1/recipients/{recipientId}/contact-points", body);

    internal static Task<HttpResponseMessage> PutConsentsAsync(
        HttpClient client,
        string recipientId,
        params object[] consents)
        => client.PutAsJsonAsync($"/v1/recipients/{recipientId}/consents", new { consents });

    internal static Task<HttpResponseMessage> PostDeviceAsync(
        HttpClient client,
        string recipientId,
        string token,
        string platform = "android",
        string? appVersion = null)
        => client.PostAsJsonAsync(
            $"/v1/recipients/{recipientId}/devices",
            new { token, platform, appVersion });
}
