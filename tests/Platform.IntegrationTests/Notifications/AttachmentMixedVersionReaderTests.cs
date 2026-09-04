using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// Readers and writers of two generations over one database, at the seam
/// where the difference between them is real: the statements each one sends.
/// <para>
/// A deployment replaces binaries one at a time, so for a while the same rows
/// are read and written by code that knows the attachment snapshot column and
/// by code that does not. The generation that does not know it is stood in
/// for by a model that ignores the property, which is what makes its
/// statements omit the column exactly as an older binary's would; the
/// generation that knows it is the model the service ships. Every case below
/// states which one wrote and which one read, and proves the arrangement
/// produced the condition it claims before asserting anything about it.
/// </para>
/// <para>
/// What the stand-in is not: an older binary. It shares this build's
/// validator, its canonical form, its claim and its entity type, so nothing
/// here says anything about behaviour that lived in code no longer present.
/// It says what a reader that never selects the column, and a writer that
/// never sets it, do against the schema that has it.
/// </para>
/// <para>
/// The database is this class's own. The row a writer of the current
/// generation leaves here is read back by a model with a hole in it, and a
/// neighbour sharing the database would meet notifications nobody arranged
/// for it.
/// </para>
/// </summary>
[Collection(QueryPlanCollectionDefinition.Name)]
public sealed class AttachmentMixedVersionReaderTests : IAsyncLifetime
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
        await using NotificationsDbContext db = CreateCurrentReader();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.DisposeAsync();

    /// <summary>
    /// A reader that never selects the column, over the schema that has it and
    /// a row that leaves it empty. It loads the notification, moves it forward
    /// and writes the transition, and the column it does not know stays as it
    /// was.
    /// <para>
    /// The two captures are the point. The reader of this build names the
    /// column in its select and the stand-in does not, over the very same
    /// query: without that pair, the absence asserted below would be satisfied
    /// by a stand-in that queries nothing at all.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_reader_that_never_selects_the_column_advances_a_row_the_schema_left_empty()
    {
        Guid id = await StoreWithoutAttachmentsAsync();
        (await ColumnExistsAsync()).ShouldBeTrue(
            "o ensaio afirma esquema novo, e sem a coluna ele mediria a implantação anterior.");
        (await IsSqlNullAsync(id)).ShouldBeTrue(
            "o braço afirma linha nula, e sem ela nada distingue tolerância de ausência de dado.");

        var current = new StatementCapture();
        await using (NotificationsDbContext reader = CreateCurrentReader(current))
        {
            await reader.Notifications.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == id);
        }

        var stale = new StatementCapture();
        await using (NotificationsDbContext reader = CreateReaderWithoutTheColumn(stale))
        {
            Notification loaded = await reader.Notifications
                .SingleAsync(candidate => candidate.Id == id);
            loaded.MarkDispatched(policyVersion: 5, admittedPlanJson: """[{"channel":"email"}]""");
            await reader.SaveChangesAsync();
        }

        // The pair: the same query, one model naming the column and the other
        // not. The first half is what says the stand-in is a stand-in.
        current.Reads.ShouldContain(
            text => text.Contains(SnapshotColumn, StringComparison.Ordinal), current.Report());
        stale.Reads.ShouldAllBe(
            text => !text.Contains(SnapshotColumn, StringComparison.Ordinal), stale.Report());
        stale.Writes.Count.ShouldBe(1, stale.Report());
        stale.Writes[0].Contains(SnapshotColumn, StringComparison.Ordinal)
            .ShouldBeFalse(stale.Report());

        await using NotificationsDbContext verify = CreateCurrentReader();
        Notification advanced = await verify.Notifications.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == id);
        advanced.Status.ShouldBe(NotificationStatuses.Dispatched);
        advanced.AcceptedAttachmentsJson.ShouldBeNull();
    }

    /// <summary>
    /// A row inserted by a writer that never sets the column, read by the
    /// model of this build. The absence is the ordinary path with no
    /// attachments and never a document that failed to read, and the
    /// notification carries on.
    /// <para>
    /// The insert is captured because the whole claim rests on it: a writer
    /// that named the column and sent null would be a different arrangement,
    /// and the row it left would be indistinguishable afterwards.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_row_a_writer_that_never_sets_the_column_left_reads_as_no_attachment()
    {
        var stale = new StatementCapture();
        Notification written = Accepted();

        await using (NotificationsDbContext writer = CreateReaderWithoutTheColumn(stale))
        {
            writer.Notifications.Add(written);
            await writer.SaveChangesAsync();
        }

        stale.Writes.Count.ShouldBe(1, stale.Report());
        stale.Writes[0].Contains("INSERT INTO notifications.notification", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue(stale.Report());
        stale.Writes[0].Contains(SnapshotColumn, StringComparison.Ordinal)
            .ShouldBeFalse(stale.Report());
        (await IsSqlNullAsync(written.Id)).ShouldBeTrue();

        await using NotificationsDbContext reader = CreateCurrentReader();
        Notification loaded = await reader.Notifications
            .SingleAsync(candidate => candidate.Id == written.Id);

        AcceptedAttachmentManifest.Read(loaded.AcceptedAttachmentsJson)
            .ShouldBeOfType<AcceptedManifestRead.Absent>();

        // The gate the pipeline actually calls, and not only the reader behind
        // it: a refusal here would hold the notification instead of letting it
        // move, and holding every row an older writer left is what tolerance
        // has to rule out.
        Should.NotThrow(() => AcceptedAttachmentManifest.RefuseUnreadable(loaded));

        loaded.MarkDispatched(policyVersion: 2, admittedPlanJson: """[{"channel":"sms"}]""");
        await reader.SaveChangesAsync();

        await using NotificationsDbContext verify = CreateCurrentReader();
        Notification advanced = await verify.Notifications.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == written.Id);
        advanced.Status.ShouldBe(NotificationStatuses.Dispatched);
        advanced.AcceptedAttachmentsJson.ShouldBeNull();
    }

    /// <summary>
    /// One row, written with a document by the writer of this build, read at
    /// the same instant by both models. This build reads the whole set; the
    /// model that ignores the column reads a notification with no attachments
    /// at all.
    /// <para>
    /// The two answers over one row are the reason the combination is
    /// forbidden, and they are what the deployment order exists to prevent. A
    /// reader that never selects the column cannot tell a notification
    /// accepted over two files from one accepted over none, so it would carry
    /// the notification to a provider with nothing attached and settle it as
    /// delivered, and the producer was told the files had been accepted.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_row_written_with_a_document_reads_whole_here_and_reads_as_no_attachment_without_the_column()
    {
        var document = AcceptedAttachmentManifest.Serialize(
            Set("att_mixed_one", "att_mixed_two"));
        Notification written = Accepted();
        written.FreezeAcceptedAttachments(document);
        var current = new StatementCapture();

        await using (NotificationsDbContext writer = CreateCurrentReader(current))
        {
            writer.Notifications.Add(written);
            await writer.SaveChangesAsync();
        }

        current.Writes.Count.ShouldBe(1, current.Report());
        current.Writes[0].Contains(SnapshotColumn, StringComparison.Ordinal)
            .ShouldBeTrue(current.Report());
        (await IsSqlNullAsync(written.Id)).ShouldBeFalse(
            "o braço afirma documento não nulo, e sem ele as duas leituras concordariam por vazio.");

        await using (NotificationsDbContext reader = CreateCurrentReader())
        {
            Notification loaded = await reader.Notifications.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == written.Id);
            AcceptedAttachmentSet whole = AcceptedAttachmentManifest
                .Read(loaded.AcceptedAttachmentsJson)
                .ShouldBeOfType<AcceptedManifestRead.Present>()
                .Accepted;
            whole.Select(item => item.Reference).ShouldBe(["att_mixed_one", "att_mixed_two"]);

            // Every member, and not only the references: a reading that
            // returned the right names under the wrong lengths would satisfy
            // the line above and would compose a message the acceptance never
            // agreed to.
            whole.ShouldBe(Set("att_mixed_one", "att_mixed_two"));
        }

        await using NotificationsDbContext stale = CreateReaderWithoutTheColumn();
        Notification blind = await stale.Notifications.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == written.Id);

        blind.AcceptedAttachmentsJson.ShouldBeNull();
        AcceptedAttachmentManifest.Read(blind.AcceptedAttachmentsJson)
            .ShouldBeOfType<AcceptedManifestRead.Absent>();

        // Nothing stops it. The document is on the row, the reader answers
        // absence, and absence is the answer that lets a notification leave.
        Should.NotThrow(() => AcceptedAttachmentManifest.RefuseUnreadable(blind));
    }

    private async Task<Guid> StoreWithoutAttachmentsAsync()
    {
        Notification accepted = Accepted();
        await using NotificationsDbContext db = CreateCurrentReader();
        db.Notifications.Add(accepted);
        await db.SaveChangesAsync();
        return accepted.Id;
    }

    /// <summary>Whether the schema under the rehearsal really carries the column.</summary>
    private async Task<bool> ColumnExistsAsync()
    {
        await using NotificationsDbContext db = CreateCurrentReader();
        await using DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'notifications'
              AND table_name = 'notification'
              AND column_name = 'accepted_attachments'
            """;
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    /// <summary>
    /// Whether the column of one row holds SQL null, asked of the store rather
    /// than of a model: a model that does not map the column would answer
    /// absence for every row, which is the very thing under test here.
    /// </summary>
    private async Task<bool> IsSqlNullAsync(Guid id)
    {
        await using NotificationsDbContext db = CreateCurrentReader();
        await using DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT accepted_attachments IS NULL FROM notifications.notification WHERE id = $1
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.Value = id;
        command.Parameters.Add(parameter);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private NotificationsDbContext CreateCurrentReader(StatementCapture? capture = null)
        => new(Options(capture));

    private ReaderWithoutTheColumn CreateReaderWithoutTheColumn(StatementCapture? capture = null)
        => new(Options(capture));

    private DbContextOptions<NotificationsDbContext> Options(StatementCapture? capture)
    {
        DbContextOptionsBuilder<NotificationsDbContext> builder =
            new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "notifications"))
                .ReplaceService<IModelCacheKeyFactory, PerContextTypeModelCacheKey>();
        if (capture is not null)
        {
            builder.AddInterceptors(capture);
        }

        return builder.Options;
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
    /// The generation that does not know the column, standing in for a binary
    /// deployed before it existed. The property is ignored, so the model has
    /// no place for it: every statement this context builds omits the column,
    /// and every entity it materialises carries nothing there.
    /// </summary>
    private sealed class ReaderWithoutTheColumn(DbContextOptions<NotificationsDbContext> options)
        : NotificationsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            ArgumentNullException.ThrowIfNull(modelBuilder);
            modelBuilder.Entity<Notification>()
                .Ignore(notification => notification.AcceptedAttachmentsJson);
        }
    }

    /// <summary>
    /// Keys the compiled model by the context type, so the model with the
    /// column and the model without it never share one. The default key is
    /// built from the type already; it is replaced here because both contexts
    /// are constructed from the same options type, and a key that ignored the
    /// runtime type would hand the second context whichever model was built
    /// first.
    /// </summary>
    private sealed class PerContextTypeModelCacheKey : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => (context?.GetType(), designTime);
    }

    /// <summary>
    /// Every statement a context sent, kept apart as reads and writes because
    /// the claims here are about which of the two named the column.
    /// </summary>
    private sealed class StatementCapture : DbCommandInterceptor
    {
        private readonly List<string> _all = [];

        public IReadOnlyList<string> Writes =>
        [
            .. _all.Where(text => text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)),
        ];

        public IReadOnlyList<string> Reads =>
        [
            .. _all.Where(text => text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)),
        ];

        public string Report()
            => string.Join(Environment.NewLine + "---" + Environment.NewLine, _all);

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

        private void Capture(DbCommand command) => _all.Add(command.CommandText);
    }
}
