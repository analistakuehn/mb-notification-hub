using System.Net;
using System.Text.Json;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class CorePipelineHappyPathTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task An_authentication_message_crosses_the_whole_pipeline_into_a_queued_attempt()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, var templateVersion) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication", sensitiveVariables: ["code"]);
        var policyVersion = await CorePipelineApi.CreatePublishedPolicyAsync(
            fixture, application, "critical");
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        HttpClient producer = fixture.CreateProducerClient("auth-service", NotificationsApi.SendCritical);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(application, templateKey, "critical", recipientId),
            Guid.NewGuid().ToString("N"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Guid notificationId = await ReadNotificationIdAsync(accepted);

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);

        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        SqsConsumePassResult pass = await CorePipelineFixture.RunCorePassAsync(worker, "core-auth");
        pass.Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The notification landed on dispatched with the ruling policy stamped.
        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Dispatched);
        notification.PolicyVersion.ShouldBe(policyVersion);
        notification.TemplateVersion.ShouldBe(templateVersion);
        notification.VariablesEncrypted.ShouldNotBeNull();

        // Attempt #1 queued on the first plan step, fallback deadline stamped
        // at enqueue time, provider untouched, push targeting device tokens.
        NotificationAttempt attempt = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.NotificationId == notificationId));
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Queued);
        attempt.Sequence.ShouldBe(1);
        attempt.Channel.ShouldBe("push");
        attempt.ContactPointId.ShouldBeNull();
        attempt.ProviderKey.ShouldBeNull();
        attempt.FallbackDeadline.ShouldBe(attempt.CreatedAt.AddSeconds(30));

        // The stored content is the sealed full render: decryptable with the
        // application-scoped key, carrying the real value, while the two
        // hashes prove the masked form differs from the full one.
        IEnvelopeCipher cipher = worker.GetRequiredService<IEnvelopeCipher>();
        var plaintext = await cipher.DecryptAsync(
            application, attempt.RenderedContentEncrypted, CancellationToken.None);
        using JsonDocument rendered = JsonDocument.Parse(plaintext);
        rendered.RootElement.GetProperty("channel").GetString().ShouldBe("push");
        rendered.RootElement.GetProperty("body").GetString()!.ShouldContain("123456");
        attempt.ContentHashFull.ShouldNotBe(attempt.ContentHashMasked);

        // One policy_evaluation row per rule of the fixed v1 set, each with
        // its decision. Version-7 ids do not order within one millisecond and
        // the query carries no ordering, so the oracle looks each rule up by
        // name: a comparison of the whole dictionary would read as a sequence
        // and fail on a permutation that means nothing.
        List<PolicyEvaluation> evaluations = await fixture.QueryNotificationsDbAsync(db => db.PolicyEvaluations
            .AsNoTracking()
            .Where(evaluation => evaluation.NotificationId == notificationId)
            .ToListAsync());
        evaluations.Count.ShouldBe(4);
        Dictionary<string, string> resultByRule = evaluations.ToDictionary(
            evaluation => evaluation.Rule,
            evaluation => evaluation.Result);
        resultByRule["ConsentGate"].ShouldBe("allow");
        resultByRule["QuietHours"].ShouldBe("allow");
        resultByRule["DedupeWindow"].ShouldBe("allow");
        resultByRule["ChannelSelection"].ShouldBe("filter-channels");

        // The outbox row targets the auth dispatch queue with the claim check.
        var outboxPayload = await fixture.QueryPlatformDbAsync(db => db.Database
            .SqlQuery<string>(
                $"""
                SELECT payload::text AS "Value" FROM platform.outbox
                WHERE destination = 'dispatch-push-auth' AND payload->'payload'->>'notificationId' = {notificationId.ToString()}
                """)
            .SingleAsync());
        using JsonDocument dispatchEnvelope = JsonDocument.Parse(outboxPayload);
        dispatchEnvelope.RootElement.GetProperty("type").GetString().ShouldBe("attempt.queued");
        dispatchEnvelope.RootElement.GetProperty("payload").GetProperty("attemptId").GetGuid()
            .ShouldBe(attempt.Id);

        // A relay instance restricted to the auth band publishes it: the
        // dispatch-*-auth destination classifies into the auth band.
        await using ServiceProvider authRelay = fixture.BuildRelayProvider(new Dictionary<string, string?>
        {
            ["Platform:Messaging:Relay:Bands:0"] = "auth",
        });
        (await CorePipelineFixture.RunRelayPassAsync(authRelay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        List<Message> dispatched = await ReceiveAllAsync("dispatch-push-auth", expected: 1);
        dispatched.ShouldContain(message =>
            message.Body.Contains(attempt.Id.ToString(), StringComparison.OrdinalIgnoreCase));

        // The audit trail carries the dispatch decision and the dedupe mark exists.
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "notification.dispatched"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(1);
        List<string> markIds = await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .Where(mark => mark.Consumer == "core-pipeline")
            .Select(mark => mark.MessageId)
            .ToListAsync());
        markIds.Count(id => id.EndsWith(notificationId.ToString("N"), StringComparison.Ordinal)).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_redelivered_core_message_lands_on_the_dedupe_mark_with_zero_repeated_effect()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await CorePipelineApi.CreatePublishedPolicyAsync(fixture, application, "critical");
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        HttpClient producer = fixture.CreateProducerClient("auth-service", NotificationsApi.SendCritical);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(application, templateKey, "critical", recipientId),
            Guid.NewGuid().ToString("N"));
        Guid notificationId = await ReadNotificationIdAsync(accepted);

        // Keep the exact stored payload so the redelivery is byte-identical.
        var corePayload = await fixture.QueryPlatformDbAsync(db => db.Database
            .SqlQuery<string>(
                $"""
                SELECT payload::text AS "Value" FROM platform.outbox
                WHERE destination = 'core-auth' AND payload->'payload'->>'notificationId' = {notificationId.ToString()}
                """)
            .SingleAsync());

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(worker, "core-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The broker redelivers the same message (relay double-publish).
        var queueUrl = (await fixture.Sqs.GetQueueUrlAsync("core-auth")).QueueUrl;
        await fixture.Sqs.SendMessageAsync(queueUrl, corePayload);
        SqsConsumePassResult secondPass = await CorePipelineFixture.RunCorePassAsync(worker, "core-auth");
        secondPass.Duplicates.ShouldBe(1);
        secondPass.Processed.ShouldBe(0);

        // One attempt, one dedupe mark, and the duplicate left its own trail.
        (await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .CountAsync(attempt => attempt.NotificationId == notificationId)))
            .ShouldBe(1);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "notification.duplicate"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(1);
    }

    private static async Task<Guid> ReadNotificationIdAsync(HttpResponseMessage response)
    {
        JsonElement body = await NotificationsApi.ReadJsonAsync(response);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString(), out Guid id).ShouldBeTrue();
        return id;
    }

    private async Task<List<Message>> ReceiveAllAsync(string queueName, int expected)
    {
        var queueUrl = (await fixture.Sqs.GetQueueUrlAsync(queueName)).QueueUrl;
        var received = new List<Message>();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (received.Count < expected && DateTimeOffset.UtcNow < deadline)
        {
            ReceiveMessageResponse response = await fixture.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
            });
            foreach (Message message in response.Messages ?? [])
            {
                received.Add(message);
                await fixture.Sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
            }
        }

        return received;
    }
}
