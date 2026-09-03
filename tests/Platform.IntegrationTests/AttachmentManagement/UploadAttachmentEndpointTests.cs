using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class UploadAttachmentEndpointTests(AttachmentManagementApiFixture fixture)
{
    private const string ContentSentinel = "private-attachment-content-7f38d5a2";

    [RequiresDockerFact]
    public async Task Upload_streams_to_s3_marks_received_and_rejects_overwrite_without_leaks()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "attachment-producer");
        fixture.Logs.Events.Clear();
        using HttpClient client = fixture.CreateProducerClient("attachment-producer");
        AttachmentObjectVersion[] versionsBefore = await fixture.ObjectVersionsAsync();
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, ContentSentinel.Length);
        using (registration)
        {
            registration.StatusCode.ShouldBe(HttpStatusCode.Created);

            using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
                client,
                registered.Reference,
                ContentSentinel);
            AttachmentApi.ApiResponse received =
                await AttachmentApi.ReadMinimalResponseAsync(upload);

            upload.StatusCode.ShouldBe(HttpStatusCode.OK);
            received.Reference.ShouldBe(registered.Reference);
            received.State.ShouldBe(AttachmentStates.Received);

            AttachmentObjectVersion captured = (await fixture.ObjectVersionsAsync())
                .Except(versionsBefore)
                .ShouldHaveSingleItem();
            captured.IsDeleteMarker.ShouldBeFalse();
            (await fixture.ReadObjectAsync(captured)).ShouldBe(ContentSentinel);

            using HttpResponseMessage query = await client.GetAsync(
                $"/v1/attachments/{registered.Reference}");
            AttachmentApi.ApiResponse observed = await AttachmentApi.ReadMinimalResponseAsync(query);
            observed.State.ShouldBe(AttachmentStates.Received);

            using HttpResponseMessage repeated = await AttachmentApi.PutContentAsync(
                client,
                registered.Reference,
                new string('x', ContentSentinel.Length));
            var repeatedBody = await repeated.Content.ReadAsStringAsync();
            repeated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            repeatedBody.ShouldContain(ErrorCodes.AlreadyReceived);
            (await fixture.ObjectVersionsAsync())
                .Except(versionsBefore)
                .ShouldHaveSingleItem()
                .ShouldBe(captured);
            (await fixture.ReadObjectAsync(captured)).ShouldBe(ContentSentinel);

            Attachment persisted = await fixture.QueryAttachmentAsync(registered.Reference);

            // The event that says a generation was recorded used to name it
            // with the locator alone, and the locator renders as a fixed
            // placeholder, so the line carried no correlator at all and no
            // investigation could tie it to an attachment.
            string[] recordedEvents =
                [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
            recordedEvents.ShouldContain(
                fragment => fragment.Contains(
                    "geração verificada e registrada",
                    StringComparison.Ordinal)
                    && fragment.Contains(registered.Reference, StringComparison.Ordinal),
                "the recorded generation has to name the attachment it belongs to.");

            AssertDoesNotLeak(
                [
                    .. AttachmentApi.ResponseFragments(registration, registered.Body),
                    .. AttachmentApi.ResponseFragments(upload, received.Body),
                    .. AttachmentApi.ResponseFragments(query, observed.Body),
                    .. AttachmentApi.ResponseFragments(repeated, repeatedBody),
                ],
                captured,
                persisted.ContentId.ToString("N"),
                await RecordedDigestAsync(persisted.Id));
        }
    }

    [RequiresDockerFact]
    public async Task Divergent_content_length_keeps_waiting_and_does_not_write_an_object()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "size-checking-producer");
        using HttpClient client = fixture.CreateProducerClient("size-checking-producer");
        AttachmentObjectVersion[] versionsBefore = await fixture.ObjectVersionsAsync();
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
        using (registration)
        {
            using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
                client,
                registered.Reference,
                "abc");
            var error = await upload.Content.ReadAsStringAsync();

            upload.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            error.ShouldContain(ErrorCodes.SizeMismatch);
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
            (await fixture.ObjectVersionsAsync()).ShouldBe(versionsBefore, ignoreOrder: true);
        }
    }

    [RequiresDockerFact]
    public async Task Missing_store_configuration_fails_closed_at_upload()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "unconfigured-store-producer");
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{AttachmentObjectStoreOptions.SectionName}:Bucket"] = null,
                    [$"{AttachmentObjectStoreOptions.SectionName}:ServiceUrl"] = null,
                    [$"{AttachmentObjectStoreOptions.SectionName}:Region"] = null,
                    [$"{AttachmentObjectStoreOptions.SectionName}:AccessKey"] = null,
                    [$"{AttachmentObjectStoreOptions.SectionName}:SecretKey"] = null,
                })));
        using HttpClient client = fixture.CreateProducerClient(
            host,
            "unconfigured-store-producer");
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
        using (registration)
        {
            using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
                client,
                registered.Reference,
                "abcd");
            var error = await upload.Content.ReadAsStringAsync();

            upload.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            error.ShouldContain(ErrorCodes.StoreUnavailable);
            error.ShouldNotContain(AttachmentObjectStoreOptions.SectionName);
            error.ShouldNotContain(AttachmentManagementApiFixture.Bucket);
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task Store_failure_does_not_disclose_private_configuration_in_error_or_logs()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "storage-failure-producer");
        var privateBucket = $"private-missing-bucket-{Guid.NewGuid():N}";
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{AttachmentObjectStoreOptions.SectionName}:Bucket"] = privateBucket,
                })));
        using HttpClient client = fixture.CreateProducerClient(host, "storage-failure-producer");
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
            error.ShouldContain(ErrorCodes.StoreUnavailable);
            string[] fragments =
            [
                .. AttachmentApi.ResponseFragments(upload, error),
                .. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments),
            ];
            fragments.ShouldAllBe(fragment =>
                !fragment.Contains(privateBucket, StringComparison.Ordinal));
            fragments.ShouldAllBe(fragment =>
                !fragment.Contains(fixture.AwsEndpoint, StringComparison.Ordinal));
        }
    }

    [RequiresDockerFact]
    public async Task Concurrent_uploads_store_once_and_return_one_explicit_conflict()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "concurrent-producer-a");
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "concurrent-producer-b");
        using HttpClient firstClient = fixture.CreateProducerClient("concurrent-producer-a");
        using HttpClient secondClient = fixture.CreateProducerClient("concurrent-producer-b");
        AttachmentObjectVersion[] versionsBefore = await fixture.ObjectVersionsAsync();
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(firstClient, sizeBytes: 8);
        using (registration)
        {
            string[] contents = ["alpha123", "beta1234"];
            Task<HttpResponseMessage> first = AttachmentApi.PutContentAsync(
                firstClient,
                registered.Reference,
                contents[0]);
            Task<HttpResponseMessage> second = AttachmentApi.PutContentAsync(
                secondClient,
                registered.Reference,
                contents[1]);
            HttpResponseMessage[] responses = await Task.WhenAll(first, second);
            try
            {
                responses.Select(response => response.StatusCode)
                    .Order()
                    .ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
                HttpResponseMessage conflict = responses.Single(
                    response => response.StatusCode == HttpStatusCode.Conflict);
                var conflictBody = await conflict.Content.ReadAsStringAsync();
                (conflictBody.Contains(ErrorCodes.AlreadyReceived, StringComparison.Ordinal)
                    || conflictBody.Contains(ErrorCodes.UploadConflict, StringComparison.Ordinal))
                    .ShouldBeTrue();

                var successfulIndex = Array.FindIndex(
                    responses,
                    response => response.StatusCode == HttpStatusCode.OK);
                successfulIndex.ShouldBeGreaterThanOrEqualTo(0);
                AttachmentObjectVersion captured = (await fixture.ObjectVersionsAsync())
                    .Except(versionsBefore)
                    .ShouldHaveSingleItem();
                captured.IsDeleteMarker.ShouldBeFalse();
                (await fixture.ReadObjectAsync(captured)).ShouldBe(contents[successfulIndex]);
                (await fixture.QueryAttachmentAsync(registered.Reference)).State
                    .ShouldBe(AttachmentStates.Received);
            }
            finally
            {
                foreach (HttpResponseMessage response in responses)
                {
                    response.Dispose();
                }
            }
        }
    }

    [RequiresDockerFact]
    public async Task Durable_length_below_the_declared_size_is_discarded_without_advancing_state()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "length-verification-producer");
        using HttpClient client = fixture.CreateProducerClient("length-verification-producer");
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
        using (registration)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            var store = new StoredObjectStore(keptBytes: 3);
            UploadAttachment.Handler handler = CreateHandler(scope.ServiceProvider, store);
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);
            using var caller = new CancellationTokenSource();

            Result<UploadAttachment.Response> result = await handler.HandleAsync(
                new UploadAttachment.Command(registered.Reference, content, 4),
                caller.Token);
            await caller.CancelAsync();

            result.IsFailure.ShouldBeTrue();
            result.Error.ShouldBe(ErrorCodes.SizeMismatch);
            store.DiscardCalled.ShouldBeTrue();

            // What the removal needs is independence from the caller, not the
            // absence of any deadline. Asserting that it received the empty
            // token asserted the mechanism and refused every bounded token,
            // which left the removal with no limit at all.
            store.DiscardCancellationToken.IsCancellationRequested.ShouldBeFalse();
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task Existing_object_never_advances_an_attachment_that_is_still_waiting()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "existing-object-producer");
        using HttpClient client = fixture.CreateProducerClient("existing-object-producer");
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
        using (registration)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            var store = new ExistingObjectStore();
            UploadAttachment.Handler handler = CreateHandler(scope.ServiceProvider, store);
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);

            Result<UploadAttachment.Response> result = await handler.HandleAsync(
                new UploadAttachment.Command(registered.Reference, content, 4),
                CancellationToken.None);

            result.IsFailure.ShouldBeTrue();
            result.Error.ShouldBe(ErrorCodes.UploadConflict);
            store.DiscardCalled.ShouldBeFalse();
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task Cancellation_after_storage_uses_independent_cleanup_and_retry_recovers()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "canceled-upload-producer");
        using HttpClient client = fixture.CreateProducerClient("canceled-upload-producer");
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
        using (registration)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            AttachmentManagementDbContext dbContext = scope.ServiceProvider
                .GetRequiredService<AttachmentManagementDbContext>();
            using var cancellation = new CancellationTokenSource();
            var store = new CancelAfterWriteStore(cancellation);
            UploadAttachment.Handler handler = CreateHandler(
                scope.ServiceProvider,
                store,
                dbContext: dbContext);
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);

            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await handler.HandleAsync(
                    new UploadAttachment.Command(registered.Reference, content, 4),
                    cancellation.Token));

            store.DiscardCalled.ShouldBeTrue();

            // The caller's token is cancelled at this point, and the removal's
            // is not. That is the property, and it survives the removal having
            // a deadline of its own.
            cancellation.Token.IsCancellationRequested.ShouldBeTrue();
            store.DiscardCancellationToken.IsCancellationRequested.ShouldBeFalse();
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);

            using IServiceScope retryScope = fixture.Services.CreateScope();
            var stored = new StoredObjectStore(keptBytes: 4);
            UploadAttachment.Handler retryHandler = CreateHandler(
                retryScope.ServiceProvider,
                stored);
            using var retryContent = new MemoryStream("test"u8.ToArray(), writable: false);

            Result<UploadAttachment.Response> retry = await retryHandler.HandleAsync(
                new UploadAttachment.Command(registered.Reference, retryContent, 4),
                CancellationToken.None);

            retry.IsSuccess.ShouldBeTrue();
            retry.Value!.State.ShouldBe(AttachmentStates.Received);
            stored.DiscardCalled.ShouldBeFalse();
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.Received);
        }
    }

    [RequiresDockerFact]
    public async Task Confirmed_commit_followed_by_transport_failure_preserves_the_object()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "ambiguous-commit-producer");
        using HttpClient client = fixture.CreateProducerClient("ambiguous-commit-producer");
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
        using (registration)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            var store = new StoredObjectStore(keptBytes: 4);
            UploadAttachment.Handler handler = CreateHandler(
                scope.ServiceProvider,
                store,
                new CommitThenThrowSaveOperation());
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);

            IOException failure = await Should.ThrowAsync<IOException>(async () =>
                await handler.HandleAsync(
                    new UploadAttachment.Command(registered.Reference, content, 4),
                    CancellationToken.None));

            failure.Message.ShouldBe(CommitThenThrowSaveOperation.FailureMessage);
            store.DiscardCalled.ShouldBeFalse();
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.Received);
        }
    }

    [RequiresDockerFact]
    public async Task Unconfirmed_state_after_save_failure_preserves_the_object_and_names_the_attachment()
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(
            fixture.Services,
            "unconfirmed-commit-producer");
        using HttpClient client = fixture.CreateProducerClient("unconfirmed-commit-producer");
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
        using (registration)
        {
            Attachment persisted = await fixture.QueryAttachmentAsync(registered.Reference);
            using IServiceScope scope = fixture.Services.CreateScope();
            var store = new StoredObjectStore(keptBytes: 4);
            fixture.Logs.Events.Clear();
            var handler = new UploadAttachment.Handler(
                scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>(),
                new ThrowingDbContextFactory(),
                new ThrowBeforeCommitSaveOperation(),
                store,
                TimeProvider.System,
                scope.ServiceProvider.GetRequiredService<ILogger<UploadAttachment.Handler>>());
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);

            IOException failure = await Should.ThrowAsync<IOException>(async () =>
                await handler.HandleAsync(
                    new UploadAttachment.Command(registered.Reference, content, 4),
                    CancellationToken.None));

            failure.Message.ShouldBe(ThrowBeforeCommitSaveOperation.FailureMessage);
            store.DiscardCalled.ShouldBeFalse();
            string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];

            // One rule decides what an event may name, and it applies to the
            // failure events too: the opaque reference is publishable, because
            // without it the warning cannot be tied to anything, and the
            // storage coordinate and the content identifier are not.
            logs.ShouldContain(
                fragment => fragment.Contains("preservado para reconciliação", StringComparison.Ordinal)
                    && fragment.Contains(registered.Reference, StringComparison.Ordinal),
                "the warning that says the object was kept has to say for which attachment.");
            logs.ShouldAllBe(fragment =>
                !fragment.Contains(persisted.ContentId.ToString("N"), StringComparison.Ordinal));
            logs.ShouldAllBe(fragment =>
                !fragment.Contains(AttachmentApi.Application, StringComparison.Ordinal));
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task A_removal_the_store_did_not_confirm_is_recorded_against_the_attachment()
    {
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await RegisterAsync("unconfirmed-removal-producer");
        using (registration)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            var store = new StoredObjectStore(keptBytes: 3)
            {
                DiscardAnswer = AttachmentObjectDiscard.Unavailable,
            };
            UploadAttachment.Handler handler = CreateLoggingHandler(scope.ServiceProvider, store);
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);
            fixture.Logs.Events.Clear();

            Result<UploadAttachment.Response> result = await handler.HandleAsync(
                new UploadAttachment.Command(registered.Reference, content, 4),
                CancellationToken.None);

            result.Error.ShouldBe(ErrorCodes.SizeMismatch);
            store.DiscardCallCount.ShouldBe(1);

            // Bytes the store did not confirm as removed stay durable under a
            // key that stays taken, so every retry is refused. Without the
            // event, nothing in the system names the attachment they belong to.
            string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
            logs.ShouldContain(
                fragment => fragment.Contains("não confirmou a remoção", StringComparison.Ordinal)
                    && fragment.Contains(registered.Reference, StringComparison.Ordinal),
                "an unconfirmed removal has to name the attachment it left behind.");
            logs.ShouldAllBe(fragment =>
                !fragment.Contains(store.Locator.Version, StringComparison.Ordinal));
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task A_removal_that_throws_does_not_replace_the_failure_it_compensates()
    {
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await RegisterAsync("throwing-removal-producer");
        using (registration)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            var store = new StoredObjectStore(keptBytes: 3)
            {
                DiscardFailure = new IOException("Simulated removal failure."),
            };
            UploadAttachment.Handler handler = CreateLoggingHandler(scope.ServiceProvider, store);
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);
            fixture.Logs.Events.Clear();

            Result<UploadAttachment.Response> result = await handler.HandleAsync(
                new UploadAttachment.Command(registered.Reference, content, 4),
                CancellationToken.None);

            // A removal that throws used to run inside the block whose own
            // handler compensates as well, which asked the store twice and
            // turned this clean refusal into an unexpected failure.
            result.IsFailure.ShouldBeTrue();
            result.Error.ShouldBe(ErrorCodes.SizeMismatch);
            store.DiscardCallCount.ShouldBe(1);
            string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
            logs.ShouldContain(
                fragment => fragment.Contains("lançou", StringComparison.Ordinal)
                    && fragment.Contains(registered.Reference, StringComparison.Ordinal),
                "a removal that threw has to be recorded even though it is not the answer.");
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task A_generation_the_store_cannot_find_is_not_answered_as_an_unavailable_store()
    {
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await RegisterAsync("vanished-generation-producer");
        using (registration)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            var store = new UnreadableObjectStore(AttachmentStoreOpenStatus.Missing);
            UploadAttachment.Handler handler = CreateLoggingHandler(scope.ServiceProvider, store);
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);
            fixture.Logs.Events.Clear();

            Result<UploadAttachment.Response> result = await handler.HandleAsync(
                new UploadAttachment.Command(registered.Reference, content, 4),
                CancellationToken.None);

            // The store answered, so it is reachable. What it answered is that
            // the generation it had just named is not there, and that sends
            // whoever reads it somewhere else entirely.
            result.Error.ShouldBe(ErrorCodes.GenerationUnreadable);
            result.Error.ShouldNotBe(ErrorCodes.StoreUnavailable);
            store.DiscardCalled.ShouldBeTrue();
            string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
            logs.ShouldContain(
                fragment => fragment.Contains("não foi encontrada", StringComparison.Ordinal)
                    && fragment.Contains(registered.Reference, StringComparison.Ordinal),
                "a generation that vanished right after the write has to be recorded.");
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task A_reading_the_store_cannot_answer_is_answered_as_an_unavailable_store()
    {
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await RegisterAsync("unreachable-reading-producer");
        using (registration)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            var store = new UnreadableObjectStore(AttachmentStoreOpenStatus.Unavailable);
            UploadAttachment.Handler handler = CreateLoggingHandler(scope.ServiceProvider, store);
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);

            Result<UploadAttachment.Response> result = await handler.HandleAsync(
                new UploadAttachment.Command(registered.Reference, content, 4),
                CancellationToken.None);

            result.Error.ShouldBe(ErrorCodes.StoreUnavailable);
            store.DiscardCalled.ShouldBeTrue();
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task Bytes_the_store_took_without_naming_a_generation_are_reported_as_such()
    {
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await RegisterAsync("unidentified-write-producer");
        using (registration)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            var store = new UnidentifiedObjectStore();
            UploadAttachment.Handler handler = CreateLoggingHandler(scope.ServiceProvider, store);
            using var content = new MemoryStream("test"u8.ToArray(), writable: false);
            fixture.Logs.Events.Clear();

            Result<UploadAttachment.Response> result = await handler.HandleAsync(
                new UploadAttachment.Command(registered.Reference, content, 4),
                CancellationToken.None);

            // The provider was available, and answering unavailable sent the
            // reader to look at connectivity. There is also no generation to
            // remove, so the only thing left is to say the bytes are there.
            result.Error.ShouldBe(ErrorCodes.StoreUnidentifiedGeneration);
            result.Error.ShouldNotBe(ErrorCodes.StoreUnavailable);
            store.DiscardCalled.ShouldBeFalse();
            string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
            logs.ShouldContain(
                fragment => fragment.Contains("sem nomear a geração", StringComparison.Ordinal)
                    && fragment.Contains(registered.Reference, StringComparison.Ordinal),
                "bytes left without an identity have to be named somewhere.");
            (await fixture.QueryAttachmentAsync(registered.Reference)).State
                .ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    [RequiresDockerFact]
    public async Task Upload_without_a_bearer_token_returns_401()
    {
        using HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await AttachmentApi.PutContentAsync(
            client,
            AttachmentReference.Generate().Value,
            "test");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private void AssertDoesNotLeak(
        IEnumerable<string> responseFragments,
        AttachmentObjectVersion captured,
        string contentId,
        byte[] recordedDigest)
    {
        string[] prohibited =
        [
            AttachmentManagementApiFixture.Bucket,
            AttachmentManagementApiFixture.AccessKey,
            AttachmentManagementApiFixture.SecretKey,
            fixture.AwsEndpoint,
            captured.Key,
            captured.VersionId,
            contentId,
            ContentSentinel,
            AttachmentApi.FileName,
            AttachmentApi.ContentType,

            // The digest is the proof of the bytes and stays on the generation
            // row. Both spellings are listed because a leak picks whichever
            // encoding its caller happened to use.
            Convert.ToHexString(recordedDigest),
            Convert.ToHexString(recordedDigest).ToLowerInvariant(),
            Convert.ToBase64String(recordedDigest),
        ];
        string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
        logs.ShouldNotBeEmpty("the log surface has to be observed through real events.");
        foreach (var value in prohibited)
        {
            responseFragments.ShouldAllBe(fragment =>
                !fragment.Contains(value, StringComparison.Ordinal));
            logs.ShouldAllBe(fragment =>
                !fragment.Contains(value, StringComparison.Ordinal));
        }

        logs.ShouldContain(
            fragment => fragment.Contains(AttachmentObjectLocator.Redacted, StringComparison.Ordinal),
            "the line that carries the locator has to be among the captured events.");
    }

    private async Task<(HttpResponseMessage Registration, AttachmentApi.ApiResponse Registered)>
        RegisterAsync(string principal)
    {
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        return await AttachmentApi.RegisterAsync(client, sizeBytes: 4);
    }

    private async Task<byte[]> RecordedDigestAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .ObjectGenerations
            .AsNoTracking()
            .Where(generation => generation.AttachmentId == attachmentId)
            .Select(generation => generation.Digest)
            .SingleAsync();
    }

    /// <summary>
    /// Builds the handler on the host's own logger, so the events it writes
    /// reach the capture the fixture installed.
    /// </summary>
    private static UploadAttachment.Handler CreateLoggingHandler(
        IServiceProvider serviceProvider,
        IAttachmentObjectStore objectStore)
        => new(
            serviceProvider.GetRequiredService<AttachmentManagementDbContext>(),
            serviceProvider.GetRequiredService<IDbContextFactory<AttachmentManagementDbContext>>(),
            serviceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            objectStore,
            TimeProvider.System,
            serviceProvider.GetRequiredService<ILogger<UploadAttachment.Handler>>());

    private static UploadAttachment.Handler CreateHandler(
        IServiceProvider serviceProvider,
        IAttachmentObjectStore objectStore,
        IAttachmentSaveOperation? saveOperation = null,
        AttachmentManagementDbContext? dbContext = null)
        => new(
            dbContext ?? serviceProvider.GetRequiredService<AttachmentManagementDbContext>(),
            serviceProvider.GetRequiredService<IDbContextFactory<AttachmentManagementDbContext>>(),
            saveOperation ?? serviceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            objectStore,
            TimeProvider.System,
            NullLogger<UploadAttachment.Handler>.Instance);

    private static AttachmentObjectLocator FakeLocator()
        => AttachmentObjectLocator.FromStoredRow(
            AttachmentManagementApiFixture.Bucket,
            $"attachments/{Guid.NewGuid():N}",
            $"{Guid.NewGuid():N}");

    private sealed class CancelAfterWriteStore(CancellationTokenSource cancellation)
        : IAttachmentObjectStore
    {
        private readonly AttachmentObjectLocator _locator = FakeLocator();

        internal bool DiscardCalled { get; private set; }

        internal CancellationToken DiscardCancellationToken { get; private set; }

        public Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = content;
            _ = cancellationToken;
            cancellation.Cancel();
            return Task.FromResult(AttachmentObjectCapture.Captured(_locator));
        }

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AttachmentStoreOpen.Unavailable());
        }

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            DiscardCalled = true;
            DiscardCancellationToken = cancellationToken;
            return Task.FromResult(AttachmentObjectDiscard.Removed);
        }
    }

    private sealed class ExistingObjectStore : IAttachmentObjectStore
    {
        internal bool DiscardCalled { get; private set; }

        public Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = content;
            _ = cancellationToken;
            return Task.FromResult(AttachmentObjectCapture.AlreadyExists());
        }

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            return Task.FromResult(AttachmentStoreOpen.Missing());
        }

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            DiscardCalled = true;
            return Task.FromResult(AttachmentObjectDiscard.Removed);
        }
    }

    /// <summary>
    /// Keeps only the first <paramref name="keptBytes"/> of what it is handed,
    /// which is how a store that accepted a write and kept less than the
    /// caller sent reads on the way back.
    /// </summary>
    private sealed class StoredObjectStore(long keptBytes) : IAttachmentObjectStore
    {
        private readonly AttachmentObjectLocator _locator = FakeLocator();
        private byte[] _durable = [];

        internal bool DiscardCalled => DiscardCallCount > 0;

        internal int DiscardCallCount { get; private set; }

        internal CancellationToken DiscardCancellationToken { get; private set; }

        /// <summary>What the store answers about a removal it was asked for.</summary>
        internal AttachmentObjectDiscard DiscardAnswer { get; init; }
            = AttachmentObjectDiscard.Removed;

        /// <summary>
        /// Set to make the removal throw instead of answering, which is the
        /// state a store reaches when a failure class nobody mapped comes back
        /// from the client library.
        /// </summary>
        internal Exception? DiscardFailure { get; init; }

        internal AttachmentObjectLocator Locator => _locator;

        public async Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            _ = request;
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var written = buffer.ToArray();
            _durable = written[..(int)Math.Min(keptBytes, written.Length)];
            return AttachmentObjectCapture.Captured(_locator);
        }

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            return Task.FromResult(AttachmentStoreOpen.Opened(
                new MemoryStream(_durable, writable: false),
                owner: null));
        }

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            DiscardCallCount++;
            DiscardCancellationToken = cancellationToken;
            return DiscardFailure is { } failure
                ? Task.FromException<AttachmentObjectDiscard>(failure)
                : Task.FromResult(DiscardAnswer);
        }
    }

    /// <summary>
    /// Takes the write and then answers the reading with the status it was
    /// built with, which is how a store that cannot deliver the generation it
    /// just named reads from the caller's side.
    /// </summary>
    private sealed class UnreadableObjectStore(AttachmentStoreOpenStatus reading)
        : IAttachmentObjectStore
    {
        private readonly AttachmentObjectLocator _locator = FakeLocator();

        internal bool DiscardCalled { get; private set; }

        public async Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            _ = request;
            using var drain = new MemoryStream();
            await content.CopyToAsync(drain, cancellationToken);
            return AttachmentObjectCapture.Captured(_locator);
        }

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            return Task.FromResult(reading == AttachmentStoreOpenStatus.Missing
                ? AttachmentStoreOpen.Missing()
                : AttachmentStoreOpen.Unavailable());
        }

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            DiscardCalled = true;
            return Task.FromResult(AttachmentObjectDiscard.Removed);
        }
    }

    /// <summary>
    /// Takes the write and answers no generation for it, which is the shape of
    /// a store that does not keep generations at all.
    /// </summary>
    private sealed class UnidentifiedObjectStore : IAttachmentObjectStore
    {
        internal bool DiscardCalled { get; private set; }

        public async Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            _ = request;
            using var drain = new MemoryStream();
            await content.CopyToAsync(drain, cancellationToken);
            return AttachmentObjectCapture.Unidentified();
        }

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            return Task.FromResult(AttachmentStoreOpen.Missing());
        }

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            DiscardCalled = true;
            return Task.FromResult(AttachmentObjectDiscard.Removed);
        }
    }

    private sealed class CommitThenThrowSaveOperation : IAttachmentSaveOperation
    {
        internal const string FailureMessage = "Simulated transport failure after commit.";

        public async Task SaveChangesAsync(
            AttachmentManagementDbContext dbContext,
            CancellationToken cancellationToken)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new IOException(FailureMessage);
        }
    }

    private sealed class ThrowBeforeCommitSaveOperation : IAttachmentSaveOperation
    {
        internal const string FailureMessage = "Simulated failure before commit.";

        public Task SaveChangesAsync(
            AttachmentManagementDbContext dbContext,
            CancellationToken cancellationToken)
        {
            _ = dbContext;
            _ = cancellationToken;
            return Task.FromException(new IOException(FailureMessage));
        }
    }

    private sealed class ThrowingDbContextFactory
        : IDbContextFactory<AttachmentManagementDbContext>
    {
        public AttachmentManagementDbContext CreateDbContext()
            => throw new IOException("Simulated verification failure.");
    }
}
