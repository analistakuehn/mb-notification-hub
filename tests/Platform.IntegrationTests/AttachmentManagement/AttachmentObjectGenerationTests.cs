using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;
using Npgsql;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentObjectGenerationTests(AttachmentManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_failed_commit_and_a_retry_leave_exactly_one_recorded_generation()
    {
        const string content = "one-recorded-generation";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "generation-retry-producer");
        using HttpClient client = fixture.CreateProducerClient("generation-retry-producer");
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, content.Length);
        using (registration)
        {
            Attachment attachment = await fixture.QueryAttachmentAsync(registered.Reference);
            IAttachmentObjectStore store = fixture.Services
                .GetRequiredService<IAttachmentObjectStore>();
            var recording = new RecordingObjectStore(store);

            using (IServiceScope failing = fixture.Services.CreateScope())
            {
                UploadAttachment.Handler handler = CreateHandler(
                    failing.ServiceProvider,
                    recording,
                    new FailTheCommitThatAdvancesTheStateSaveOperation());
                using var first = new MemoryStream(
                    Encoding.UTF8.GetBytes(content),
                    writable: false);

                IOException failure = await Should.ThrowAsync<IOException>(async () =>
                    await handler.HandleAsync(
                        new UploadAttachment.Command(
                            registered.Reference,
                            first,
                            content.Length),
                        CancellationToken.None));

                failure.Message.ShouldBe(
                    FailTheCommitThatAdvancesTheStateSaveOperation.FailureMessage);
            }

            (await GenerationsAsync(attachment.Id)).ShouldBeEmpty();
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);

            using (IServiceScope retrying = fixture.Services.CreateScope())
            {
                UploadAttachment.Handler handler = CreateHandler(
                    retrying.ServiceProvider,
                    recording);
                using var second = new MemoryStream(
                    Encoding.UTF8.GetBytes(content),
                    writable: false);

                Result<UploadAttachment.Response> retry = await handler.HandleAsync(
                    new UploadAttachment.Command(registered.Reference, second, content.Length),
                    CancellationToken.None);

                retry.IsSuccess.ShouldBeTrue();
            }

            recording.Captured.Count.ShouldBe(2);
            AttachmentObjectLocator abandoned = recording.Captured[0];
            AttachmentObjectLocator kept = recording.Captured[1];
            abandoned.Version.ShouldNotBe(kept.Version);

            AttachmentObjectGeneration row =
                (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem();
            row.Version.ShouldBe(kept.Version);
            using (AttachmentStoreOpen gone = await store.OpenAsync(
                abandoned,
                CancellationToken.None))
            {
                gone.Status.ShouldBe(AttachmentStoreOpenStatus.Missing);
            }

            AttachmentObjectVersion[] durable = await fixture.ObjectVersionsAsync(row.Key);
            durable.Select(version => version.VersionId).ShouldBe([row.Version]);
            (await fixture.ReadObjectAsync(durable[0])).ShouldBe(content);
            Convert.ToHexString(row.Digest)
                .ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));

            Attachment received = await fixture.QueryAttachmentAsync(registered.Reference);
            received.State.ShouldBe(AttachmentStates.Received);
            received.SizeBytes.ShouldBe(row.LengthBytes);
            received.ReceivedAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(row.CapturedAt);
        }
    }

    [RequiresDockerFact]
    public async Task A_retry_over_a_generation_that_survived_the_failure_is_refused()
    {
        const string content = "undiscarded-generation";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "undiscarded-generation-producer");
        using HttpClient client = fixture.CreateProducerClient("undiscarded-generation-producer");
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, content.Length);
        using (registration)
        {
            Attachment attachment = await fixture.QueryAttachmentAsync(registered.Reference);
            var recording = new RecordingObjectStore(
                fixture.Services.GetRequiredService<IAttachmentObjectStore>())
            {
                DiscardsReachTheStore = false,
            };

            using (IServiceScope failing = fixture.Services.CreateScope())
            {
                UploadAttachment.Handler handler = CreateHandler(
                    failing.ServiceProvider,
                    recording,
                    new FailTheCommitThatAdvancesTheStateSaveOperation());
                using var first = new MemoryStream(
                    Encoding.UTF8.GetBytes(content),
                    writable: false);

                await Should.ThrowAsync<IOException>(async () =>
                    await handler.HandleAsync(
                        new UploadAttachment.Command(
                            registered.Reference,
                            first,
                            content.Length),
                        CancellationToken.None));
            }

            recording.DiscardCallCount.ShouldBe(1);
            AttachmentObjectLocator orphan = recording.Captured.ShouldHaveSingleItem();

            using (IServiceScope retrying = fixture.Services.CreateScope())
            {
                UploadAttachment.Handler handler = CreateHandler(
                    retrying.ServiceProvider,
                    recording);
                using var second = new MemoryStream(
                    Encoding.UTF8.GetBytes(content),
                    writable: false);

                Result<UploadAttachment.Response> retry = await handler.HandleAsync(
                    new UploadAttachment.Command(registered.Reference, second, content.Length),
                    CancellationToken.None);

                retry.IsFailure.ShouldBeTrue();
                retry.Error.ShouldBe(ErrorCodes.UploadConflict);
            }

            recording.Captured.ShouldHaveSingleItem();
            (await GenerationsAsync(attachment.Id)).ShouldBeEmpty();
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
            AttachmentObjectVersion[] durable = await fixture.ObjectVersionsAsync(orphan.Key);
            durable.Select(version => version.VersionId).ShouldBe([orphan.Version]);
        }
    }

    [RequiresDockerFact]
    public async Task A_recorded_generation_refuses_a_rewrite_of_its_identity()
    {
        Attachment attachment = await UploadAsync("identity-freeze-producer", "frozen-identity");
        AttachmentObjectGeneration recorded =
            (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem();

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        AttachmentObjectGeneration tracked = await dbContext.ObjectGenerations
            .SingleAsync(generation => generation.Id == recorded.Id);
        dbContext.Entry(tracked).Property(generation => generation.Version).CurrentValue =
            $"{Guid.NewGuid():N}";

        InvalidOperationException rejection = await Should.ThrowAsync<InvalidOperationException>(
            async () => await dbContext.SaveChangesAsync());

        rejection.Message.ShouldContain("read-only after it has been saved");
        AttachmentObjectGeneration durable =
            (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem();
        durable.Version.ShouldBe(recorded.Version);
        Convert.ToHexString(durable.Digest).ShouldBe(Convert.ToHexString(recorded.Digest));
    }

    [RequiresDockerFact]
    public async Task Discarding_a_generation_removes_it_and_leaves_no_marker_behind()
    {
        Attachment attachment = await UploadAsync("generation-discard-producer", "discarded");
        AttachmentObjectGeneration recorded =
            (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem();
        IAttachmentObjectStore store = fixture.Services
            .GetRequiredService<IAttachmentObjectStore>();
        AttachmentObjectLocator locator = recorded.Locator();

        using (AttachmentStoreOpen before = await store.OpenAsync(locator, CancellationToken.None))
        {
            before.Status.ShouldBe(AttachmentStoreOpenStatus.Opened);
        }

        (await store.DiscardAsync(locator, CancellationToken.None))
            .ShouldBe(AttachmentObjectDiscard.Removed);

        using (AttachmentStoreOpen after = await store.OpenAsync(locator, CancellationToken.None))
        {
            after.Status.ShouldBe(AttachmentStoreOpenStatus.Missing);
        }

        (await fixture.ObjectVersionsAsync(recorded.Key)).ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task A_removal_the_store_cannot_perform_is_not_answered_as_removed()
    {
        Attachment attachment = await UploadAsync("unremovable-generation-producer", "kept");
        AttachmentObjectGeneration recorded =
            (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem();

        // The generation is named inside a store that does not exist, which is
        // an unreachable custody without touching the bytes under test.
        AttachmentObjectDiscard answer = await fixture.Services
            .GetRequiredService<IAttachmentObjectStore>()
            .DiscardAsync(
                AttachmentObjectLocator.FromStoredRow(
                    $"absent-store-{Guid.NewGuid():N}",
                    recorded.Key,
                    recorded.Version),
                CancellationToken.None);

        answer.ShouldBe(AttachmentObjectDiscard.Unavailable);
        (await fixture.ObjectVersionsAsync(recorded.Key))
            .Select(version => version.VersionId)
            .ShouldBe([recorded.Version]);
    }

    /// <summary>
    /// What this proves is the independent recomputation. It does not prove
    /// that the digest came from the reading rather than from what the
    /// provider asserts, because on a healthy provider the two agree, so the
    /// substitution passes here either way. The oracle that separates them is
    /// the one where the double keeps fewer bytes than it was handed, and the
    /// two sources disagree by construction.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_recorded_digest_equals_an_independent_hash_of_the_stored_content()
    {
        const string content = "recomputed-digest-content";
        Attachment attachment = await UploadAsync("digest-recompute-producer", content);
        AttachmentObjectGeneration recorded =
            (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem();

        recorded.Algorithm.ShouldBe(AttachmentContentProof.Sha256);
        recorded.LengthBytes.ShouldBe(content.Length);
        Convert.ToHexString(recorded.Digest).ShouldBe(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
        recorded.Proof()
            .Matches(AttachmentContentProof.Sha256Of(
                SHA256.HashData(Encoding.UTF8.GetBytes(content)),
                content.Length))
            .ShouldBeTrue();
    }

    [RequiresDockerFact]
    public async Task A_write_that_delivers_fewer_bytes_than_declared_is_not_a_store_failure()
    {
        IAttachmentObjectStore store = fixture.Services
            .GetRequiredService<IAttachmentObjectStore>();
        var contentId = Guid.NewGuid();
        using var content = new MemoryStream("abc"u8.ToArray(), writable: false);

        AttachmentObjectCapture capture = await store.PutAsync(
            new AttachmentObjectRequest(contentId, "application/pdf", 4),
            content,
            CancellationToken.None);

        // The transport drops a request that promised four bytes and sent
        // three, and the failure it raises derives from the same reading
        // failure a broken connection raises. Answering both as an unavailable
        // store blames the store for what the caller sent.
        capture.Status.ShouldBe(AttachmentObjectCaptureStatus.ContentShorterThanDeclared);
        capture.Locator.ShouldBeNull();
    }

    [RequiresDockerFact]
    public async Task A_generation_row_that_names_no_generation_is_refused_by_the_database()
    {
        Attachment attachment = await UploadAsync("blank-version-producer", "blank-version");
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();

        // A blank generation is the state that makes removal place a delete
        // marker instead of removing anything, and the marker is what reopens
        // the conditional write for a second durable generation under the same
        // key. The row cannot exist, so the state cannot be reached.
        PostgresException rejection = await Should.ThrowAsync<PostgresException>(async () =>
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO attachmentmanagement.attachment_object_generation
                     (id, attachment_id, store, object_key, version,
                      digest_algorithm, digest, length_bytes, captured_at)
                 VALUES ({Guid.CreateVersion7()}, {attachment.Id}, {"a-store"},
                         {"attachments/blank"}, {"   "}, {AttachmentContentProof.Sha256},
                         {new byte[32]}, {0L}, {DateTimeOffset.UtcNow})
                 """));

        rejection.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        rejection.ConstraintName.ShouldBe(
            "ck_attachment_object_generation_version_not_blank");
    }

    /// <summary>
    /// The freeze on the mapping refuses a revision made through a tracked
    /// instance and never sees a statement that goes around the tracker. A
    /// set-based update is exactly that statement: it reaches the durable row
    /// with no entity loaded, and until the guard moved into the database it
    /// rewrote the recorded proof of the bytes in silence.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_set_based_update_of_a_recorded_generation_is_refused_by_the_database()
    {
        Attachment attachment = await UploadAsync("set-update-producer", "set-based-update");
        AttachmentObjectGeneration recorded =
            (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem();

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();

        var forgedDigest = new byte[32];
        forgedDigest[0] = 0xFF;
        PostgresException rejection = await Should.ThrowAsync<PostgresException>(async () =>
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                 UPDATE attachmentmanagement.attachment_object_generation
                 SET digest = {forgedDigest}, length_bytes = {0L}
                 WHERE id = {recorded.Id}
                 """));

        rejection.MessageText.ShouldContain("append-only");

        AttachmentObjectGeneration durable =
            (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem();
        Convert.ToHexString(durable.Digest).ShouldBe(Convert.ToHexString(recorded.Digest));
        durable.LengthBytes.ShouldBe(recorded.LengthBytes);
        durable.Version.ShouldBe(recorded.Version);
    }

    [RequiresDockerFact]
    public async Task A_set_based_delete_of_a_recorded_generation_is_refused_by_the_database()
    {
        Attachment attachment = await UploadAsync("set-delete-producer", "set-based-delete");
        AttachmentObjectGeneration recorded =
            (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem();

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();

        PostgresException rejection = await Should.ThrowAsync<PostgresException>(async () =>
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                 DELETE FROM attachmentmanagement.attachment_object_generation
                 WHERE id = {recorded.Id}
                 """));

        rejection.MessageText.ShouldContain("append-only");
        (await GenerationsAsync(attachment.Id)).ShouldHaveSingleItem()
            .Id.ShouldBe(recorded.Id);
    }

    /// <summary>
    /// What the refusal of a delete costs, stated rather than discovered later.
    /// The generation line points at the attachment under a restrictive foreign
    /// key, so removing the attachment needs the line removed first, and the
    /// line refuses. An attachment that ever held content therefore cannot be
    /// removed from this schema at all.
    /// <para>
    /// The two halves that keep this from being a schema nobody can operate are
    /// measured with it: an attachment that never held content is removable,
    /// and the guard covers only the generation table, so the state of an
    /// attachment still moves.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attachment_that_holds_a_generation_cannot_be_removed_and_one_without_can()
    {
        Attachment withContent = await UploadAsync("indelible-producer", "indelible");
        (await GenerationsAsync(withContent.Id)).ShouldHaveSingleItem();

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();

        PostgresException blockedByTheKey = await Should.ThrowAsync<PostgresException>(async () =>
            await dbContext.Database.ExecuteSqlAsync(
                $"DELETE FROM attachmentmanagement.attachment WHERE id = {withContent.Id}"));
        blockedByTheKey.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);

        // And the way around the key is closed too, which is what turns "needs
        // an order" into "cannot be done".
        PostgresException blockedByTheTrigger = await Should.ThrowAsync<PostgresException>(async () =>
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                 DELETE FROM attachmentmanagement.attachment_object_generation
                 WHERE attachment_id = {withContent.Id}
                 """));
        blockedByTheTrigger.MessageText.ShouldContain("append-only");

        // The row is still there for a reader, which is the point of refusing.
        (await GenerationsAsync(withContent.Id)).ShouldHaveSingleItem();

        // The state of the attachment still moves: the guard is on the line
        // that records the bytes, never on the row that records the lifecycle.
        (await dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE attachmentmanagement.attachment
             SET reconciliation_liability = {"content-orphan"}
             WHERE id = {withContent.Id}
             """)).ShouldBe(1);

        // An attachment registered and never uploaded owns no generation line,
        // so nothing holds it and it is removable like any other row.
        var withoutContent = Guid.CreateVersion7();
        await dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO attachmentmanagement.attachment
                 (id, reference, application, file_name, content_type, size_bytes,
                  content_id, state, created_at)
             VALUES ({withoutContent}, {Guid.CreateVersion7().ToString()}, {"indelible-app"},
                     {"never-uploaded.pdf"}, {"application/pdf"}, {1L},
                     {Guid.CreateVersion7()}, {"registered"}, {DateTimeOffset.UtcNow})
             """);
        (await dbContext.Database.ExecuteSqlAsync(
            $"DELETE FROM attachmentmanagement.attachment WHERE id = {withoutContent}"))
            .ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_store_that_never_versioned_is_refused_and_records_no_identity()
    {
        var bucket = await CreateBucketAsync(versioning: null);

        await AssertFailsClosedAsync(bucket, "unversioned-store-producer");
    }

    [RequiresDockerFact]
    public async Task A_store_with_suspended_versioning_is_refused_and_records_no_identity()
    {
        var bucket = await CreateBucketAsync(versioning: VersionStatus.Suspended);

        await AssertFailsClosedAsync(bucket, "suspended-store-producer");
    }

    private async Task AssertFailsClosedAsync(string bucket, string principal)
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{AttachmentObjectStoreOptions.SectionName}:Bucket"] = bucket,
                })));
        using HttpClient client = fixture.CreateProducerClient(host, principal);
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
        using (registration)
        {
            fixture.Logs.Events.Clear();
            using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
                client,
                registered.Reference,
                "abcd");
            var error = await upload.Content.ReadAsStringAsync();

            upload.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

            // The provider answered, so it was available. The code says what
            // happened, which is that the store kept the bytes and named no
            // generation for them.
            error.ShouldContain(ErrorCodes.StoreUnidentifiedGeneration);
            error.ShouldNotContain(ErrorCodes.StoreUnavailable);
            Attachment attachment = await fixture.QueryAttachmentAsync(registered.Reference);
            attachment.State.ShouldBe(AttachmentStates.AwaitingUpload);
            attachment.ReceivedAt.ShouldBeNull();
            (await GenerationsAsync(attachment.Id)).ShouldBeEmpty();

            // What is actually left in the refused store, stated instead of
            // implied. The write went through before the answer came back
            // without a generation, and nothing can remove bytes whose
            // generation nobody named: a removal without one places a delete
            // marker and leaves them readable.
            AttachmentObjectVersion[] durable = await fixture.ObjectVersionsAsync(bucket: bucket);
            AttachmentObjectVersion orphan = durable.ShouldHaveSingleItem();
            orphan.IsDeleteMarker.ShouldBeFalse();
            orphan.Key.ShouldBe($"attachments/{attachment.ContentId:N}");
            (await fixture.ReadObjectAsync(orphan, bucket)).ShouldBe("abcd");

            // Those bytes are unreachable from the module's own records, so
            // the only thing that makes them findable again is an event naming
            // the attachment. The storage coordinate stays out of it.
            string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
            logs.ShouldContain(
                fragment => fragment.Contains(registered.Reference, StringComparison.Ordinal)
                    && fragment.Contains("sem nomear a geração", StringComparison.Ordinal),
                "bytes left without an identity have to be named somewhere.");
            logs.ShouldAllBe(fragment => !fragment.Contains(bucket, StringComparison.Ordinal));
            logs.ShouldAllBe(fragment => !fragment.Contains(orphan.Key, StringComparison.Ordinal));
        }
    }

    private async Task<string> CreateBucketAsync(VersionStatus? versioning)
    {
        var bucket = $"attachment-identity-{Guid.NewGuid():N}";
        await fixture.S3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        if (versioning is not null)
        {
            await AttachmentManagementApiFixture.EnableVersioningAsync(fixture.S3, bucket);
            await fixture.S3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket,
                VersioningConfig = new S3BucketVersioningConfig { Status = versioning },
            });
        }

        return bucket;
    }

    private async Task<Attachment> UploadAsync(string principal, string content)
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, content.Length);
        using (registration)
        {
            using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
                client,
                registered.Reference,
                content);
            upload.StatusCode.ShouldBe(HttpStatusCode.OK);
            return await fixture.QueryAttachmentAsync(registered.Reference);
        }
    }

    private async Task<AttachmentObjectGeneration[]> GenerationsAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .ObjectGenerations
            .AsNoTracking()
            .Where(generation => generation.AttachmentId == attachmentId)
            .OrderBy(generation => generation.CapturedAt)
            .ToArrayAsync();
    }

    private static UploadAttachment.Handler CreateHandler(
        IServiceProvider serviceProvider,
        IAttachmentObjectStore? objectStore = null,
        IAttachmentSaveOperation? saveOperation = null)
        => new(
            serviceProvider.GetRequiredService<AttachmentManagementDbContext>(),
            serviceProvider.GetRequiredService<IDbContextFactory<AttachmentManagementDbContext>>(),
            saveOperation ?? serviceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            objectStore ?? serviceProvider.GetRequiredService<IAttachmentObjectStore>(),
            TimeProvider.System,
            NullLogger<UploadAttachment.Handler>.Instance);

    /// <summary>
    /// Fails the commit that carries the state transition and lets any other
    /// commit through. The aim is the failure, not the call number, so the
    /// arrangement holds whether the write travels in one unit of work or in
    /// two.
    /// </summary>
    private sealed class FailTheCommitThatAdvancesTheStateSaveOperation
        : IAttachmentSaveOperation
    {
        internal const string FailureMessage = "Simulated failure before the state advances.";

        public Task SaveChangesAsync(
            AttachmentManagementDbContext dbContext,
            CancellationToken cancellationToken)
            => dbContext.ChangeTracker
                .Entries<Attachment>()
                .Any(entry => entry.State == EntityState.Modified)
                    ? Task.FromException(new IOException(FailureMessage))
                    : dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Passes every call to the real store and remembers each generation it
    /// captured, so a test can name the generation an attempt left behind.
    /// With <see cref="DiscardsReachTheStore"/> off, the compensation is
    /// counted and never reaches the store, which is the state of an upload
    /// whose cleanup did not land.
    /// </summary>
    private sealed class RecordingObjectStore(IAttachmentObjectStore inner)
        : IAttachmentObjectStore
    {
        private readonly List<AttachmentObjectLocator> _captured = [];

        internal List<AttachmentObjectLocator> Captured => _captured;

        internal bool DiscardsReachTheStore { get; init; } = true;

        internal int DiscardCallCount { get; private set; }

        public async Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            AttachmentObjectCapture capture = await inner.PutAsync(
                request,
                content,
                cancellationToken);
            if (capture.Locator is { } locator)
            {
                _captured.Add(locator);
            }

            return capture;
        }

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
            => inner.OpenAsync(locator, cancellationToken);

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            DiscardCallCount++;

            // With the call held back, the caller still hears the removal it
            // asked for. That is the shape of a compensation that never
            // landed and was believed anyway.
            return DiscardsReachTheStore
                ? inner.DiscardAsync(locator, cancellationToken)
                : Task.FromResult(AttachmentObjectDiscard.Removed);
        }
    }
}
