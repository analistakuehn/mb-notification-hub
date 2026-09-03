using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// What the acceptance actually sends to PostgreSQL, and what it never sends
/// afterwards.
/// <para>
/// The claim that the snapshot travels in the insert of the acceptance is a
/// claim about emitted statements, so it is read from the statements
/// themselves rather than from the code that composes them. The same goes for
/// its opposite: no statement after the insert may name the column, and the
/// only way to know is to make the row move on and read what was sent.
/// </para>
/// <para>
/// The database is this class's own, because the planted documents here are
/// deliberate corruptions of a stored row and nothing else in the suite should
/// ever meet them.
/// </para>
/// </summary>
[Collection(QueryPlanCollectionDefinition.Name)]
public sealed class AcceptedAttachmentSnapshotTests : IAsyncLifetime
{
    private const string SnapshotColumn = "accepted_attachments";

    private const string EmptyJsonObject = "{}";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await _postgres.StartAsync();
        await using NotificationsDbContext db = CreateContext();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.DisposeAsync();

    /// <summary>
    /// The snapshot is part of the insert that accepts the notification, and
    /// no statement of its own carries it.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_snapshot_travels_in_the_insert_of_the_acceptance()
    {
        var document = AcceptedAttachmentManifest.Serialize(Set("att_insert_one", "att_insert_two"));
        var capture = new StatementCapture();
        Notification accepted = Accepted();
        accepted.FreezeAcceptedAttachments(document);

        await using (NotificationsDbContext db = CreateContext(capture))
        {
            db.Notifications.Add(accepted);
            await db.SaveChangesAsync();
        }

        IReadOnlyList<string> writes = capture.Writes;
        writes.Count.ShouldBe(1, capture.Report());
        writes[0].Contains("INSERT INTO notifications.notification", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue(capture.Report());
        writes[0].Contains(SnapshotColumn, StringComparison.Ordinal).ShouldBeTrue(capture.Report());

        // The text names the column; the parameters are what actually carried
        // the document, and a statement that named the column while sending
        // something else would pass the assertion above on its own.
        capture.Parameters.ShouldContain(value => IsSameDocument(value, document), capture.Report());

        AcceptedAttachmentSet stored = await ShouldReadBackAsync(accepted.Id);
        stored.Select(item => item.Reference).ShouldBe(["att_insert_one", "att_insert_two"]);
    }

    /// <summary>
    /// A later transition of the same row writes what it changes and never the
    /// snapshot. The admitted plan is asserted alongside it on purpose: it is
    /// the other document on this row, it does move in this transition, and
    /// without it a capture that recorded nothing at all would satisfy the
    /// absence being claimed.
    /// </summary>
    [RequiresDockerFact]
    public async Task No_statement_after_the_acceptance_names_the_snapshot()
    {
        Notification accepted = await StoreAcceptedAsync("att_update_one");
        var capture = new StatementCapture();

        await using (NotificationsDbContext db = CreateContext(capture))
        {
            Notification tracked = await db.Notifications.SingleAsync(
                notification => notification.Id == accepted.Id);
            tracked.MarkDispatched(policyVersion: 7, admittedPlanJson: """[{"channel":"email"}]""");
            await db.SaveChangesAsync();
        }

        IReadOnlyList<string> writes = capture.Writes;
        writes.Count.ShouldBe(1, capture.Report());
        writes[0].Contains("UPDATE notifications.notification", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue(capture.Report());
        writes[0].Contains("admitted_plan", StringComparison.Ordinal).ShouldBeTrue(capture.Report());
        writes[0].Contains("status", StringComparison.Ordinal).ShouldBeTrue(capture.Report());
        writes[0].Contains(SnapshotColumn, StringComparison.Ordinal).ShouldBeFalse(capture.Report());

        AcceptedAttachmentSet stored = await ShouldReadBackAsync(accepted.Id);
        stored.Single().Reference.ShouldBe("att_update_one");
    }

    /// <summary>
    /// Changing the snapshot of a row that already exists fails on the model
    /// guard, and it fails before a statement is built. The zero below is the
    /// whole point: a guard that refused after the update had been sent would
    /// be a guard over an already durable change.
    /// </summary>
    [RequiresDockerFact]
    public async Task Changing_the_snapshot_after_the_row_exists_fails_before_any_statement()
    {
        Notification accepted = await StoreAcceptedAsync("att_frozen_one");
        var other = AcceptedAttachmentManifest.Serialize(Set("att_frozen_other"));
        var capture = new StatementCapture();

        await using (NotificationsDbContext db = CreateContext(capture))
        {
            Notification tracked = await db.Notifications.SingleAsync(
                notification => notification.Id == accepted.Id);

            // The capture is emptied only after the row is loaded, so the
            // count below covers the mutation and the save and nothing else.
            capture.Reset();

            InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(
                async () =>
                {
                    db.Entry(tracked).Property(notification => notification.AcceptedAttachmentsJson)
                        .CurrentValue = other;
                    await db.SaveChangesAsync();
                });

            refusal.Message.Contains("read-only after it has been saved", StringComparison.Ordinal)
                .ShouldBeTrue(refusal.Message);
        }

        // Every statement, not only the writes: the guard refuses before the
        // context has asked the database anything at all.
        capture.All.Count.ShouldBe(0, capture.Report());

        AcceptedAttachmentSet stored = await ShouldReadBackAsync(accepted.Id);
        stored.Single().Reference.ShouldBe("att_frozen_one");
    }

    /// <summary>
    /// A row written by something that never knew the column keeps moving
    /// forward. It is the ordinary history of the table, and the only thing it
    /// says about attachments is that this notification carries none.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_row_written_without_the_column_reads_as_no_attachments_and_still_advances()
    {
        Guid id = await InsertWithoutTheColumnAsync();
        var capture = new StatementCapture();

        await using (NotificationsDbContext db = CreateContext(capture))
        {
            Notification tracked = await db.Notifications.SingleAsync(
                notification => notification.Id == id);
            tracked.AcceptedAttachmentsJson.ShouldBeNull();
            AcceptedAttachmentManifest.Read(tracked.AcceptedAttachmentsJson)
                .ShouldBeOfType<AcceptedManifestRead.Absent>();

            tracked.MarkDispatched(policyVersion: 3, admittedPlanJson: """[{"channel":"sms"}]""");
            await db.SaveChangesAsync();
        }

        capture.Writes.Count.ShouldBe(1, capture.Report());
        capture.Writes[0].Contains(SnapshotColumn, StringComparison.Ordinal)
            .ShouldBeFalse(capture.Report());

        await using NotificationsDbContext verify = CreateContext();
        Notification advanced = await verify.Notifications.AsNoTracking()
            .SingleAsync(notification => notification.Id == id);
        advanced.Status.ShouldBe(NotificationStatuses.Dispatched);
        advanced.AcceptedAttachmentsJson.ShouldBeNull();
    }

    /// <summary>
    /// A document the store accepts and this reader cannot is refused, case by
    /// case, with the word that names the shape of the defect. These are the
    /// documents PostgreSQL itself has no opinion about: every one of them is
    /// valid JSON, so nothing but this reader stands between them and a
    /// delivery over a composition nobody accepted.
    /// </summary>
    [RequiresDockerTheory]
    [MemberData(nameof(PlantedDocuments))]
    public async Task A_planted_document_the_store_accepts_is_refused_by_the_reader(
        string planted,
        string reason)
    {
        Notification accepted = await StoreAcceptedAsync("att_planted_one");
        await PlantAsync(accepted.Id, planted);

        await using NotificationsDbContext db = CreateContext();
        Notification stored = await db.Notifications.AsNoTracking()
            .SingleAsync(notification => notification.Id == accepted.Id);

        AcceptedAttachmentManifest.Read(stored.AcceptedAttachmentsJson)
            .ShouldBeOfType<AcceptedManifestRead.Unreadable>()
            .Reason.ShouldBe(reason);
    }

    public static TheoryData<string, string> PlantedDocuments()
    {
        var data = new TheoryData<string, string>();

        // The three holes the published type has no opinion about, first: a
        // version nobody wrote a reader for, a member the envelope never
        // declared, and the JSON literal null, which is a column that was
        // written rather than a column that never was.
        data.Add("""{"schemaVersion":2,"items":[{"reference":"att_a","contentIdentity":"c_a","name":"a.pdf","mediaType":"application/pdf","length":1}]}""",
            AcceptedAttachmentManifest.RefusedUnknownSchemaVersion);
        data.Add("""{"schemaVersion":1,"items":[{"reference":"att_a","contentIdentity":"c_a","name":"a.pdf","mediaType":"application/pdf","length":1}],"storageKey":"bucket/key"}""",
            AcceptedAttachmentManifest.RefusedMalformedDocument);
        data.Add("null", AcceptedAttachmentManifest.RefusedMalformedDocument);

        data.Add("[]", AcceptedAttachmentManifest.RefusedMalformedDocument);
        data.Add("{}", AcceptedAttachmentManifest.RefusedMalformedDocument);
        data.Add("""{"schemaVersion":1,"items":[]}""", AcceptedAttachmentManifest.RefusedUnusableSet);
        data.Add("""{"schemaVersion":1,"items":[{"reference":"att_a","contentIdentity":"c_a","name":"a.pdf","mediaType":"application/pdf","length":-1}]}""",
            AcceptedAttachmentManifest.RefusedUnusableSet);
        data.Add("""{"schemaVersion":1,"items":[{"reference":"att_a","contentIdentity":"c_a","name":"a.pdf","mediaType":"application/pdf","length":1},{"reference":"att_a","contentIdentity":"c_b","name":"b.pdf","mediaType":"application/pdf","length":2}]}""",
            AcceptedAttachmentManifest.RefusedUnusableSet);
        return data;
    }

    /// <summary>
    /// Text that is not JSON never reaches the reader, because the column
    /// refuses it. The acceptance that tried to store it ends in a rolled back
    /// transaction rather than in a row with a document nobody can parse.
    /// </summary>
    [RequiresDockerFact]
    public async Task Text_that_is_not_json_is_refused_by_the_column_itself()
    {
        Notification accepted = await StoreAcceptedAsync("att_broken_one");

        PostgresException refusal = await Should.ThrowAsync<PostgresException>(
            () => PlantAsync(accepted.Id, "not a document"));

        refusal.SqlState.ShouldBe(PostgresErrorCodes.InvalidTextRepresentation);
        AcceptedAttachmentSet survived = await ShouldReadBackAsync(accepted.Id);
        survived.Single().Reference.ShouldBe("att_broken_one");
    }

    private async Task<Notification> StoreAcceptedAsync(params string[] references)
    {
        Notification accepted = Accepted();
        accepted.FreezeAcceptedAttachments(AcceptedAttachmentManifest.Serialize(Set(references)));

        await using NotificationsDbContext db = CreateContext();
        db.Notifications.Add(accepted);
        await db.SaveChangesAsync();
        return accepted;
    }

    /// <summary>
    /// One row written exactly as a writer that never knew the column would
    /// have written it: the column is not in the statement at all.
    /// </summary>
    private async Task<Guid> InsertWithoutTheColumnAsync()
    {
        Guid id = Guid.CreateVersion7();
        var key = id.ToString();
        var emptyObject = EmptyJsonObject;
        await using NotificationsDbContext db = CreateContext();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO notifications.notification
                (id, created_at, application, idempotency_key, recipient_id, class, template_key,
                 auth_flow, template_version, variables_masked, requested_by, status, expires_at)
            VALUES
                ({id}, now(), 'araia-cambio', {key}, 'cus_01J5X9', 'transactional',
                 'billing.invoice', false, 1, CAST({emptyObject} AS jsonb), 'producer', 'accepted',
                 now() + interval '1 day')
            """);
        return id;
    }

    private async Task PlantAsync(Guid id, string document)
    {
        await using NotificationsDbContext db = CreateContext();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE notifications.notification SET accepted_attachments = CAST({document} AS jsonb) WHERE id = {id}");
    }

    private async Task<AcceptedAttachmentSet> ShouldReadBackAsync(Guid id)
    {
        await using NotificationsDbContext db = CreateContext();
        Notification stored = await db.Notifications.AsNoTracking()
            .SingleAsync(notification => notification.Id == id);

        return AcceptedAttachmentManifest.Read(stored.AcceptedAttachmentsJson)
            .ShouldBeOfType<AcceptedManifestRead.Present>()
            .Accepted;
    }

    private static Notification Accepted() => Notification.Accept(new NotificationDraft
    {
        Application = "araia-cambio",
        IdempotencyKey = Guid.CreateVersion7().ToString(),
        RecipientId = "cus_01J5X9",
        Class = NotificationClasses.Transactional,
        TemplateKey = "billing.invoice",
        TemplateVersion = 1,
        VariablesMaskedJson = EmptyJsonObject,
        RequestedBy = "producer",
        TtlSeconds = 3600,
        AcceptedAt = DateTimeOffset.UtcNow,
    });

    private static AcceptedAttachmentSet Set(params string[] references)
        => AcceptedAttachmentSet.Of(references.Select(reference => new AcceptedAttachment
        {
            Reference = reference,
            ContentIdentity = "content_" + reference,
            Name = reference + ".pdf",
            MediaType = "application/pdf",
            Length = 11,
        }));

    /// <summary>
    /// Whether the value a parameter carried is the same document, compared as
    /// JSON rather than as text. The column re-serialises what it stores, and
    /// a comparison of raw text would be a comparison of formatting.
    /// </summary>
    private static bool IsSameDocument(object? value, string document)
    {
        if (value is not string candidate)
        {
            return false;
        }

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(candidate), JsonNode.Parse(document));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private NotificationsDbContext CreateContext(StatementCapture? capture = null)
    {
        DbContextOptionsBuilder<NotificationsDbContext> builder =
            new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "notifications"));
        if (capture is not null) builder.AddInterceptors(capture);

        return new NotificationsDbContext(builder.Options);
    }

    /// <summary>
    /// Every statement this context sends, in order, with the values that went
    /// with it. Reads are kept apart from writes because the claims here are
    /// about what was written, and a select would otherwise inflate the counts
    /// those claims are made of.
    /// </summary>
    private sealed class StatementCapture : DbCommandInterceptor
    {
        private readonly List<string> _all = [];
        private readonly List<object?> _parameters = [];

        public List<string> All => _all;

        public IReadOnlyList<string> Writes =>
        [
            .. _all.Where(text => text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)),
        ];

        public IReadOnlyList<object?> Parameters => _parameters;

        public void Reset()
        {
            _all.Clear();
            _parameters.Clear();
        }

        public string Report() => string.Join(Environment.NewLine + "---" + Environment.NewLine, _all);

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Capture(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Capture(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command)
        {
            _all.Add(command.CommandText);
            _parameters.AddRange(
                command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value));
        }
    }
}
