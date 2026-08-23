using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class RequestNotificationTransactionTests(NotificationsApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_failing_audit_append_rolls_back_every_row_of_the_acceptance()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuditTrail>();
                services.AddSingleton<IAuditTrail>(new FailingAuditTrail());
            }));
        HttpClient producer = fixture.CreateProducerClient(
            host, "producer-rollback", NotificationsApi.SendTransactional);
        var idempotencyKey = $"rollback-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var body = NotificationsApi.RequestBody(templateKey, recipientId: recipientId);

        HttpResponseMessage failed = await NotificationsApi.PostNotificationAsync(
            producer, body, idempotencyKey);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        // Nothing committed: no notification, no registration, no outbox row.
        var notifications = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.RecipientId == recipientId));
        notifications.ShouldBe(0);
        var registrations = await fixture.QueryNotificationsDbAsync(db => db.IdempotencyRegistrations
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey));
        registrations.ShouldBe(0);
        var outboxMessages = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(candidate => candidate.MessageKey == recipientId));
        outboxMessages.ShouldBe(0);

        // The fast path learned nothing either: the same key on the healthy
        // host is a first acceptance, not a replay.
        HttpClient healthyProducer = fixture.CreateProducerClient(
            "producer-rollback", NotificationsApi.SendTransactional);
        HttpResponseMessage retried = await NotificationsApi.PostNotificationAsync(
            healthyProducer, body, idempotencyKey);
        retried.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private sealed class FailingAuditTrail : IAuditTrail
    {
        public Task AppendAsync(DbTransaction transaction, AuditEntry entry, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Falha induzida no append da trilha de auditoria.");

        public Task RecordApprovalAsync(DbTransaction transaction, ApprovalGrant grant, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Falha induzida no registro de aprovação.");
    }
}
