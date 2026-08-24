using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class RequestNotificationEndpointTests(NotificationsApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Accepting_a_notification_persists_notification_idempotency_outbox_and_audit_together()
    {
        (var templateKey, var version) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-accept", NotificationsApi.SendTransactional);
        var idempotencyKey = $"accept-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId),
            idempotencyKey);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement body = await NotificationsApi.ReadJsonAsync(response);
        var publicId = body.GetProperty("notificationId").GetString()!;
        publicId.ShouldStartWith("ntf_");
        body.GetProperty("status").GetString().ShouldBe("accepted");
        response.Headers.Location!.ToString().ShouldBe($"/v1/notifications/{publicId}");

        NotificationId.TryParse(publicId, out Guid storedId).ShouldBeTrue();

        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == storedId));
        notification.Application.ShouldBe(NotificationsApi.Application);
        notification.RecipientId.ShouldBe(recipientId);
        notification.Class.ShouldBe("transactional");
        notification.TemplateKey.ShouldBe(templateKey);
        notification.TemplateVersion.ShouldBe(version);
        notification.PolicyVersion.ShouldBeNull();
        notification.Status.ShouldBe("accepted");
        notification.RequestedBy.ShouldBe("producer-accept");
        notification.ExpiresAt.ShouldBe(notification.CreatedAt.AddSeconds(300));

        IdempotencyRegistration registration = await fixture.QueryNotificationsDbAsync(db =>
            db.IdempotencyRegistrations
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Application == NotificationsApi.Application
                    && candidate.IdempotencyKey == idempotencyKey));
        registration.NotificationId.ShouldBe(storedId);
        registration.PayloadHash.Length.ShouldBe(64);

        OutboxMessage outboxMessage = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(candidate => candidate.MessageKey == recipientId));
        outboxMessage.Destination.ShouldBe("core-transactional");
        outboxMessage.EventType.ShouldBe("notification.accepted");
        outboxMessage.PriorityClass.ShouldBe("transactional");
        outboxMessage.SentAt.ShouldBeNull();
        using JsonDocument envelope = JsonDocument.Parse(outboxMessage.PayloadJson);
        envelope.RootElement.GetProperty("type").GetString().ShouldBe("notification.accepted");
        envelope.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        envelope.RootElement.GetProperty("priorityClass").GetString().ShouldBe("transactional");
        envelope.RootElement.GetProperty("payload").GetProperty("notificationId").GetGuid().ShouldBe(storedId);

        var auditCount = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(candidate => candidate.Action == "notification.accepted"
                && candidate.EntityId == storedId.ToString()
                && candidate.ActorType == "producer"
                && candidate.ActorId == "producer-accept"));
        auditCount.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_disabled_producer_records_exactly_one_rejection_trail_and_event_without_acceptance()
    {
        var producerId = $"producer-disabled-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = $"producer-disabled-{Guid.NewGuid():N}";
        HttpClient admin = fixture.CreatePlatformAdminClient("admin-producer-disabled");
        HttpResponseMessage activated = await admin.PutAsJsonAsync(
            $"/v1/notifications/kill-switch/producer/{producerId}",
            new { active = true });
        activated.StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpClient producer = fixture.CreateProducerClient(
            producerId,
            NotificationsApi.SendTransactional);

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody("template-is-never-read", recipientId: recipientId),
            idempotencyKey);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await NotificationsApi.ReadJsonAsync(response))
            .GetProperty("type").GetString().ShouldBe("producer-disabled");

        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.IdempotencyKey == idempotencyKey)))
            .ShouldBe(0);
        (await fixture.QueryNotificationsDbAsync(db => db.IdempotencyRegistrations
            .AsNoTracking()
            .CountAsync(registration => registration.IdempotencyKey == idempotencyKey)))
            .ShouldBe(0);

        var entityId = $"{NotificationsApi.Application}:{idempotencyKey}";
        List<AuditEvent> trails = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == entityId)
            .ToListAsync());
        trails.Count.ShouldBe(1);
        trails[0].Action.ShouldBe("notification.rejected_at_ingress");
        trails[0].ActorId.ShouldBe(producerId);
        using (JsonDocument details = JsonDocument.Parse(trails[0].DetailsJson))
        {
            details.RootElement.GetProperty("reason").GetString().ShouldBe("producer-disabled");
            details.RootElement.GetProperty("source").GetString().ShouldBe("rest");
        }

        List<OutboxMessage> events = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.MessageKey == recipientId)
            .ToListAsync());
        events.Count.ShouldBe(1);
        events[0].Destination.ShouldBe("notifications.events.v1");
        events[0].EventType.ShouldBe("araia.notification.rejected.v1");
        CloudEventParse parse = CloudEventParser.Parse(events[0].PayloadJson);
        parse.InvalidReason.ShouldBeNull();
        parse.Event!.Data.GetProperty("reason").GetString().ShouldBe("producer-disabled");
        parse.Event.Data.GetProperty("idempotencyKey").GetString().ShouldBe(idempotencyKey);
    }

    [RequiresDockerFact]
    public async Task The_variables_are_stored_masked_and_the_envelope_decrypts_to_the_original_object()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(
            fixture, sensitiveVariables: ["code"]);
        HttpClient producer = fixture.CreateProducerClient("producer-pii", NotificationsApi.SendTransactional);
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(
                templateKey,
                recipientId: recipientId,
                variables: new { orderId = "ord-1", code = "482913" }),
            $"pii-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement body = await NotificationsApi.ReadJsonAsync(response);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString(), out Guid storedId).ShouldBeTrue();

        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == storedId));
        // jsonb re-serializes on read, so the projection is compared as JSON
        // values, never as raw text.
        using JsonDocument maskedProjection = JsonDocument.Parse(notification.VariablesMaskedJson);
        maskedProjection.RootElement.GetProperty("code").GetString().ShouldBe("***");
        maskedProjection.RootElement.GetProperty("orderId").GetString().ShouldBe("ord-1");
        maskedProjection.RootElement.EnumerateObject().Count().ShouldBe(2);
        notification.VariablesEncrypted.ShouldNotBeNull();

        var decrypted = await fixture.UsingScopeAsync(async services =>
        {
            IEnvelopeCipher cipher = services.GetRequiredService<IEnvelopeCipher>();
            var plaintext = await cipher.DecryptAsync(
                NotificationsApi.Application, notification.VariablesEncrypted!, CancellationToken.None);
            return Encoding.UTF8.GetString(plaintext);
        });
        decrypted.ShouldBe("""{"code":"482913","orderId":"ord-1"}""");
    }

    [RequiresDockerFact]
    public async Task An_authentication_purpose_template_routes_the_outbox_message_to_the_auth_queue()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(
            fixture, purpose: "authentication");
        HttpClient producer = fixture.CreateProducerClient("producer-auth", NotificationsApi.SendTransactional);
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId),
            $"auth-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        OutboxMessage outboxMessage = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(candidate => candidate.MessageKey == recipientId));
        outboxMessage.Destination.ShouldBe("core-auth");
    }

    /// <summary>
    /// The shape refusal belongs to the use case, on both transports. The 400
    /// keeps the per-field report the framework published and gains the catalog
    /// code plus the trail that the bus path always had.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_body_that_fails_the_shape_rules_is_payload_invalid_with_the_field_report_and_a_trail()
    {
        HttpClient producer = fixture.CreateProducerClient("producer-shape", NotificationsApi.SendTransactional);
        var idempotencyKey = $"shape-{Guid.NewGuid():N}";

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody("any.key", @class: "banana", ttlSeconds: 0),
            idempotencyKey);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("payload-invalid");
        problem.GetProperty("status").GetInt32().ShouldBe(400);

        // The dictionary is the validator's own, unchanged: one entry per
        // failed rule, keyed by property name, with the same messages.
        JsonElement errors = problem.GetProperty("errors");
        errors.EnumerateObject()
            .Select(entry => entry.Name)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["Class", "TtlSeconds"]);
        errors.GetProperty("TtlSeconds")[0].GetString()
            .ShouldBe("'Ttl Seconds' must be greater than '0'.");
        errors.GetProperty("Class")[0].GetString()
            .ShouldBe("Class must be one of: critical, transactional, operational.");

        var entityId = $"{NotificationsApi.Application}:{idempotencyKey}";
        List<AuditEvent> rejections = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(candidate => candidate.Action == "notification.rejected_at_ingress"
                && candidate.EntityId == entityId)
            .ToListAsync());
        rejections.Count.ShouldBe(1);
        rejections[0].DetailsJson.ShouldContain("payload-invalid");
    }

    /// <summary>
    /// Accepted consequence of moving the shape check into the use case: the
    /// key is answered first, which is the right order, because the trail needs
    /// it for the identity of the entity it records.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_invalid_body_without_the_idempotency_key_is_answered_for_the_missing_key_first()
    {
        HttpClient producer = fixture.CreateProducerClient(
            "producer-shape-nokey", NotificationsApi.SendTransactional);

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody("any.key", @class: "banana", ttlSeconds: 0),
            idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("idempotency-key-required");
    }

    [RequiresDockerFact]
    public async Task A_request_without_a_locale_is_accepted()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-nolocale", NotificationsApi.SendTransactional);
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId, locale: null),
            $"nolocale-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.RecipientId == recipientId)))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_missing_idempotency_key_header_is_a_bad_request_problem()
    {
        HttpClient producer = fixture.CreateProducerClient("producer-nokey", NotificationsApi.SendTransactional);

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer, NotificationsApi.RequestBody("any.key"), idempotencyKey: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("idempotency-key-required");
    }

    [RequiresDockerFact]
    public async Task A_class_the_token_does_not_cover_is_forbidden_and_audited()
    {
        HttpClient producer = fixture.CreateProducerClient("producer-op-only", NotificationsApi.SendOperational);
        var idempotencyKey = $"forbidden-{Guid.NewGuid():N}";

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody("any.key", @class: "transactional"),
            idempotencyKey);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("class-not-allowed-for-principal");

        var entityId = $"{NotificationsApi.Application}:{idempotencyKey}";
        List<AuditEvent> rejections = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(candidate => candidate.Action == "notification.rejected_at_ingress"
                && candidate.EntityId == entityId)
            .ToListAsync());
        rejections.Count.ShouldBe(1);
        rejections[0].DetailsJson.ShouldContain("class-not-allowed-for-principal");
    }

    [RequiresDockerFact]
    public async Task A_token_without_any_send_role_never_reaches_the_use_case()
    {
        HttpClient client = fixture.CreateAuthorClient("not-a-producer");

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            client, NotificationsApi.RequestBody("any.key"), $"role-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [RequiresDockerFact]
    public async Task An_unknown_template_is_unprocessable()
    {
        HttpClient producer = fixture.CreateProducerClient("producer-unknown", NotificationsApi.SendTransactional);

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody($"missing.{Guid.NewGuid():N}"),
            $"unknown-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-not-found");
    }

    [RequiresDockerFact]
    public async Task A_deprecated_template_is_rejected_with_the_catalog_reason()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient publisher = fixture.CreatePublisherClient("template-publisher");
        (await publisher.PostAsJsonAsync(
                $"/v1/templates/{templateKey}/deprecate",
                new { reason = "substituído pela campanha nova" }))
            .EnsureSuccessStatusCode();
        HttpClient producer = fixture.CreateProducerClient("producer-deprecated", NotificationsApi.SendTransactional);

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer, NotificationsApi.RequestBody(templateKey), $"deprecated-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-deprecated");
    }

    [RequiresDockerFact]
    public async Task Variables_failing_the_published_schema_are_rejected_with_the_report()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-badvars", NotificationsApi.SendTransactional);

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, variables: new { unexpected = "x" }),
            $"badvars-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-variables-invalid");
        problem.GetProperty("checks").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [RequiresDockerFact]
    public async Task A_class_different_from_the_template_class_is_rejected()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient("producer-mismatch", NotificationsApi.SendCritical);

        HttpResponseMessage response = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, @class: "critical"),
            $"mismatch-{Guid.NewGuid():N}");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-class-mismatch");
    }
}
