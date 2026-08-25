using System.Text.Json;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.KillSwitch;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class ApplicationKillSwitchCoreTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task An_active_application_switch_holds_core_work_before_any_progression()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture,
            application,
            NotificationClasses.Transactional,
            "order-updates");
        var recipientId = $"cus_{Guid.NewGuid():N}";
        HttpClient producer = fixture.CreateProducerClient(
            "billing-service",
            NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(
                application,
                templateKey,
                NotificationClasses.Transactional,
                recipientId),
            Guid.NewGuid().ToString("N"));
        accepted.EnsureSuccessStatusCode();
        JsonElement response = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(
            response.GetProperty("notificationId").GetString(),
            out Guid notificationId).ShouldBeTrue();

        OutboxMessage acceptedMessage = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.MessageKey == recipientId
                && message.EventType == CoreMessageProcessor.AcceptedMessageType));
        await ActivateApplicationSwitchAsync(application);
        var queueUrl = (await fixture.Sqs.GetQueueUrlAsync(acceptedMessage.Destination)).QueueUrl;
        await fixture.Sqs.SendMessageAsync(queueUrl, acceptedMessage.PayloadJson);

        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        SqsConsumePassResult pass = await CorePipelineFixture.RunCorePassAsync(
            worker,
            acceptedMessage.Destination);

        pass.Processed.ShouldBe(1);
        pass.Failed.ShouldBe(0);
        pass.Postponed.ShouldBe(0);
        var state = await fixture.QueryNotificationsDbAsync(async db => new
        {
            Notification = await db.Notifications
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == notificationId),
            Attempts = await db.NotificationAttempts
                .AsNoTracking()
                .CountAsync(attempt => attempt.NotificationId == notificationId),
            Evaluations = await db.PolicyEvaluations
                .AsNoTracking()
                .CountAsync(evaluation => evaluation.NotificationId == notificationId),
            Hold = await db.KillSwitchHolds
                .AsNoTracking()
                .SingleAsync(hold => hold.WorkKind == KillSwitchWorkKinds.Core
                    && hold.WorkId == $"core:{notificationId:N}"),
        });
        state.Notification.Status.ShouldBe(NotificationStatuses.Accepted);
        state.Attempts.ShouldBe(0);
        state.Evaluations.ShouldBe(0);
        state.Hold.Scope.ShouldBe(KillSwitchScopes.Application);
        state.Hold.Key.ShouldBe(application);
        state.Hold.Destination.ShouldBe(acceptedMessage.Destination);
        state.Hold.ExpiresAt.ShouldBe(state.Notification.ExpiresAt);
        state.Hold.ReleasedAt.ShouldBeNull();
        using var claimCheck = JsonDocument.Parse(state.Hold.PayloadJson);
        claimCheck.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ShouldBe(["notificationId"]);
        claimCheck.RootElement.GetProperty("notificationId").GetGuid().ShouldBe(notificationId);

        (await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages
            .AsNoTracking()
            .CountAsync(mark => mark.Consumer == PipelineCommitWriter.ConsumerName
                && mark.MessageId.Contains(notificationId.ToString("N")))))
            .ShouldBe(0);
        (await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId)))
            .ShouldBe(1);

        ReceiveMessageResponse remaining = await fixture.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 1,
        });
        (remaining.Messages ?? []).ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task An_expired_fallback_reaches_terminal_handling_while_the_switch_is_active()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var notification = Notification.Accept(new NotificationDraft
        {
            Application = CorePipelineApi.NewApplication(),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RecipientId = $"cus_{Guid.NewGuid():N}",
            Class = NotificationClasses.Transactional,
            TemplateKey = $"template-{Guid.NewGuid():N}",
            TemplateVersion = 1,
            VariablesMaskedJson = "{}",
            RequestedBy = "application-kill-switch-core-tests",
            TtlSeconds = 60,
            AcceptedAt = now.AddMinutes(-2),
        });
        notification.MarkDispatched(policyVersion: 1);
        var failedAttempt = NotificationAttempt.Queue(new NotificationAttemptDraft
        {
            NotificationId = notification.Id,
            Sequence = 1,
            Channel = "email",
            RenderedContentEncrypted = [1],
            ContentHashFull = "full-hash",
            ContentHashMasked = "masked-hash",
            QueuedAt = now.AddMinutes(-2),
        });
        await fixture.QueryNotificationsDbAsync(async db =>
        {
            db.Notifications.Add(notification);
            db.NotificationAttempts.Add(failedAttempt);
            return await db.SaveChangesAsync();
        });
        await ActivateApplicationSwitchAsync(notification.Application);
        var destination = "core-transactional";
        var queueUrl = (await fixture.Sqs.GetQueueUrlAsync(destination)).QueueUrl;
        await fixture.Sqs.SendMessageAsync(
            queueUrl,
            JsonSerializer.Serialize(new
            {
                messageId = Guid.CreateVersion7(),
                type = DispatchMessages.FallbackRequestedType,
                schemaVersion = CoreMessageProcessor.SupportedSchemaVersion,
                occurredAt = now,
                priorityClass = notification.Class,
                payload = new
                {
                    notificationId = notification.Id,
                    failedAttemptId = failedAttempt.Id,
                },
            }));

        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        SqsConsumePassResult pass = await CorePipelineFixture.RunCorePassAsync(worker, destination);

        pass.Processed.ShouldBe(1);
        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notification.Id)
            .Select(candidate => candidate.Status)
            .SingleAsync()))
            .ShouldBe(NotificationStatuses.Expired);
        (await fixture.QueryNotificationsDbAsync(db => db.KillSwitchHolds
            .AsNoTracking()
            .CountAsync(hold => hold.WorkKind == KillSwitchWorkKinds.Fallback
                && hold.WorkId == $"fallback:{failedAttempt.Id:N}"
                && hold.ReleasedAt == null)))
            .ShouldBe(0);
    }

    private async Task ActivateApplicationSwitchAsync(string application)
        => await fixture.QueryNotificationsDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO notifications.kill_switch
                (scope, key, state, version, actor, second_actor, updated_at)
            VALUES
                ({KillSwitchScopes.Application}, {application}, {KillSwitchStates.Active},
                 1, {"tdd-application-core"}, NULL, {DateTimeOffset.UtcNow})
            """));
}
