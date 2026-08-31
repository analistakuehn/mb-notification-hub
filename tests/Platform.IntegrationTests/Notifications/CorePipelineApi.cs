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

    /// <summary>The plan of the critical class: push with a deadline, then SMS.</summary>
    private static readonly (string Channel, string? Timeout)[] PushThenSms =
        [("push", "30s"), ("sms", null)];

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
        string[]? sensitiveVariables = null,
        string legalBasis = "execucao-de-contrato")
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
            legalBasis,
            defaultLocale = "pt-BR",
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
        etag = await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { code = new { type = "string" } },
            required = RequiredCode,
        }, etag);
        if (sensitiveVariables is not null)
        {
            await TemplateApi.PutSensitiveVariablesAsync(author, key, version, sensitiveVariables, etag);
        }

        await TemplateApi.PublishAsync(publisher, key, version);
        return (key, version);
    }

    /// <summary>
    /// Publishes a class policy through the same four-eyes flow the humans
    /// use. The plan defaults to the two steps of the critical class; a caller
    /// that names its own steps gets exactly those, and the allowed channels
    /// are read from them so the two halves of the policy cannot disagree.
    /// </summary>
    internal static async Task<int> CreatePublishedPolicyAsync(
        CorePipelineFixture fixture,
        string application,
        string @class,
        object? quietHours = null,
        string? consentPurpose = null,
        string dedupeWindow = "60s",
        (string Channel, string? Timeout)[]? deliveryPlan = null)
    {
        HttpClient author = fixture.CreateAuthorClient("policy-author");
        HttpClient publisher = fixture.CreatePublisherClient("policy-publisher");
        (string Channel, string? Timeout)[] steps = deliveryPlan ?? PushThenSms;
        await ClassPolicyApi.CreateDraftAsync(author, application, @class, new
        {
            schemaVersion = 1,
            channelsAllowed = steps.Select(step => step.Channel).Distinct().ToArray(),
            deliveryPlan = steps
                .Select(step => step.Timeout is null
                    ? (object)new { channel = step.Channel }
                    : new { channel = step.Channel, timeout = step.Timeout })
                .ToArray(),
            defaultTtl = "300s",
            dedupeWindow,
            quietHours,
            consentPurpose,
        });
        return await ClassPolicyApi.PublishAsync(publisher, application, @class);
    }

    /// <summary>
    /// Registers a recipient with an sms contact point and, optionally, a push
    /// device. A null timezone leaves the profile on the default of the
    /// contact context; a named one is how a test reaches a rule that decides
    /// in the recipient's own hours.
    /// </summary>
    internal static async Task<string> RegisterRecipientAsync(
        CorePipelineFixture fixture,
        bool withSmsContact = true,
        bool withDevice = true,
        string? timezone = null,
        params object[] consents)
    {
        HttpClient contacts = fixture.CreateContactsClient("contacts-writer");
        var recipientId = ContactConsentApi.NewRecipientId();
        object[] contactPoints = withSmsContact
            ? [ContactConsentApi.ContactPoint("sms", "+5511999990000")]
            : [];
        HttpResponseMessage declared = await ContactConsentApi.PutContactPointsAsync(
            contacts, recipientId, ContactConsentApi.ContactPointsBody(contactPoints, timezone));
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
