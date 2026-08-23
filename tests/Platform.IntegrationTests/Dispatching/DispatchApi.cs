using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.ContactConsent;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// Seeding and provider settings shared by the dispatch tests: templates with
/// email and push content, class policies with configurable plans, recipients
/// with contacts and devices, and the fake provider endpoints the dispatcher
/// role talks to.
/// </summary>
internal static class DispatchApi
{
    internal const string FcmTokenPath = "/oauth/token";

    internal const string FcmTokenBody =
        """{"access_token":"fake-access-token","expires_in":3600,"token_type":"Bearer"}""";

    private static readonly string[] RequiredCode = ["code"];

    internal static string NewApplication() => $"app-{Guid.NewGuid():N}";

    /// <summary>Publishes a template with email and push content over a required <c>code</c> variable.</summary>
    internal static async Task<(string Key, int Version)> CreatePublishedTemplateAsync(
        CorePipelineFixture fixture,
        string application,
        string @class,
        string purpose)
    {
        HttpClient author = fixture.CreateAuthorClient("template-author");
        HttpClient publisher = fixture.CreatePublisherClient("template-publisher");
        var key = TemplateApi.NewKey("dsp");

        HttpResponseMessage created = await author.PostAsJsonAsync("/v1/templates", new
        {
            key,
            application,
            @class,
            ownerTeam = "growth-squad",
            purpose,
            legalBasis = "execucao-de-contrato",
            defaultLocale = "pt-BR",
        });
        created.EnsureSuccessStatusCode();

        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Seu código de acesso",
            body = "<p>Use o código {{ code }} para entrar.</p>",
            bodyText = "Use o código {{ code }} para entrar.",
        }, etag);
        etag = await TemplateApi.PutContentAsync(author, key, version, "push/pt-BR", new
        {
            subject = "Seu código",
            body = "Use o código {{ code }} para entrar.",
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

    /// <summary>Publishes a class policy whose plan follows the given ordered steps.</summary>
    internal static async Task<int> CreatePublishedPolicyAsync(
        CorePipelineFixture fixture,
        string application,
        string @class,
        params (string Channel, string? Timeout)[] steps)
    {
        HttpClient author = fixture.CreateAuthorClient("policy-author");
        HttpClient publisher = fixture.CreatePublisherClient("policy-publisher");
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
            dedupeWindow = "1s",
            quietHours = (object?)null,
            consentPurpose = (string?)null,
        });
        return await ClassPolicyApi.PublishAsync(publisher, application, @class);
    }

    /// <summary>
    /// Registers a recipient with an e-mail contact and the given number of
    /// devices, registered in order so the last one is the most recent.
    /// Returns the recipient id and the registered token values, newest
    /// first.
    /// </summary>
    internal static async Task<(string RecipientId, string Email, IReadOnlyList<string> TokensNewestFirst)>
        RegisterRecipientAsync(CorePipelineFixture fixture, bool withEmail = true, int deviceCount = 0)
    {
        HttpClient contacts = fixture.CreateContactsClient("contacts-writer");
        var recipientId = ContactConsentApi.NewRecipientId();
        var email = $"pessoa-{Guid.NewGuid():N}@example.com";
        object[] contactPoints = withEmail ? [ContactConsentApi.ContactPoint("email", email)] : [];
        HttpResponseMessage declared = await ContactConsentApi.PutContactPointsAsync(
            contacts, recipientId, ContactConsentApi.ContactPointsBody(contactPoints));
        declared.EnsureSuccessStatusCode();

        var tokens = new List<string>();
        for (var index = 0; index < deviceCount; index++)
        {
            var token = $"tok-{index}-{Guid.NewGuid():N}";
            HttpResponseMessage device = await ContactConsentApi.PostDeviceAsync(
                contacts, recipientId, token);
            device.EnsureSuccessStatusCode();
            tokens.Add(token);
            // The registration instant orders the fan-out; keep them distinct.
            await Task.Delay(20);
        }

        tokens.Reverse();
        return (recipientId, email, tokens);
    }

    /// <summary>
    /// Accepts one notification and walks it to the queued attempt on its
    /// dispatch queue: ingestion, relay, core pass, relay again. Returns the
    /// notification id.
    /// </summary>
    internal static async Task<Guid> AcceptAndRouteAsync(
        CorePipelineFixture fixture,
        string application,
        string templateKey,
        string @class,
        string recipientId,
        string coreQueue,
        int ttlSeconds = 300)
    {
        var role = @class switch
        {
            "critical" => NotificationsApi.SendCritical,
            "operational" => NotificationsApi.SendOperational,
            _ => NotificationsApi.SendTransactional,
        };
        HttpClient producer = fixture.CreateProducerClient("dispatch-producer", role);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            new
            {
                application,
                recipientId,
                @class,
                templateKey,
                locale = "pt-BR",
                variables = new { code = "123456" },
                ttlSeconds,
            },
            Guid.NewGuid().ToString("N"));
        accepted.EnsureSuccessStatusCode();
        JsonElement body = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString(), out Guid notificationId)
            .ShouldBeTrue();

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, coreQueue))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        return notificationId;
    }

    /// <summary>Dispatcher settings pointing both providers at the given fake server.</summary>
    internal static Dictionary<string, string?> ProviderSettings(
        Uri sendGridBase,
        Uri fcmBase,
        int timeoutSeconds = 5)
    {
        using var rsa = RSA.Create(2048);
        var timeout = timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, string?>
        {
            ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = sendGridBase.ToString(),
            ["Modules:Dispatch:Providers:SendGrid:ApiKey"] = "test-key",
            ["Modules:Dispatch:Providers:SendGrid:SenderEmail"] = "no-reply@example.com",
            ["Modules:Dispatch:Providers:SendGrid:TimeoutSeconds"] = timeout,
            ["Modules:Dispatch:Providers:Fcm:BaseAddress"] = fcmBase.ToString(),
            ["Modules:Dispatch:Providers:Fcm:ProjectId"] = "test-project",
            ["Modules:Dispatch:Providers:Fcm:ServiceAccountEmail"] = "svc@test-project.iam.gserviceaccount.com",
            ["Modules:Dispatch:Providers:Fcm:ServiceAccountPrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["Modules:Dispatch:Providers:Fcm:TokenUri"] = new Uri(fcmBase, FcmTokenPath).ToString(),
            ["Modules:Dispatch:Providers:Fcm:TimeoutSeconds"] = timeout,
        };
    }

    /// <summary>Reads the pending outbox payloads addressed to one destination and mentioning the notification.</summary>
    internal static async Task<List<string>> ReadOutboxPayloadsAsync(
        CorePipelineFixture fixture,
        string destination,
        Guid notificationId)
        => await fixture.QueryPlatformDbAsync(db => db.Database
            .SqlQuery<string>(
                $"""
                SELECT payload::text AS "Value" FROM platform.outbox
                WHERE destination = {destination}
                  AND payload->'payload'->>'notificationId' = {notificationId.ToString()}
                """)
            .ToListAsync());
}
