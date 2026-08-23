using System.Net.Http.Json;
using NotificationHub.IntegrationTests.ContactConsent;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// Seeding of the governed catalog the pipeline reads: published templates
/// with per-channel content, published class policies, and recipients with
/// contacts, consents and devices, everything through the public API with the
/// real four-eyes flow.
/// </summary>
internal static class CorePipelineApi
{
    private static readonly string[] RequiredCode = ["code"];
    private static readonly string[] PushAndSms = ["push", "sms"];

    internal static string NewApplication() => $"app-{Guid.NewGuid():N}";

    /// <summary>
    /// Publishes a template with push and sms content whose schema declares a
    /// required <c>code</c> variable.
    /// </summary>
    internal static async Task<(string Key, int Version)> CreatePublishedTemplateAsync(
        CorePipelineFixture fixture,
        string application,
        string @class,
        string purpose,
        string[]? sensitiveVariables = null)
    {
        HttpClient author = fixture.CreateAuthorClient("template-author");
        HttpClient publisher = fixture.CreatePublisherClient("template-publisher");
        var key = TemplateApi.NewKey("core");

        HttpResponseMessage created = await author.PostAsJsonAsync("/v1/templates", new
        {
            key,
            application,
            @class,
            ownerTeam = "growth-squad",
            purpose,
            legalBasis = "execucao-de-contrato",
            defaultLocale = "pt-BR",
            sensitiveVariables,
        });
        created.EnsureSuccessStatusCode();

        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "push/pt-BR", new
        {
            subject = "Seu código",
            body = "Use o código {{ code }} para entrar.",
        }, etag);
        etag = await TemplateApi.PutContentAsync(author, key, version, "sms/pt-BR", new
        {
            body = "Código de acesso: {{ code }}.",
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

    /// <summary>Publishes a class policy through the same four-eyes flow the humans use.</summary>
    internal static async Task<int> CreatePublishedPolicyAsync(
        CorePipelineFixture fixture,
        string application,
        string @class,
        object? quietHours = null,
        string? consentPurpose = null,
        string dedupeWindow = "60s")
    {
        HttpClient author = fixture.CreateAuthorClient("policy-author");
        HttpClient publisher = fixture.CreatePublisherClient("policy-publisher");
        await ClassPolicyApi.CreateDraftAsync(author, application, @class, new
        {
            schemaVersion = 1,
            channelsAllowed = PushAndSms,
            deliveryPlan = new object[]
            {
                new { channel = "push", timeout = "30s" },
                new { channel = "sms" },
            },
            defaultTtl = "300s",
            dedupeWindow,
            quietHours,
            consentPurpose,
        });
        return await ClassPolicyApi.PublishAsync(publisher, application, @class);
    }

    /// <summary>Registers a recipient with an sms contact point and, optionally, a push device.</summary>
    internal static async Task<string> RegisterRecipientAsync(
        CorePipelineFixture fixture,
        bool withSmsContact = true,
        bool withDevice = true,
        params object[] consents)
    {
        HttpClient contacts = fixture.CreateContactsClient("contacts-writer");
        var recipientId = ContactConsentApi.NewRecipientId();
        object[] contactPoints = withSmsContact
            ? [ContactConsentApi.ContactPoint("sms", "+5511999990000")]
            : [];
        HttpResponseMessage declared = await ContactConsentApi.PutContactPointsAsync(
            contacts, recipientId, ContactConsentApi.ContactPointsBody(contactPoints));
        declared.EnsureSuccessStatusCode();
        if (withDevice)
        {
            HttpResponseMessage device = await ContactConsentApi.PostDeviceAsync(
                contacts, recipientId, $"token-{Guid.NewGuid():N}");
            device.EnsureSuccessStatusCode();
        }

        if (consents.Length > 0)
        {
            HttpResponseMessage granted = await ContactConsentApi.PutConsentsAsync(
                contacts, recipientId, consents);
            granted.EnsureSuccessStatusCode();
        }

        return recipientId;
    }

    internal static object NotificationBody(
        string application,
        string templateKey,
        string @class,
        string recipientId,
        int ttlSeconds = 300)
        => new
        {
            application,
            recipientId,
            @class,
            templateKey,
            locale = "pt-BR",
            variables = new { code = "123456" },
            ttlSeconds,
        };
}
