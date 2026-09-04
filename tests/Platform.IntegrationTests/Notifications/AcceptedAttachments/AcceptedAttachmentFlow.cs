using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
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

/// <summary>
/// Template and class policy of one plan, arranged once and reused by every
/// notification a suite accepts under it.
/// <para>
/// The recipient is deliberately not here. The deduplication window of the
/// policy is keyed on the application, the template and the recipient, so two
/// notifications sharing all three inside the window are one notification and
/// the second is rejected before it ever reaches a channel. A recipient of its
/// own per acceptance is what keeps an arrangement reusable.
/// </para>
/// </summary>
internal sealed record AttachmentArrangement(
    string Application,
    string TemplateKey,
    int DeviceCount);

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

    /// <summary>Type of the event that reports a notification that ended on failed.</summary>
    internal const string FailedEventType = "araia.notification.failed.v1";

    /// <summary>Type of the event that reports a notification the pipeline refused.</summary>
    internal const string RejectedEventType = "araia.notification.rejected.v1";

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
        => await AcceptAsync(
            fixture,
            await ArrangeAsync(fixture, plan, deviceCount, sensitiveVariables),
            attachmentCount);

    /// <summary>
    /// Seeds template, class policy, recipient and provider bindings for one
    /// plan, without accepting anything over them.
    /// <para>
    /// It is split from the acceptance so that a suite needing several
    /// notifications under one plan pays for the plan once. Each of those
    /// notifications still gets attachments of its own, because a set that two
    /// notifications shared would be held by two claims and a mutation aimed at
    /// one of them would land on the other.
    /// </para>
    /// </summary>
    internal static async Task<AttachmentArrangement> ArrangeAsync(
        AcceptedAttachmentFlowFixture fixture,
        (string Channel, string? Timeout)[] plan,
        int deviceCount = 1,
        string[]? sensitiveVariables = null)
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, NotificationClass, Purpose, sensitiveVariables);
        await DispatchApi.CreatePublishedPolicyAsync(fixture, application, NotificationClass, plan);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));
        return new AttachmentArrangement(application, templateKey, deviceCount);
    }

    /// <summary>
    /// Accepts one notification over freshly released attachments of its own,
    /// under an arrangement that already exists.
    /// <para>
    /// <paramref name="grantedAt"/> is the instant the whole lifecycle of those
    /// attachments is written at. Setting it into the past is how a set is
    /// accepted over a release that is already past its validity, which the
    /// claim admits because a claim reads the state and the release row and
    /// never the age of either.
    /// </para>
    /// </summary>
    internal static async Task<AttachedNotification> AcceptAsync(
        AcceptedAttachmentFlowFixture fixture,
        AttachmentArrangement arrangement,
        int attachmentCount,
        DateTimeOffset? grantedAt = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        // A recipient of this notification's own, so the deduplication window
        // of the policy never reads two notifications of one arrangement as
        // one notification asked for twice.
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(
            fixture, deviceCount: arrangement.DeviceCount);

        var attachments = new List<SeededAttachment>();
        for (var index = 0; index < attachmentCount; index++)
        {
            // With the bytes in custody, because every path below that reaches
            // a provider composes the message out of them. Seeding the record
            // alone would leave each of those sends failing on a coordinate
            // nobody ever wrote to, and the suites would be measuring an
            // environment with no content in it rather than the rule they name.
            attachments.Add(await ClaimableAttachments.ReleasedWithContentAsync(
                fixture,
                arrangement.Application,
                fileName: $"manifesto-{Guid.NewGuid():N}.pdf",
                mediaType: "application/pdf",
                length: 2048 + index,
                grantedAt: grantedAt));
        }

        HttpClient producer = fixture.CreateProducerClient(
            "attachment-producer", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            Body(arrangement.Application, arrangement.TemplateKey, recipientId, attachments),
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
        return new AttachedNotification(id, arrangement.Application, recipientId, attachments);
    }

    /// <summary>
    /// Accepts one notification under an existing arrangement, naming no
    /// attachment at all.
    /// <para>
    /// It exists to stand beside a notification that names some. Every refusal
    /// this flow measures is a negative, and a negative over a plan, a
    /// recipient and a template that could not have worked anyway proves
    /// nothing; the neighbour is the same arrangement, the same plan and the
    /// same channels, differing in the one thing under test.
    /// </para>
    /// </summary>
    internal static async Task<AttachedNotification> AcceptWithoutAttachmentsAsync(
        AcceptedAttachmentFlowFixture fixture,
        AttachmentArrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(
            fixture, deviceCount: arrangement.DeviceCount);

        HttpClient producer = fixture.CreateProducerClient(
            "attachment-producer", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            Body(arrangement.Application, arrangement.TemplateKey, recipientId, []),
            Guid.NewGuid().ToString("N"));
        if (!accepted.IsSuccessStatusCode)
        {
            accepted.StatusCode.ShouldBe(
                HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
        }

        JsonElement answer = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(answer.GetProperty("notificationId").GetString(), out Guid id)
            .ShouldBeTrue();
        return new AttachedNotification(id, arrangement.Application, recipientId, []);
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

    /// <summary>
    /// Runs the acceptance messages of several notifications through the
    /// pipeline and publishes what they wrote.
    /// <para>
    /// The loop stops on the attempts and never on a count of processed
    /// messages. One receive hands over whatever the queue felt like handing
    /// over, and this environment holds messages of neighbouring arrangements
    /// too, so a pass that processed as many messages as this call is waiting
    /// for may have processed none of them.
    /// </para>
    /// </summary>
    internal static async Task DispatchAllAsync(
        AcceptedAttachmentFlowFixture fixture,
        params Guid[] notifications)
    {
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        Guid[] pending = notifications;
        for (var pass = 0; pass < notifications.Length + 12 && pending.Length > 0; pass++)
        {
            await CorePipelineFixture.RunRelayPassAsync(relay);
            await CorePipelineFixture.RunCorePassAsync(core, CoreQueue);
            pending = await WithoutAttemptAsync(fixture, pending);
        }

        pending.ShouldBeEmpty(
            "cada notificação arranjada precisa do seu attempt antes de qualquer medição.");
        await CorePipelineFixture.RunRelayPassAsync(relay);
    }

    /// <summary>The notifications the pipeline has not queued an attempt for yet.</summary>
    private static async Task<Guid[]> WithoutAttemptAsync(
        AcceptedAttachmentFlowFixture fixture,
        Guid[] notifications)
    {
        List<Guid> attempted = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => notifications.Contains(attempt.NotificationId))
            .Select(attempt => attempt.NotificationId)
            .Distinct()
            .ToListAsync());
        return [.. notifications.Except(attempted)];
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

    /// <summary>
    /// The reason the published rejection event carries for one notification,
    /// read the same way and keyed the same way as the failure above.
    /// </summary>
    internal static async Task<string?> PublishedRejectionReasonAsync(
        AcceptedAttachmentFlowFixture fixture,
        string recipientId)
        => await PublishedReasonAsync(fixture, recipientId, RejectedEventType);

    /// <summary>
    /// The reason the published failure event carries for one notification,
    /// read off the outbox row the settlement wrote.
    /// <para>
    /// Keyed by the recipient because that is the key the event is published
    /// under, and every notification arranged here gets a recipient of its
    /// own, so the row belongs to this notification and to no neighbour.
    /// </para>
    /// </summary>
    internal static async Task<string?> PublishedFailureReasonAsync(
        AcceptedAttachmentFlowFixture fixture,
        string recipientId)
        => await PublishedReasonAsync(fixture, recipientId, FailedEventType);

    private static async Task<string?> PublishedReasonAsync(
        AcceptedAttachmentFlowFixture fixture,
        string recipientId,
        string eventType)
    {
        OutboxMessage published = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.EventType == eventType
                && message.MessageKey == recipientId));
        CloudEventParse parse = CloudEventParser.Parse(published.PayloadJson);
        parse.InvalidReason.ShouldBeNull();
        return parse.Event!.Data.GetProperty("reason").GetString();
    }

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
        // An empty receive answers with no list at all, not with an empty
        // one, and a queue this walk never wrote to is exactly that case.
        return [.. (received.Messages ?? []).Select(message => message.Body)];
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
        attachment.Revoke(DateTimeOffset.UtcNow).ShouldBe(AttachmentRevocationTransition.Applied);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Captures a second generation for one attachment and grants a release
    /// over it, which is the shape an explicit revalidation writes: a second
    /// row with an instant of its own, leaving the first one exactly as it was.
    /// <para>
    /// The handle the snapshot froze still names the first generation, so after
    /// this the content the notification was accepted with is no longer the
    /// content the release in force stands for.
    /// </para>
    /// </summary>
    internal static async Task SupersedeContentAsync(
        AcceptedAttachmentFlowFixture fixture,
        SeededAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext db = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var generation = AttachmentObjectGeneration.Capture(
            attachment.Id,
            AttachmentObjectLocator.FromStoredRow(
                "attachment-store",
                $"attachments/{Guid.NewGuid():N}",
                $"generation-{Guid.NewGuid():N}"),
            AttachmentContentProof.Sha256Of(
                SHA256.HashData(Encoding.UTF8.GetBytes($"superseded-{attachment.Id:N}")),
                attachment.Length),
            attachment.MediaType,
            now);
        db.ObjectGenerations.Add(generation);
        db.Releases.Add(AttachmentRelease.Grant(
            attachment.Id, generation.Id, now, TimeSpan.FromDays(30)));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Gives one attachment a reference nobody holds, so the reference the
    /// snapshot froze stops naming a row and the set is one member short.
    /// <para>
    /// Written straight onto the row because no path in the module renames an
    /// attachment: what is being arranged is a set the module can no longer
    /// assemble in full, and the reason it cannot is not the point.
    /// </para>
    /// </summary>
    internal static async Task ForgetReferenceAsync(
        AcceptedAttachmentFlowFixture fixture,
        Guid attachmentId)
    {
        var replacement = $"{AttachmentReference.Prefix}{Guid.CreateVersion7():N}";
        using IServiceScope scope = fixture.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Database
            .ExecuteSqlAsync($"""
                UPDATE attachmentmanagement.attachment
                SET reference = {replacement}
                WHERE id = {attachmentId}
                """);
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
        List<SeededAttachment> attachments)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["application"] = application,
            ["recipientId"] = recipientId,
            ["class"] = NotificationClass,
            ["templateKey"] = templateKey,
            ["locale"] = "pt-BR",
            ["variables"] = new { code = "123456" },
            ["ttlSeconds"] = 300,
        };

        // The member is omitted rather than sent empty: an empty list is a
        // malformed request, and what a neighbour without attachments has to
        // be is a request that named none.
        if (attachments.Count > 0)
        {
            body["attachments"] = attachments.Select(attachment => attachment.Reference).ToArray();
        }

        return body;
    }
}
