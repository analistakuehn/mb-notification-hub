using System.Text.Json;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class CorePipelineErrorHandlingTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_permanently_invalid_message_is_discarded_with_a_trail_and_the_queue_empties()
    {
        var queueUrl = (await fixture.Sqs.GetQueueUrlAsync("core-operational")).QueueUrl;
        await fixture.Sqs.SendMessageAsync(queueUrl, "isto não é um envelope");

        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        SqsConsumePassResult pass = await CorePipelineFixture.RunCorePassAsync(worker, "core-operational");

        pass.Discarded.ShouldBe(1);
        pass.Failed.ShouldBe(0);

        // The queue emptied: the poison message was deleted, not retried.
        ReceiveMessageResponse remaining = await fixture.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 1,
        });
        (remaining.Messages ?? []).ShouldBeEmpty();

        // Zero business effect, one discard trail, one processed mark.
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "message.discarded")))
            .ShouldBeGreaterThanOrEqualTo(1);
        List<string> markIds = await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .Where(mark => mark.Consumer == "core-pipeline")
            .Select(mark => mark.MessageId)
            .ToListAsync());
        markIds.Count(id => id.StartsWith("discard:", StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(1);
    }

    [RequiresDockerFact]
    public async Task An_unknown_message_type_is_discarded_with_the_envelope_identity_in_the_trail()
    {
        var envelopeId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            messageId = envelopeId,
            type = "mystery.event",
            schemaVersion = 1,
            payload = new { },
        });
        var queueUrl = (await fixture.Sqs.GetQueueUrlAsync("core-operational")).QueueUrl;
        await fixture.Sqs.SendMessageAsync(queueUrl, body);

        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        SqsConsumePassResult pass = await CorePipelineFixture.RunCorePassAsync(worker, "core-operational");

        pass.Discarded.ShouldBe(1);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "message.discarded"
                && auditEvent.EntityId == envelopeId.ToString())))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_transient_failure_returns_the_message_and_a_later_pass_reprocesses_it()
    {
        // A published template without a published class policy: the pipeline
        // treats the missing policy as an operational failure, so the message
        // must return to the queue instead of ending rejected.
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        HttpClient producer = fixture.CreateProducerClient(
            "billing-service", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(application, templateKey, "transactional", recipientId),
            Guid.NewGuid().ToString("N"));
        accepted.EnsureSuccessStatusCode();
        JsonElement responseBody = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(
            responseBody.GetProperty("notificationId").GetString(), out Guid notificationId).ShouldBeTrue();

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);

        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        SqsConsumePassResult failing = await CorePipelineFixture.RunCorePassAsync(
            worker, "core-transactional");
        failing.Failed.ShouldBe(1);
        failing.Processed.ShouldBe(0);

        // The failure produced no business effect and no rejection trail.
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notificationId)
            .Select(candidate => candidate.Status)
            .SingleAsync()))
            .ShouldBe(NotificationStatuses.Accepted);

        // Once the operator publishes the policy, the returned message
        // processes end to end on a later pass.
        await CorePipelineApi.CreatePublishedPolicyAsync(fixture, application, "transactional");
        SqsConsumePassResult recovered = await ReceiveUntilProcessedAsync(worker);
        recovered.Processed.ShouldBe(1);
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notificationId)
            .Select(candidate => candidate.Status)
            .SingleAsync()))
            .ShouldBe(NotificationStatuses.Dispatched);
    }

    /// <summary>
    /// The failed message is invisible for the backoff window (one to two
    /// seconds in this fixture); poll until it returns.
    /// </summary>
    private static async Task<SqsConsumePassResult> ReceiveUntilProcessedAsync(ServiceProvider worker)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            SqsConsumePassResult pass = await CorePipelineFixture.RunCorePassAsync(
                worker, "core-transactional");
            if (pass.Processed > 0)
            {
                return pass;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("A mensagem devolvida não retornou dentro do prazo do teste.");
    }
}
