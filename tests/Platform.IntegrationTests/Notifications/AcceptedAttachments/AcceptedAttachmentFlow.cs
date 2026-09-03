using System.Net;
using System.Text.Json;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.Dispatching;

namespace NotificationHub.IntegrationTests.Notifications.AcceptedAttachments;

/// <summary>
/// The Core pipeline environment on stores and queues of its own.
/// <para>
/// Of its own because the suites here plant a corrupted document on a
/// notification row on purpose, and every path that meets one refuses by
/// throwing, which is how the queue holds the message instead of losing it. A
/// message held that way stays on the queue, and on the shared environment the
/// next neighbour to drain it would meet a corruption it never arranged and
/// count a failure it cannot explain.
/// </para>
/// </summary>
public sealed class AcceptedAttachmentFlowFixture : CorePipelineFixture
{
}

[CollectionDefinition(Name)]
public sealed class AcceptedAttachmentFlowCollectionDefinition
    : ICollectionFixture<AcceptedAttachmentFlowFixture>
{
    public const string Name = "accepted-attachment-flow";
}

/// <summary>One arranged notification and the set it was accepted over.</summary>
internal sealed record AttachedNotification(
    Guid NotificationId,
    string Application,
    string RecipientId,
    IReadOnlyList<SeededAttachment> Attachments)
{
    /// <summary>
    /// The values a copy of the manifest would carry wherever it was copied
    /// to. Each is unique to this notification, which is what lets a scan of a
    /// shared table say that the value it found belongs to this set and not to
    /// a neighbour's.
    /// <para>
    /// The media type and the length of an item are deliberately not here.
    /// Both are part of the document, but a common media type and a byte count
    /// are values a payload may carry for reasons of its own, so a scan that
    /// looked for them would report a copy where there is none.
    /// </para>
    /// </summary>
    internal IReadOnlyList<string> Sentinels =>
    [
        .. Attachments.SelectMany(attachment => new[]
        {
            attachment.Reference,
            attachment.ContentIdentity,
            attachment.Name,
        }),
    ];
}

/// <summary>
/// Arranging a notification that was accepted over a real set of attachments,
/// and driving it through the paths that read that set afterwards.
/// <para>
/// The set is claimed by the acceptance itself, over attachments seeded into
/// the owning module's own store, so the document on the row is the one the
/// production path wrote and never one a test composed.
/// </para>
/// </summary>
internal static class AcceptedAttachmentFlow
{
    internal const string CoreQueue = "core-transactional";
    internal const string EmailQueue = "dispatch-email-transactional";
    internal const string PushQueue = "dispatch-push-transactional";
    internal const string NotificationClass = "transactional";
    internal const string Purpose = "order-updates";

