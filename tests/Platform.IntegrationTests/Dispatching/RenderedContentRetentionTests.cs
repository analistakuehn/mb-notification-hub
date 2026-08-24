using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// The two-phase life of rendered content: complete while the send needs it,
/// masked once nothing does. The template of these tests declares the access
/// code as a sensitive variable, so the two forms differ and every transition
/// is observable.
/// </summary>
public sealed class RenderedContentRetentionTests(CorePipelineFixture fixture)
    : IClassFixture<CorePipelineFixture>
{
    /// <summary>Value of the sensitive variable the seeded template renders.</summary>
    private const string AccessCode = "123456";

    private const string Mask = "***";

    private static readonly string[] SensitiveCode = ["code"];

    [RequiresDockerFact]
    public async Task The_verdict_leaves_the_masked_form_at_rest_after_the_provider_took_the_complete_one()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates", SensitiveCode);
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, var email, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "sg-otp-1" }));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        // Before the verdict the envelope still carries the complete content,
        // which is what the dispatcher has to send, with the masked form
        // sealed beside it.
        NotificationAttempt queued = await SingleAttemptAsync(notificationId);
        queued.ContentHashFull.ShouldNotBe(queued.ContentHashMasked);
        SealedContentView beforeVerdict = await ReadSealedAsync(application, queued.RenderedContentEncrypted);
        beforeVerdict.Body.ShouldContain(AccessCode);
        beforeVerdict.CarriesMaskedForm.ShouldBeTrue();

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        NotificationAttempt settled = await SingleAttemptAsync(notificationId);
        settled.Status.ShouldBe(NotificationAttemptStatuses.Sent);

        // The send did not regress: the provider got the complete content.
        FakeProviderRequest request = provider.Requests
            .Where(candidate => candidate.Body.Contains(email, StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        request.Body.ShouldContain(AccessCode);

        // What stays at rest is the masked form and only it: field by field
        // equal to the masked render, hashing to the recorded masked hash,
        // with the code gone from the whole envelope and no companion left.
        RenderedForm masked = await MaskedRenderAsync(application, templateKey, beforeVerdict);
        masked.ContentHash.ShouldBe(settled.ContentHashMasked);
        SealedContentView durable = await ReadSealedAsync(application, settled.RenderedContentEncrypted);
        durable.Subject.ShouldBe(masked.Subject);
        durable.Body.ShouldBe(masked.Body);
        durable.BodyText.ShouldBe(masked.BodyText);
        durable.Body.ShouldContain(Mask);
        durable.Raw.ShouldNotContain(AccessCode);
        durable.CarriesMaskedForm.ShouldBeFalse();

        // The hash of the complete form survives the transition: it is the
        // anchor for confronting external evidence.
        settled.ContentHashFull.ShouldBe(queued.ContentHashFull);
    }

    [RequiresDockerFact]
    public async Task A_render_whose_two_forms_coincide_is_never_rewritten_by_the_verdict()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        NotificationAttempt queued = await SingleAttemptAsync(notificationId);
        queued.ContentHashFull.ShouldBe(queued.ContentHashMasked);
        var sealedBytes = queued.RenderedContentEncrypted;
        (await ReadSealedAsync(application, sealedBytes)).CarriesMaskedForm.ShouldBeFalse();

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // Nothing to discard, so nothing is written: the stored bytes are the
        // ones the render stage sealed, byte for byte.
        NotificationAttempt settled = await SingleAttemptAsync(notificationId);
        settled.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        settled.RenderedContentEncrypted.ShouldBe(sealedBytes);
    }

    [RequiresDockerFact]
    public async Task A_fallback_seals_its_own_two_forms_while_the_failed_attempt_keeps_only_the_masked_one()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates", SensitiveCode);
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", "30s"), ("push", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            400, """{"errors":[{"message":"invalid","field":"to"}]}""", null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The plan advances: the relay carries the trigger and the core role
        // renders and seals the next step on its own.
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, "core-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        List<NotificationAttempt> attempts = await AttemptsAsync(notificationId);
        attempts.Count.ShouldBe(2);

        // The failed step keeps only the masked form.
        attempts[0].Channel.ShouldBe("email");
        attempts[0].Status.ShouldBe(NotificationAttemptStatuses.Failed);
        SealedContentView failed = await ReadSealedAsync(application, attempts[0].RenderedContentEncrypted);
        failed.Raw.ShouldNotContain(AccessCode);
        failed.Body.ShouldContain(Mask);
        failed.CarriesMaskedForm.ShouldBeFalse();

        // The next step sealed its own two forms: the fallback renders, it
        // never reuses the seal of the step that failed.
        attempts[1].Channel.ShouldBe("push");
        attempts[1].Status.ShouldBe(NotificationAttemptStatuses.Queued);
        attempts[1].ContentHashFull.ShouldNotBe(attempts[1].ContentHashMasked);
        SealedContentView next = await ReadSealedAsync(application, attempts[1].RenderedContentEncrypted);
        next.Body.ShouldContain(AccessCode);
        next.CarriesMaskedForm.ShouldBeTrue();
    }

    [RequiresDockerFact]
    public async Task The_sweep_settles_abandoned_attempts_past_the_expiry_window_and_leaves_the_live_one()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates", SensitiveCode);
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var orphanRecipient, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        (var crashedRecipient, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        (var liveRecipient, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);

        // Three notifications no dispatcher ever settles: one left queued, one
        // parked on sending by a crash between claim and verdict, and one that
        // stays valid for a day.
        Guid orphanId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", orphanRecipient, "core-transactional");
        Guid crashedId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", crashedRecipient, "core-transactional");
        Guid liveId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", liveRecipient, "core-transactional",
            ttlSeconds: 86_400);

        NotificationAttempt crashed = await SingleAttemptAsync(crashedId);
        await ForceStatusAsync(crashed.Id, NotificationAttemptStatuses.Sending);
        var liveBefore = (await SingleAttemptAsync(liveId)).RenderedContentEncrypted;

        // Two hours later every expired notification is past the grace, and
        // the one with a day of validity is not.
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(2));
        await using ServiceProvider maintenance = fixture.BuildMaintenanceWorkerProvider(
            replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        using IServiceScope scope = maintenance.CreateScope();
        (await scope.ServiceProvider
            .GetRequiredService<RenderedContentSweep>()
            .RunAsync(CancellationToken.None))
            .ShouldBeGreaterThanOrEqualTo(2);

        foreach (Guid abandoned in new[] { orphanId, crashedId })
        {
            SealedContentView settled = await ReadSealedAsync(
                application, (await SingleAttemptAsync(abandoned)).RenderedContentEncrypted);
            settled.Raw.ShouldNotContain(AccessCode);
            settled.Body.ShouldContain(Mask);
            settled.CarriesMaskedForm.ShouldBeFalse();
        }

        // The attempt still inside its notification's validity may yet be
        // sent, so it keeps the content it was queued with.
        (await SingleAttemptAsync(liveId)).RenderedContentEncrypted.ShouldBe(liveBefore);
    }

    [RequiresDockerFact]
    public async Task The_backfill_substitutes_only_the_rows_whose_recomputed_hash_matches_the_recorded_one()
    {
        var application = DispatchApi.NewApplication();
        (var stableKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates", SensitiveCode);
        (var movedKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates", SensitiveCode);
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var stableRecipient, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        (var movedRecipient, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));

        Guid stableId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, stableKey, "transactional", stableRecipient, "core-transactional");
        Guid movedId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, movedKey, "transactional", movedRecipient, "core-transactional");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional");
        await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional");

        NotificationAttempt stable = await SingleAttemptAsync(stableId);
        NotificationAttempt moved = await SingleAttemptAsync(movedId);
        stable.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        moved.Status.ShouldBe(NotificationAttemptStatuses.Sent);

        // Both rows go back to the shape of content written before the
        // two-form seal existed: the complete form alone, with no companion
        // to promote.
        var stableLegacy = await SealCompleteFormAsync(application, stableKey, stable);
        var movedLegacy = await SealCompleteFormAsync(application, movedKey, moved);
        await ForceContentAsync(stable.Id, stableLegacy);
        await ForceContentAsync(moved.Id, movedLegacy);

        // The published content of one of them moved on after the send, so no
        // fresh render can reproduce the hash that attempt recorded.
        await DispatchApi.PublishVersionAsync(fixture, movedKey, "Seu código de acesso é {{ code }}.");

        var logs = new CapturingLoggerProvider();
        await using ServiceProvider maintenance = fixture.BuildMaintenanceWorkerProvider(
            new Dictionary<string, string?>
            {
                ["Modules:Notifications:RenderedContentBackfill:Enabled"] = "true",
            },
            logs);
        using IServiceScope scope = maintenance.CreateScope();
        RenderedContentBackfillResult result = await scope.ServiceProvider
            .GetRequiredService<RenderedContentBackfill>()
            .RunAsync(CancellationToken.None);
        result.Masked.ShouldBeGreaterThanOrEqualTo(1);
        result.NeedsReview.ShouldBeGreaterThanOrEqualTo(1);

        // The row whose recomputed hash matches carries only the masked form.
        SealedContentView substituted = await ReadSealedAsync(
            application, (await SingleAttemptAsync(stableId)).RenderedContentEncrypted);
        substituted.Raw.ShouldNotContain(AccessCode);
        substituted.Body.ShouldContain(Mask);
        substituted.CarriesMaskedForm.ShouldBeFalse();

        // The row whose hash no longer matches was not touched, byte for
        // byte, and left in the review list under its own identity.
        (await SingleAttemptAsync(movedId)).RenderedContentEncrypted.ShouldBe(movedLegacy);
        logs.Lines.ShouldContain(line =>
            line.Contains(moved.Id.ToString(), StringComparison.Ordinal)
            && line.Contains(RenderedContentBackfill.ReviewReasonHashMismatch, StringComparison.Ordinal));
    }

    private async Task<NotificationAttempt> SingleAttemptAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.NotificationId == notificationId));

    private async Task<List<NotificationAttempt>> AttemptsAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(candidate => candidate.NotificationId == notificationId)
            .OrderBy(candidate => candidate.Sequence)
            .ToListAsync());

    private async Task<int> ForceStatusAsync(Guid attemptId, string status)
        => await fixture.UsingScopeAsync(async serviceProvider => await serviceProvider
            .GetRequiredService<NotificationsDbContext>()
            .NotificationAttempts
            .Where(attempt => attempt.Id == attemptId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(attempt => attempt.Status, status)));

    private async Task<int> ForceContentAsync(Guid attemptId, byte[] sealedContent)
        => await fixture.UsingScopeAsync(async serviceProvider => await serviceProvider
            .GetRequiredService<NotificationsDbContext>()
            .NotificationAttempts
            .Where(attempt => attempt.Id == attemptId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                attempt => attempt.RenderedContentEncrypted, sealedContent)));

    /// <summary>Seals the complete render alone, which is the shape of a row written before the transition.</summary>
    private async Task<byte[]> SealCompleteFormAsync(
        string application,
        string templateKey,
        NotificationAttempt attempt)
    {
        SealedContentView current = await ReadSealedAsync(application, attempt.RenderedContentEncrypted);
        PublishedTemplateRender render = await RenderAsync(application, templateKey, current, masked: false);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            channel = current.Channel,
            locale = current.Locale,
            subject = render.Full.Subject,
            body = render.Full.Body,
            bodyText = render.Full.BodyText,
        });
        return await fixture.Services
            .GetRequiredService<IEnvelopeCipher>()
            .EncryptAsync(application, plaintext, CancellationToken.None);
    }

    private async Task<RenderedForm> MaskedRenderAsync(
        string application,
        string templateKey,
        SealedContentView stored)
    {
        PublishedTemplateRender render = await RenderAsync(application, templateKey, stored, masked: true);
        return render.Masked.ShouldNotBeNull();
    }

    private async Task<PublishedTemplateRender> RenderAsync(
        string application,
        string templateKey,
        SealedContentView stored,
        bool masked)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        Result<PublishedTemplateRender> render = await scope.ServiceProvider
            .GetRequiredService<IPublishedTemplateRenderer>()
            .RenderAsync(
                new PublishedRenderRequest
                {
                    Application = application,
                    TemplateKey = templateKey,
                    Channel = stored.Channel,
                    Locale = stored.Locale,
                    Variables = JsonSerializer.SerializeToElement(new { code = AccessCode }),
                    IncludeMaskedForm = masked,
                },
                CancellationToken.None);
        render.IsSuccess.ShouldBeTrue();
        return render.Value.ShouldNotBeNull();
    }

    private async Task<SealedContentView> ReadSealedAsync(string application, byte[] sealedContent)
    {
        var plaintext = await fixture.Services
            .GetRequiredService<IEnvelopeCipher>()
            .DecryptAsync(application, sealedContent, CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(plaintext);
        JsonElement root = document.RootElement;
        return new SealedContentView(
            root.GetProperty("channel").GetString()!,
            root.GetProperty("locale").GetString()!,
            Text(root, "subject"),
            root.GetProperty("body").GetString()!,
            Text(root, "bodyText"),
            root.TryGetProperty("masked", out _),
            Encoding.UTF8.GetString(plaintext));
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>One opened envelope, plus its raw text so a test can prove a value is nowhere in it.</summary>
    private sealed record SealedContentView(
        string Channel,
        string Locale,
        string? Subject,
        string Body,
        string? BodyText,
        bool CarriesMaskedForm,
        string Raw);
}
