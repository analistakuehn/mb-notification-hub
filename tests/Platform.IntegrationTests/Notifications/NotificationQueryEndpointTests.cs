using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.ContactConsent;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class NotificationQueryEndpointTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_dispatched_email_answers_with_the_masked_address_and_never_with_the_content()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, var templateVersion) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(fixture, application, "transactional", ("email", null));
        (var recipientId, var email, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "sg-query-1" }));

        NotificationQueryApi.Accepted accepted = await NotificationQueryApi.AcceptAsync(
            fixture, application, templateKey, "transactional", recipientId);
        await RouteAsync("core-transactional");
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        (var status, JsonElement body, var raw) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{accepted.PublicId}");

        status.ShouldBe(200);

        // The members the contract always carries.
        body.GetProperty("id").GetString().ShouldBe(accepted.PublicId);
        body.GetProperty("application").GetString().ShouldBe(application);
        body.GetProperty("class").GetString().ShouldBe("transactional");
        body.GetProperty("status").GetString().ShouldBe(NotificationStatuses.Dispatched);
        body.GetProperty("templateKey").GetString().ShouldBe(templateKey);
        body.GetProperty("templateVersion").GetInt32().ShouldBe(templateVersion);
        body.GetProperty("requestedBy").GetString().ShouldBe("query-producer");
        body.GetProperty("createdAt").ValueKind.ShouldBe(JsonValueKind.String);
        body.GetProperty("expiresAt").ValueKind.ShouldBe(JsonValueKind.String);
        body.GetProperty("policyEvaluations").GetArrayLength().ShouldBeGreaterThan(0);

        // The members omitted because their value is genuinely absent.
        NotificationQueryApi.HasMember(body, "correlationId").ShouldBeFalse();
        NotificationQueryApi.HasMember(body, "releaseAt").ShouldBeFalse();

        // The members omitted because the phase has no source for them.
        NotificationQueryApi.HasMember(body, "deliveryEvents").ShouldBeFalse();
        NotificationQueryApi.HasMember(body, "readAt").ShouldBeFalse();

        JsonElement attempt = body.GetProperty("attempts").EnumerateArray().ShouldHaveSingleItem();
        attempt.GetProperty("sequence").GetInt32().ShouldBe(1);
        attempt.GetProperty("channel").GetString().ShouldBe("email");
        attempt.GetProperty("status").GetString().ShouldBe(NotificationAttemptStatuses.Sent);
        attempt.GetProperty("providerKey").GetString().ShouldBe("sendgrid");
        attempt.GetProperty("providerMessageId").GetString().ShouldBe("sg-query-1");
        attempt.GetProperty("contentHashFull").GetString().ShouldNotBeNullOrWhiteSpace();
        attempt.GetProperty("contentHashMasked").GetString().ShouldNotBeNullOrWhiteSpace();
        attempt.GetProperty("sentAt").ValueKind.ShouldBe(JsonValueKind.String);

        // Delivery is never claimed in this phase, so the member stays out.
        NotificationQueryApi.HasMember(attempt, "deliveredAt").ShouldBeFalse();
        NotificationQueryApi.HasMember(attempt, "errorCode").ShouldBeFalse();

        // The target is the masked contact, computed by the module that owns
        // the value; the address itself is nowhere in the body.
        JsonElement target = attempt.GetProperty("target");
        target.GetProperty("kind").GetString().ShouldBe("contact-point");
        target.GetProperty("active").GetBoolean().ShouldBeTrue();
        var masked = target.GetProperty("masked").GetString()!;
        masked.ShouldNotBe(email);
        masked.ShouldContain("*");
        masked.ShouldEndWith(email[email.IndexOf('@', StringComparison.Ordinal)..]);
        raw.ShouldNotContain(email);

        // Neither the rendered content nor the stored variables projection.
        raw.ShouldNotContain("123456");
        raw.Contains("variables", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        raw.Contains("renderedContent", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task A_push_attempt_exposes_the_platform_and_the_registration_never_the_token()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(fixture, application, "critical", ("push", null));
        (var recipientId, _, IReadOnlyList<string> tokens) =
            await DispatchApi.RegisterRecipientAsync(fixture, withEmail: false, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(200, """{"name":"projects/test-project/messages/0:9"}""", null));

        NotificationQueryApi.Accepted accepted = await NotificationQueryApi.AcceptAsync(
            fixture, application, templateKey, "critical", recipientId);
        await RouteAsync("core-auth");
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        Guid deviceTokenId = await fixture.QueryContactConsentDbAsync(db => db.DeviceTokens
            .AsNoTracking()
            .Where(device => device.RecipientId == recipientId)
            .Select(device => device.Id)
            .SingleAsync());

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        (var status, JsonElement body, var raw) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{accepted.PublicId}");

        status.ShouldBe(200);
        JsonElement attempt = body.GetProperty("attempts").EnumerateArray().First();
        attempt.GetProperty("channel").GetString().ShouldBe("push");

        JsonElement target = attempt.GetProperty("target");
        target.GetProperty("kind").GetString().ShouldBe("device");
        target.GetProperty("deviceTokenId").GetString().ShouldBe(deviceTokenId.ToString());
        target.GetProperty("platform").GetString().ShouldBe("android");
        target.GetProperty("active").GetBoolean().ShouldBeTrue();

        // A push attempt has no contact point, and the routing token never
        // leaves the directory, not even reduced to a mask.
        NotificationQueryApi.HasMember(target, "contactPointId").ShouldBeFalse();
        NotificationQueryApi.HasMember(target, "masked").ShouldBeFalse();
        raw.ShouldNotContain(tokens[0]);
        raw.ShouldNotContain("123456");

        // The provider declares the token dead through the lifecycle contract,
        // exactly as the dispatcher does on UNREGISTERED. The registration
        // leaves the active set, and the answer says so instead of going
        // silent: identity and inactive flag stay, the platform goes.
        using (IServiceScope scope = dispatcher.CreateScope())
        {
            Result invalidated = await scope.ServiceProvider
                .GetRequiredService<IDeviceTokenLifecycle>()
                .InvalidateDeviceTokenAsync(
                    recipientId, deviceTokenId, "UNREGISTERED", CancellationToken.None);
            invalidated.IsSuccess.ShouldBeTrue();
        }

        (_, JsonElement afterInvalidation, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{accepted.PublicId}");
        JsonElement inactive = afterInvalidation
            .GetProperty("attempts").EnumerateArray().First().GetProperty("target");
        inactive.GetProperty("deviceTokenId").GetString().ShouldBe(deviceTokenId.ToString());
        inactive.GetProperty("active").GetBoolean().ShouldBeFalse();
        NotificationQueryApi.HasMember(inactive, "platform").ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task A_contact_directory_that_cannot_answer_omits_the_target_facts_instead_of_inventing_them()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(fixture, application, "critical", ("push", null));
        (var recipientId, _, _) =
            await DispatchApi.RegisterRecipientAsync(fixture, withEmail: false, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(200, """{"name":"projects/test-project/messages/0:8"}""", null));

        NotificationQueryApi.Accepted accepted = await NotificationQueryApi.AcceptAsync(
            fixture, application, templateKey, "critical", recipientId);
        await RouteAsync("core-auth");
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // A host whose contact directory points at a database that does not
        // exist: the read fails, and a failed read may not be reported as an
        // inactive registration.
        using WebApplicationFactory<Program> blind = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] =
                        fixture.PostgresConnectionString.Replace(
                            "Database=", "Database=nao_existe_", StringComparison.Ordinal),
                })));

        HttpClient reader = fixture.CreateReaderClient(blind, "support-agent");
        (var status, JsonElement body, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{accepted.PublicId}");

        // The query still answers: enrichment degrades, it never fails the read.
        status.ShouldBe(200);
        JsonElement target = body.GetProperty("attempts").EnumerateArray().First().GetProperty("target");
        target.GetProperty("kind").GetString().ShouldBe("device");
        target.GetProperty("deviceTokenId").ValueKind.ShouldBe(JsonValueKind.String);
        NotificationQueryApi.HasMember(target, "platform").ShouldBeFalse();
        NotificationQueryApi.HasMember(target, "active").ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task A_removed_contact_point_still_answers_and_says_it_is_no_longer_active()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(fixture, application, "transactional", ("email", null));
        (var recipientId, var email, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "sg-query-2" }));

        NotificationQueryApi.Accepted accepted = await NotificationQueryApi.AcceptAsync(
            fixture, application, templateKey, "transactional", recipientId);
        await RouteAsync("core-transactional");
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The customer changes the address after the send: the declaration
        // stamps the old point removed.
        HttpClient contacts = fixture.CreateContactsClient("contacts-writer");
        HttpResponseMessage redeclared = await ContactConsentApi.PutContactPointsAsync(
            contacts,
            recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("email", $"novo-{Guid.NewGuid():N}@example.com")]));
        redeclared.EnsureSuccessStatusCode();

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        (var status, JsonElement body, var raw) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{accepted.PublicId}");

        status.ShouldBe(200);
        JsonElement target = body.GetProperty("attempts").EnumerateArray().First().GetProperty("target");
        target.GetProperty("active").GetBoolean().ShouldBeFalse();
        target.GetProperty("masked").GetString().ShouldNotBeNullOrWhiteSpace();
        raw.ShouldNotContain(email);
    }

    private async Task RouteAsync(string coreQueue)
    {
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, coreQueue)).Processed.ShouldBeGreaterThanOrEqualTo(1);
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
    }
}
