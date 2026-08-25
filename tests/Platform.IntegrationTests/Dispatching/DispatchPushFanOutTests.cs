using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class DispatchPushFanOutTests(CorePipelineFixture fixture)
{
    private const string FcmSendPath = "/v1/projects/test-project/messages:send";

    private const string UnregisteredBody = """
        {"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND",
        "details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}
        """;

    [RequiresDockerFact]
    public async Task The_fan_out_expands_on_claim_caps_at_five_tokens_and_keeps_the_notification_open()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", "30s"), ("email", null));
        (var recipientId, _, IReadOnlyList<string> tokensNewestFirst) =
            await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: 7);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(200, """{"name":"projects/test-project/messages/0:1"}""", null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        Dictionary<string, Guid> deviceIdByToken = await fixture.QueryContactConsentDbAsync(
            db => db.DeviceTokens
                .AsNoTracking()
                .Where(device => device.RecipientId == recipientId)
                .ToDictionaryAsync(device => device.Token, device => device.Id));

        // Five attempts at most: the claimed one carries the newest token and
        // each sibling carries the next one, all sharing the step's deadline
        // and the sealed content.
        List<NotificationAttempt> attempts = await fixture.QueryNotificationsDbAsync(
            db => db.NotificationAttempts
                .AsNoTracking()
                .Where(candidate => candidate.NotificationId == notificationId)
                .OrderBy(candidate => candidate.Sequence)
                .ToListAsync());
        attempts.Count.ShouldBe(5);
        attempts.Select(attempt => attempt.Sequence).ShouldBe([1, 2, 3, 4, 5]);
        attempts[0].Status.ShouldBe(NotificationAttemptStatuses.Sent);
        attempts[0].DeviceTokenId.ShouldBe(deviceIdByToken[tokensNewestFirst[0]]);
        foreach (var index in Enumerable.Range(1, 4))
        {
            attempts[index].Status.ShouldBe(NotificationAttemptStatuses.Queued);
            attempts[index].DeviceTokenId.ShouldBe(deviceIdByToken[tokensNewestFirst[index]]);
            attempts[index].FallbackDeadline.ShouldBe(attempts[0].FallbackDeadline);
            attempts[index].ContentHashFull.ShouldBe(attempts[0].ContentHashFull);
            attempts[index].ContentHashMasked.ShouldBe(attempts[0].ContentHashMasked);
        }

        // The plan has a later step, so the acceptance is not the delivery:
        // the notification stays open until a confirmation arrives or the plan
        // concludes. Closing it here would make the deadline trigger read the
        // notification as already settled and the step that exists to rescue
        // an undelivered push would never run.
        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Dispatched);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "notification.delivered"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(0);

        // Each sibling was announced to the same queue and follows the
        // normal claim-and-send path; every revealed token reached the
        // provider exactly once.
        List<string> announcements = await DispatchApi.ReadOutboxPayloadsAsync(
            fixture, "dispatch-push-auth", notificationId);
        announcements.Count.ShouldBe(5);
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(4);
        (await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .CountAsync(candidate => candidate.NotificationId == notificationId
                && candidate.Status == NotificationAttemptStatuses.Sent)))
            .ShouldBe(5);
        // Scoped to this recipient's tokens: relay passes may also publish
        // pending attempts of earlier tests in the shared collection.
        var myTokens = new HashSet<string>(tokensNewestFirst, StringComparer.Ordinal);
        var sentTokens = provider.Requests
            .Where(request => request.Path == FcmSendPath)
            .Select(request => JsonDocument.Parse(request.Body).RootElement
                .GetProperty("message").GetProperty("token").GetString())
            .Where(token => token is not null && myTokens.Contains(token))
            .ToList();
        sentTokens.Count.ShouldBe(5);
        sentTokens.ShouldBe(tokensNewestFirst.Take(5).ToList(), ignoreOrder: true);
    }

    [RequiresDockerFact]
    public async Task Zero_active_tokens_at_claim_fail_the_attempt_and_the_fallback_reaches_the_email_step()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", "30s"), ("email", null));
        (var recipientId, var email, IReadOnlyList<string> tokens) =
            await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(
                202, null, new Dictionary<string, string> { ["X-Message-Id"] = "sg-fallback-1" }));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");

        // The only token dies between the routing and the claim: the exact
        // window the zero-token rule exists for.
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        await InvalidateTokenAndRefreshSnapshotAsync(dispatcher, recipientId, tokens[0]);
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The push attempt failed with the stable code and the trigger left in
        // the same transaction. The template serves an authentication flow, so
        // the trigger is addressed to the authentication core queue: the relay
        // reads the band off the destination, and the next step of a code has
        // to keep the band the first step already had.
        NotificationAttempt pushAttempt = await fixture.QueryNotificationsDbAsync(
            db => db.NotificationAttempts
                .AsNoTracking()
                .SingleAsync(candidate => candidate.NotificationId == notificationId));
        pushAttempt.Status.ShouldBe(NotificationAttemptStatuses.Failed);
        pushAttempt.ErrorCode.ShouldBe("no-active-device-token");
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "fallback.triggered"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(1);
        List<string> triggers = await DispatchApi.ReadOutboxPayloadsAsync(
            fixture, "core-auth", notificationId);
        triggers.Count(payload => payload.Contains("fallback.requested", StringComparison.Ordinal))
            .ShouldBe(1);

        // The Core consumes the trigger and queues the next plan step through
        // the same commit invariant, with the auth routing of the template.
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, "core-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        NotificationAttempt emailAttempt = await fixture.QueryNotificationsDbAsync(
            db => db.NotificationAttempts
                .AsNoTracking()
                .SingleAsync(candidate => candidate.NotificationId == notificationId
                    && candidate.Channel == "email"));
        emailAttempt.Status.ShouldBe(NotificationAttemptStatuses.Queued);
        emailAttempt.Sequence.ShouldBe(2);
        emailAttempt.ContactPointId.ShouldNotBeNull();
        emailAttempt.FallbackDeadline.ShouldBeNull();
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "fallback.attempt_queued"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(1);

        // The e-mail attempt follows the normal path to sent.
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        NotificationAttempt sent = await fixture.QueryNotificationsDbAsync(
            db => db.NotificationAttempts
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == emailAttempt.Id));
        sent.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        provider.Requests
            .Where(request => request.Path == "/v3/mail/send"
                && request.Body.Contains(email, StringComparison.Ordinal))
            .ShouldHaveSingleItem();
    }

    [RequiresDockerFact]
    public async Task An_unregistered_token_invalidates_the_device_and_only_the_last_sibling_failure_triggers_the_fallback()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", "30s"), ("email", null));
        (var recipientId, _, IReadOnlyList<string> tokensNewestFirst) =
            await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: 2);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(404, UnregisteredBody, null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // First failure: the sibling is still queued, so no trigger yet, and
        // the dead token is already invalidated at the source of truth.
        (await DispatchApi.ReadOutboxPayloadsAsync(fixture, "core-auth", notificationId))
            .ShouldAllBe(payload => !payload.Contains("fallback.requested"));
        Dictionary<string, DateTimeOffset?> invalidatedByToken = await fixture.QueryContactConsentDbAsync(
            db => db.DeviceTokens
                .AsNoTracking()
                .Where(device => device.RecipientId == recipientId)
                .ToDictionaryAsync(device => device.Token, device => device.InvalidatedAt));
        invalidatedByToken[tokensNewestFirst[0]].ShouldNotBeNull();
        invalidatedByToken[tokensNewestFirst[1]].ShouldBeNull();

        // The sibling fails too: now every sibling is terminal and exactly
        // one trigger leaves.
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        (await DispatchApi.ReadOutboxPayloadsAsync(fixture, "core-auth", notificationId))
            .Count(payload => payload.Contains("fallback.requested", StringComparison.Ordinal))
            .ShouldBe(1);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "fallback.triggered"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(1);

        Dictionary<string, Guid> deviceIdByToken = await fixture.QueryContactConsentDbAsync(
            db => db.DeviceTokens
                .AsNoTracking()
                .Where(device => device.RecipientId == recipientId)
                .ToDictionaryAsync(device => device.Token, device => device.Id));
        List<DateTimeOffset?> invalidations = await fixture.QueryContactConsentDbAsync(
            db => db.DeviceTokens
                .AsNoTracking()
                .Where(device => device.RecipientId == recipientId)
                .Select(device => device.InvalidatedAt)
                .ToListAsync());
        invalidations.ShouldAllBe(instant => instant != null);

        // Repeating the report is a declarative no-op: the first instant
        // stays and no second cache-invalidation event leaves.
        var contactEventsBefore = await CountContactEventsAsync(recipientId);
        DateTimeOffset? firstInstant = await fixture.QueryContactConsentDbAsync(
            db => db.DeviceTokens
                .AsNoTracking()
                .Where(device => device.Id == deviceIdByToken[tokensNewestFirst[0]])
                .Select(device => device.InvalidatedAt)
                .SingleAsync());
        using (IServiceScope scope = dispatcher.CreateScope())
        {
            IDeviceTokenLifecycle lifecycle =
                scope.ServiceProvider.GetRequiredService<IDeviceTokenLifecycle>();
            (await lifecycle.InvalidateDeviceTokenAsync(
                recipientId, deviceIdByToken[tokensNewestFirst[0]], "UNREGISTERED", CancellationToken.None))
                .IsSuccess.ShouldBeTrue();
        }

        (await fixture.QueryContactConsentDbAsync(db => db.DeviceTokens
            .AsNoTracking()
            .Where(device => device.Id == deviceIdByToken[tokensNewestFirst[0]])
            .Select(device => device.InvalidatedAt)
            .SingleAsync()))
            .ShouldBe(firstInstant);
        (await CountContactEventsAsync(recipientId)).ShouldBe(contactEventsBefore);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "device.invalidated")))
            .ShouldBeGreaterThanOrEqualTo(3);
    }

    [RequiresDockerFact]
    public async Task A_fallback_trigger_after_ttl_expiry_ends_the_notification_on_expired()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", "30s"), ("email", null));
        (var recipientId, _, IReadOnlyList<string> tokens) =
            await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");

        // The only token dies before the claim, so the failed push asks for
        // the fallback while the TTL is already gone.
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        await InvalidateTokenAndRefreshSnapshotAsync(dispatcher, recipientId, tokens[0]);
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The TTL ends before the Core handles the trigger.
        await fixture.QueryNotificationsDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""
            UPDATE notifications.notification
            SET expires_at = now() - interval '1 second'
            WHERE id = {notificationId}
            """));

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, "core-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Expired);
        notification.VariablesEncrypted.ShouldBeNull();
        (await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .CountAsync(candidate => candidate.NotificationId == notificationId)))
            .ShouldBe(1);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "notification.expired"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(1);
    }

    private async Task<int> CountContactEventsAsync(string recipientId)
        => await fixture.QueryPlatformDbAsync(db => db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value" FROM platform.outbox
                WHERE destination = 'contacts-changed' AND message_key = {recipientId}
                """)
            .SingleAsync());

    /// <summary>
    /// Kills one registered token through the published lifecycle and lets
    /// the invalidation reach the snapshot cache, so the next directory read
    /// revalidates against the store instead of serving the cached device.
    /// </summary>
    private async Task InvalidateTokenAndRefreshSnapshotAsync(
        ServiceProvider dispatcher,
        string recipientId,
        string token)
    {
        Guid deviceTokenId = await fixture.QueryContactConsentDbAsync(db => db.DeviceTokens
            .AsNoTracking()
            .Where(device => device.RecipientId == recipientId && device.Token == token)
            .Select(device => device.Id)
            .SingleAsync());
        using (IServiceScope scope = dispatcher.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IDeviceTokenLifecycle>()
                .InvalidateDeviceTokenAsync(
                    recipientId, deviceTokenId, "UNREGISTERED", CancellationToken.None))
                .IsSuccess.ShouldBeTrue();
        }

        // Drain the whole invalidation path: the shared queue may hold events
        // of earlier tests, and only a full drain guarantees this recipient's
        // stale mark landed.
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        while ((await CorePipelineFixture.RunRelayPassAsync(relay)).Published > 0)
        {
        }

        await using ServiceProvider contactConsent = fixture.BuildContactConsentWorkerProvider();
        while ((await CorePipelineFixture.RunContactsChangedPassAsync(contactConsent)).Received > 0)
        {
        }
    }
}