    /// <summary>
    /// A document the column accepts and the reader refuses: valid JSON, whole
    /// in every member, and versioned for a reader that does not exist. It is
    /// the corruption a row can plausibly reach without anything in this
    /// service having written it.
    /// </summary>
    internal static string UnknownVersionDocument(AttachedNotification accepted)
        => WholeDocument(accepted).Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":2",
            StringComparison.Ordinal);

    /// <summary>The document the acceptance itself wrote for this set.</summary>
    internal static string WholeDocument(AttachedNotification accepted)
        => AcceptedAttachmentManifest.Serialize(SetOf(accepted));

    /// <summary>
    /// Seeds template, class policy, recipient and provider bindings for one
    /// plan, and accepts a notification over freshly released attachments.
    /// </summary>
    internal static async Task<AttachedNotification> AcceptAsync(
        AcceptedAttachmentFlowFixture fixture,
        int attachmentCount,
        (string Channel, string? Timeout)[] plan,
        int deviceCount = 1,
        string[]? sensitiveVariables = null)
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, NotificationClass, Purpose, sensitiveVariables);
        await DispatchApi.CreatePublishedPolicyAsync(fixture, application, NotificationClass, plan);
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(
            fixture, deviceCount: deviceCount);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        var attachments = new List<SeededAttachment>();
        for (var index = 0; index < attachmentCount; index++)
        {
            attachments.Add(await ClaimableAttachments.ReleasedAsync(
                fixture,
                application,
                fileName: $"manifesto-{Guid.NewGuid():N}.pdf",
                mediaType: "application/pdf",
                length: 2048 + index));
        }

        HttpClient producer = fixture.CreateProducerClient(
            "attachment-producer", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            Body(application, templateKey, recipientId, attachments),
            Guid.NewGuid().ToString("N"));
        if (!accepted.IsSuccessStatusCode)
        {
            // The refusal body says which gate closed, and an acceptance that
            // fails to arrange is otherwise reported as a bare status code.
            accepted.StatusCode.ShouldBe(
                HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
        }

        JsonElement answer = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(answer.GetProperty("notificationId").GetString(), out Guid id)
            .ShouldBeTrue();
        return new AttachedNotification(id, application, recipientId, attachments);
    }

    /// <summary>Runs the acceptance message through the pipeline and publishes what it wrote.</summary>
    internal static async Task DispatchAsync(AcceptedAttachmentFlowFixture fixture)
    {
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, CoreQueue))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
    }

    /// <summary>Publishes whatever the last write left pending in the outbox.</summary>
    internal static async Task RelayAsync(AcceptedAttachmentFlowFixture fixture)
    {
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
    }

    /// <summary>
    /// Writes a document straight onto the row, which is the only way a test
    /// reaches this column: the mapping refuses a tracked change after the
    /// insert, and that refusal is a guard the acceptance path depends on.
    /// </summary>
    internal static async Task PlantAsync(
        AcceptedAttachmentFlowFixture fixture,
        Guid notificationId,
        string document)
        => await fixture.QueryNotificationsDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""
            UPDATE notifications.notification
            SET accepted_attachments = CAST({document} AS jsonb)
            WHERE id = {notificationId}
            """));

    internal static async Task<string?> StoredDocumentAsync(
        AcceptedAttachmentFlowFixture fixture,
        Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notificationId)
            .Select(candidate => candidate.AcceptedAttachmentsJson)
            .SingleAsync());

    /// <summary>The set the row carries, refusing anything the reader cannot read.</summary>
    internal static async Task<AcceptedAttachmentSet> StoredSetAsync(
        AcceptedAttachmentFlowFixture fixture,
        Guid notificationId)
        => AcceptedAttachmentManifest.Read(await StoredDocumentAsync(fixture, notificationId))
            .ShouldBeOfType<AcceptedManifestRead.Present>()
            .Accepted;

    internal static async Task<List<NotificationAttempt>> AttemptsAsync(
        AcceptedAttachmentFlowFixture fixture,
        Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(candidate => candidate.NotificationId == notificationId)
            .OrderBy(candidate => candidate.Sequence)
            .ToListAsync());

    internal static async Task<string> StatusAsync(
        AcceptedAttachmentFlowFixture fixture,
        Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notificationId)
            .Select(candidate => candidate.Status)
            .SingleAsync());

    /// <summary>
    /// The acceptance message, in the shape the ingestion writes it. Built
    /// here rather than read back from the outbox because what follows is
    /// about the pipeline and the row, and a message identity of the test's
    /// own is what lets the same trigger be handed to the pipeline twice.
    /// </summary>
    internal static MessageEnvelope AcceptedTrigger(Guid notificationId)
        => Envelope(CoreMessageProcessor.AcceptedMessageType, CoreQueue, new { notificationId });

    internal static MessageEnvelope DispatchTrigger(Guid notificationId, Guid attemptId)
        => Envelope(DispatchMessages.AttemptQueuedType, EmailQueue, new { notificationId, attemptId });

    internal static MessageEnvelope FallbackTrigger(Guid notificationId, Guid failedAttemptId)
        => Envelope(
            DispatchMessages.FallbackRequestedType,
            CoreQueue,
            new { notificationId, failedAttemptId });

    /// <summary>
    /// Every attempt row of one notification, serialized column by column.
    /// The whole row on purpose: a copy of the manifest put into a column
    /// nobody thought of is exactly the failure being looked for, and a scan
    /// that named the columns it knows about would not see it.
    /// </summary>
    internal static async Task<List<string>> AttemptRowsAsync(
        AcceptedAttachmentFlowFixture fixture,
        Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Database
            .SqlQuery<string>(
                $"""
                SELECT row_to_json(t)::text AS "Value"
                FROM notifications.notification_attempt t
                WHERE notification_id = {notificationId}
                """)
            .ToListAsync());

    /// <summary>
    /// Every outbox row this environment holds, serialized whole. The relay
    /// marks a row sent instead of deleting it, so the table is the complete
    /// record of every message body this service has published, and the
    /// environment is this collection's own, so every row here was written by
    /// the flow under test.
    /// </summary>
    internal static async Task<List<string>> OutboxRowsAsync(AcceptedAttachmentFlowFixture fixture)
    {
        const string AllRows = """
            SELECT row_to_json(t)::text AS "Value" FROM platform.outbox t
            """;
        return await fixture.QueryPlatformDbAsync(
            db => db.Database.SqlQueryRaw<string>(AllRows).ToListAsync());
    }

    /// <summary>
    /// The bodies one queue is holding, read without consuming them: the
    /// visibility timeout of zero puts every message straight back, so the
    /// consumer that owns them still finds them.
    /// </summary>
    internal static async Task<List<string>> PeekAsync(
        AcceptedAttachmentFlowFixture fixture,
        string queue)
    {
        GetQueueUrlResponse url = await fixture.Sqs.GetQueueUrlAsync(queue);
        ReceiveMessageResponse received = await fixture.Sqs.ReceiveMessageAsync(
            new ReceiveMessageRequest
            {
                QueueUrl = url.QueueUrl,
                MaxNumberOfMessages = 10,
                VisibilityTimeout = 0,
                WaitTimeSeconds = 1,
            });
        return [.. received.Messages.Select(message => message.Body)];
    }

    /// <summary>
    /// Takes back the release of one attachment through the aggregate's own
    /// transition, which is how the owning module records a revocation.
    /// </summary>
    internal static async Task RevokeAsync(AcceptedAttachmentFlowFixture fixture, Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext db = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        Attachment attachment = await db.Attachments.SingleAsync(
            candidate => candidate.Id == attachmentId);
        attachment.Revoke().ShouldBe(AttachmentRevocationTransition.Applied);
        await db.SaveChangesAsync();
    }

    /// <summary>The state the owning module now holds for one attachment.</summary>
    internal static async Task<string> AttachmentStateAsync(
        AcceptedAttachmentFlowFixture fixture,
        Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Attachments
            .AsNoTracking()
            .Where(candidate => candidate.Id == attachmentId)
            .Select(candidate => candidate.State)
            .SingleAsync();
    }

    private static AcceptedAttachmentSet SetOf(AttachedNotification accepted)
        => AcceptedAttachmentSet.Of(accepted.Attachments.Select(attachment => new AcceptedAttachment
        {
            Reference = attachment.Reference,
            ContentIdentity = attachment.ContentIdentity,
            Name = attachment.Name,
            MediaType = attachment.MediaType,
            Length = attachment.Length,
        }));

    private static MessageEnvelope Envelope(string type, string queue, object payload)
        => new()
        {
            MessageId = Guid.CreateVersion7(),
            Type = type,
            SchemaVersion = DispatchMessages.SchemaVersion,
            OccurredAt = DateTimeOffset.UtcNow,
            PriorityClass = NotificationClass,
            SourceQueue = queue,
            Payload = JsonSerializer.SerializeToElement(payload),
        };

    private static Dictionary<string, object?> Body(
        string application,
        string templateKey,
        string recipientId,
        IReadOnlyList<SeededAttachment> attachments)
        => new(StringComparer.Ordinal)
        {
            ["application"] = application,
            ["recipientId"] = recipientId,
            ["class"] = NotificationClass,
            ["templateKey"] = templateKey,
            ["locale"] = "pt-BR",
            ["variables"] = new { code = "123456" },
            ["ttlSeconds"] = 300,
            ["attachments"] = attachments.Select(attachment => attachment.Reference).ToArray(),
        };
}
