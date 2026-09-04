using System.Net;
using System.Text;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.TemplateManagement;
using Npgsql;
using System.Data.Common;
using NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// The repairs that failures outside the transaction leave behind, and the
/// round that carries them out.
/// <para>
/// Every case here arranges a real durable residue and not a description of
/// one: the bytes are written to the store the module runs on, the key is the
/// one the record derives, and what a round removes is read back from the
/// store afterwards. An arrangement that only wrote the word on the row would
/// grade the round's bookkeeping and never its effect.
/// </para>
/// <para>
/// The oracles are stated per attachment rather than as counts over the table.
/// The suite shares a database with every other attachment test, so a count of
/// outstanding repairs measures the neighbours as much as the case; what this
/// arranges is one attachment, and every zero it asserts is paired with the
/// one that preceded it on that same attachment.
/// </para>
/// </summary>
[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentReconciliationTests(AttachmentManagementApiFixture fixture)
{
    private const string Content = "conteudo-de-reconciliacao";
    private static readonly TimeSpan Window = TimeSpan.FromHours(6);

    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The store took the bytes and named no generation for them, so nothing
    /// on the upload path could remove them: a removal needs the exact
    /// generation. The key stays occupied and every retry is refused, until a
    /// round asks the store what that key holds.
    /// </summary>
    [RequiresDockerFact]
    public async Task Bytes_kept_without_a_named_generation_are_reclaimed_and_the_key_accepts_a_retry()
    {
        RegisteredAttachment registered = await RegisterAsync();
        await UploadThroughAsync(
            registered,
            new UnnamedGenerationStore(RealStore()));

        Attachment owing = await fixture.QueryAttachmentAsync(registered.Reference);
        owing.ReconciliationLiability.ShouldBe(AttachmentLiabilities.CustodyUnreclaimed);
        (await VersionsOfAsync(registered.ContentId)).Length.ShouldBe(1);

        AttachmentReconciliationResult round = await RunRoundAsync();

        round.GenerationsRemoved.ShouldBeGreaterThanOrEqualTo(1);
        (await VersionsOfAsync(registered.ContentId)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .ReconciliationLiability.ShouldBeNull();

        // The point of the repair, and the only observable that matters to a
        // producer: the upload that was being refused now goes through.
        await RetryShouldSucceedAsync(registered);
    }

    /// <summary>
    /// The record failed and the removal that would have undone the write was
    /// refused by the store, which is the same durable residue reached by a
    /// different road.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_removal_the_store_never_confirmed_is_reclaimed_by_one_round()
    {
        RegisteredAttachment registered = await RegisterAsync();
        var store = new RefusingRemovalStore(RealStore());

        await Should.ThrowAsync<IOException>(() => UploadThroughAsync(
            registered,
            store,
            new ThrowBeforeCommitSaveOperation()));

        store.DiscardCalls.ShouldBe(1);
        Attachment owing = await fixture.QueryAttachmentAsync(registered.Reference);
        owing.ReconciliationLiability.ShouldBe(AttachmentLiabilities.CustodyUnreclaimed);
        owing.State.ShouldBe(AttachmentStates.AwaitingUpload);
        (await VersionsOfAsync(registered.ContentId)).Length.ShouldBe(1);

        await RunRoundAsync();

        (await VersionsOfAsync(registered.ContentId)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .ReconciliationLiability.ShouldBeNull();
        await RetryShouldSucceedAsync(registered);
    }

    /// <summary>
    /// The generation this module never learned about: bytes that reached the
    /// store under the derived key without any of it being recorded, which is
    /// what a write whose answer was lost leaves behind. Nothing names that
    /// generation anywhere, and the key it occupies refuses every upload while
    /// it is there.
    /// <para>
    /// The repair is reached from the record, and the whole of what makes it
    /// reachable is that the key derives from the row: the round asks the
    /// store what that key holds and subtracts what the record accounts for.
    /// How the row comes to carry the repair is a separate question, and the
    /// two tests below measure the answer to it.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_key_taken_by_a_generation_nobody_recorded_is_freed_by_one_round()
    {
        RegisteredAttachment registered = await RegisterAsync();
        await PlantOrphanAsync(registered.ContentId);
        await MarkCustodyAsync(registered.Reference);
        (await VersionsOfAsync(registered.ContentId)).Length.ShouldBe(1);

        using HttpClient client = fixture.CreateProducerClient(registered.Principal);
        using (HttpResponseMessage refused = await AttachmentApi.PutContentAsync(
            client, registered.Reference, Content))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        await RunRoundAsync();

        (await VersionsOfAsync(registered.ContentId)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .ReconciliationLiability.ShouldBeNull();
        await RetryShouldSucceedAsync(registered);
    }

    /// <summary>
    /// A conflict leaves the row exactly as it was, and that is a decision
    /// with a measurement behind it.
    /// <para>
    /// A conflict says somebody else's write holds the key, and that somebody
    /// may be a request that is about to record its generation. The aggregate
    /// carries a row version, so any committed write to the row before that
    /// request saves makes its save fail; a note taken from the losing request
    /// therefore turns a concurrent upload that had already stored its bytes
    /// into a refusal that removes them. The suite that runs two uploads at
    /// once is where that harm was measured, and this holds the shape that
    /// causes it.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_conflict_leaves_the_row_unannotated_because_a_concurrent_upload_may_win()
    {
        RegisteredAttachment registered = await RegisterAsync();
        await PlantOrphanAsync(registered.ContentId);

        using HttpClient client = fixture.CreateProducerClient(registered.Principal);
        using (HttpResponseMessage refused = await AttachmentApi.PutContentAsync(
            client, registered.Reference, Content))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await fixture.QueryAttachmentAsync(registered.Reference))
            .ReconciliationLiability.ShouldBeNull();
        (await VersionsOfAsync(registered.ContentId)).Length.ShouldBe(1);

        // And the repair still reaches it once the row carries the word, so
        // the assertion above is about who writes it and never about whether
        // the bytes can be reclaimed at all.
        await MarkCustodyAsync(registered.Reference);
        await RunRoundAsync();
        (await VersionsOfAsync(registered.ContentId)).ShouldBeEmpty();
    }

    /// <summary>
    /// An inventory the store could not complete stops the repair before
    /// anything is removed, and the row stays in the backlog. The second half
    /// is what makes the first mean something: with a store that answers, the
    /// same row is repaired.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_inventory_the_store_could_not_complete_leaves_the_repair_outstanding()
    {
        RegisteredAttachment registered = await RegisterAsync();
        await PlantOrphanAsync(registered.ContentId);
        await MarkCustodyAsync(registered.Reference);

        await RunRoundAsync(inventory: new UnavailableAttachmentObjectStore());

        (await VersionsOfAsync(registered.ContentId)).Length.ShouldBe(1);
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .ReconciliationLiability.ShouldBe(AttachmentLiabilities.CustodyUnreclaimed);

        await RunRoundAsync();

        (await VersionsOfAsync(registered.ContentId)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .ReconciliationLiability.ShouldBeNull();
    }

    /// <summary>
    /// A verdict that never concluded waits on its own deadline, and nothing
    /// reaches that deadline unless somebody asks for a validation again. The
    /// round is what asks, and only once the deadline has passed.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_wait_is_closed_by_the_first_round_after_its_deadline_and_by_none_before_it()
    {
        RegisteredAttachment registered = await RegisterAsync();
        await UploadThroughAsync(registered, RealStore());
        await HoldInconclusiveAsync(registered.Reference);

        Attachment waiting = await fixture.QueryAttachmentAsync(registered.Reference);
        waiting.State.ShouldBe(AttachmentStates.Inconclusive);
        waiting.ReconciliationLiability.ShouldBe(AttachmentLiabilities.VerdictOpen);
        waiting.InconclusiveUntil.ShouldBe(Now + Window);

        AttachmentReconciliationResult early = await RunRoundAsync(
            clock: new MutableTimeProvider(Now + Window - TimeSpan.FromMinutes(1)));

        early.VerdictsClosed.ShouldBe(0);
        Attachment stillWaiting = await fixture.QueryAttachmentAsync(registered.Reference);
        stillWaiting.State.ShouldBe(AttachmentStates.Inconclusive);
        stillWaiting.ReconciliationLiability.ShouldBe(AttachmentLiabilities.VerdictOpen);

        AttachmentReconciliationResult due = await RunRoundAsync(
            clock: new MutableTimeProvider(Now + Window));

        due.VerdictsClosed.ShouldBeGreaterThanOrEqualTo(1);
        Attachment closed = await fixture.QueryAttachmentAsync(registered.Reference);
        closed.State.ShouldBe(AttachmentStates.Rejected);
        closed.ValidationDetail.ShouldBe(AttachmentValidationDetails.InconclusiveWindowElapsed);
        closed.ReconciliationLiability.ShouldBeNull();
    }

    /// <summary>
    /// A removal the store refused stops the repair and leaves the row in the
    /// backlog. Counting an unconfirmed removal as done is the one mistake
    /// this job cannot recover from by running again: the row would leave the
    /// backlog with the bytes still occupying the key.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_removal_the_store_refused_leaves_the_repair_outstanding()
    {
        RegisteredAttachment registered = await RegisterAsync();
        await PlantOrphanAsync(registered.ContentId);
        await MarkCustodyAsync(registered.Reference);

        await RunRoundAsync(objectStore: new RefusingRemovalStore(RealStore()));

        (await VersionsOfAsync(registered.ContentId)).Length.ShouldBe(1);
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .ReconciliationLiability.ShouldBe(AttachmentLiabilities.CustodyUnreclaimed);

        await RunRoundAsync();

        (await VersionsOfAsync(registered.ContentId)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .ReconciliationLiability.ShouldBeNull();
    }

    /// <summary>
    /// The repair removes what is under one key and never what merely starts
    /// with it. The provider enumerates by prefix, and a key that ends where
    /// another one continues is a neighbour whose bytes belong to a different
    /// attachment.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_key_that_only_starts_with_the_derived_one_keeps_its_bytes()
    {
        RegisteredAttachment registered = await RegisterAsync();
        await PlantOrphanAsync(registered.ContentId);
        var neighbour = AttachmentObjectKeys.For(registered.ContentId) + "-vizinho";
        await PlantAtAsync(neighbour);
        await MarkCustodyAsync(registered.Reference);

        await RunRoundAsync();

        (await VersionsOfAsync(registered.ContentId)).ShouldBeEmpty();
        (await fixture.ObjectVersionsAsync(neighbour))
            .Count(version => !version.IsDeleteMarker)
            .ShouldBe(1);
    }

    /// <summary>
    /// A repeated upload over an attachment whose bytes arrived long ago owes
    /// nothing. The repeat is a repeat and not a residue, and a backlog that
    /// filled up with them would make every round list keys it has nothing to
    /// remove from.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_repeat_over_bytes_the_record_already_accounts_for_owes_nothing()
    {
        RegisteredAttachment registered = await RegisterAsync();
        await UploadThroughAsync(registered, RealStore());
        await ReleaseAsync(registered.Reference);

        using HttpClient client = fixture.CreateProducerClient(registered.Principal);
        using (HttpResponseMessage repeated = await AttachmentApi.PutContentAsync(
            client, registered.Reference, Content))
        {
            repeated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        Attachment untouched = await fixture.QueryAttachmentAsync(registered.Reference);
        untouched.State.ShouldBe(AttachmentStates.Released);
        untouched.ReconciliationLiability.ShouldBeNull();
        (await VersionsOfAsync(registered.ContentId)).Length.ShouldBe(1);
    }

    /// <summary>
    /// A word the round does not recognise is left where it is. The vocabulary
    /// is closed on the writing side, and a round that guessed at an unknown
    /// value would be free to clear a repair it never carried out.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_repair_this_round_does_not_understand_is_left_exactly_as_it_is()
    {
        RegisteredAttachment registered = await RegisterAsync();
        await PlantOrphanAsync(registered.ContentId);
        await WriteLiabilityAsync(registered.Reference, "custody-unreclaimed-tomorrow");

        AttachmentReconciliationResult round = await RunRoundAsync();

        round.Unresolved.ShouldBeGreaterThanOrEqualTo(1);
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .ReconciliationLiability.ShouldBe("custody-unreclaimed-tomorrow");
        (await VersionsOfAsync(registered.ContentId)).Length.ShouldBe(1);
    }

    /// <summary>
    /// The claim owes the round nothing.
    /// <para>
    /// Three things are held together here, because the claim is only
    /// independent if all three hold: the host that answers requests composes
    /// no scheduler at all, so no round can be running while a claim is taken;
    /// an outstanding repair recorded on another attachment is still
    /// outstanding after the claim, so no round ran during it; and the claim
    /// concludes with the whole set. A claim that had waited on a repair would
    /// have to have been preceded by one.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_claim_concludes_with_no_round_composed_and_none_ever_run()
    {
        fixture.Services.GetServices<IHostedService>()
            .ShouldNotContain(service => service is AttachmentReconciliationService);

        var application = $"claim-without-round-{Guid.NewGuid():N}";
        SeededAttachment claimable = await ClaimableAttachments.ReleasedAsync(
            fixture, application);
        RegisteredAttachment owing = await RegisterAsync();
        await PlantOrphanAsync(owing.ContentId);
        await MarkCustodyAsync(owing.Reference);

        AttachmentClaimOutcome outcome = await ClaimAsync(application, claimable.Reference);

        outcome.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        AcceptedAttachmentSet accepted = outcome.Accepted.ShouldNotBeNull();
        accepted.Count.ShouldBe(1);
        accepted.Single().ContentIdentity.ShouldBe(claimable.ContentIdentity);

        (await fixture.QueryAttachmentAsync(owing.Reference))
            .ReconciliationLiability.ShouldBe(AttachmentLiabilities.CustodyUnreclaimed);
        (await VersionsOfAsync(owing.ContentId)).Length.ShouldBe(1);
    }

    private async Task<RegisteredAttachment> RegisterAsync()
    {
        var principal = $"reconciliation-producer-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        (HttpResponseMessage response, AttachmentApi.ApiResponse body) =
            await AttachmentApi.RegisterAsync(client, Encoding.UTF8.GetByteCount(Content));
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        Attachment attachment = await fixture.QueryAttachmentAsync(body.Reference);
        return new RegisteredAttachment(body.Reference, attachment.Id, attachment.ContentId, principal);
    }

    /// <summary>
    /// One upload through the handler the endpoint composes, with the store
    /// and the save the case needs. The handler is built rather than called
    /// through the route because what a case injects is a failure the route
    /// has no way to ask for.
    /// </summary>
    private async Task UploadThroughAsync(
        RegisteredAttachment registered,
        IAttachmentObjectStore objectStore,
        IAttachmentSaveOperation? saveOperation = null)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        UploadAttachment.Handler handler = new(
            scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>(),
            scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<AttachmentManagementDbContext>>(),
            saveOperation
                ?? scope.ServiceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            objectStore,
            TimeProvider.System,
            NullLogger<UploadAttachment.Handler>.Instance);
        await handler.HandleAsync(
            new UploadAttachment.Command(
                registered.Reference,
                new MemoryStream(Encoding.UTF8.GetBytes(Content), writable: false),
                Encoding.UTF8.GetByteCount(Content)),
            CancellationToken.None);
    }

    /// <summary>One round, composed exactly as the maintenance role composes it.</summary>
    private async Task<AttachmentReconciliationResult> RunRoundAsync(
        IAttachmentObjectInventory? inventory = null,
        IAttachmentObjectStore? objectStore = null,
        TimeProvider? clock = null)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        TimeProvider reading = clock ?? new MutableTimeProvider(Now);
        AttachmentReconciliationScan scan = new(
            dbContext,
            inventory ?? scope.ServiceProvider.GetRequiredService<IAttachmentObjectInventory>(),
            objectStore ?? scope.ServiceProvider.GetRequiredService<IAttachmentObjectStore>(),
            new AttachmentValidation(
                dbContext,
                new ApprovingPolicy(),
                scope.ServiceProvider.GetRequiredService<IAttachmentSaveOperation>(),
                Options.Create(new AttachmentValidationOptions { InconclusiveWindow = Window }),
                reading,
                NullLogger<AttachmentValidation>.Instance),
            Options.Create(new AttachmentReconciliationOptions()),
            reading,
            scope.ServiceProvider.GetRequiredService<ILogger<AttachmentReconciliationScan>>());
        return await scan.RunAsync(CancellationToken.None);
    }

    /// <summary>
    /// Takes the attachment into the waiting state through the operation that
    /// owns the state machine, with a verdict that does not conclude.
    /// </summary>
    private async Task HoldInconclusiveAsync(string reference)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentValidation validation = new(
            scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>(),
            new OpenVerdictPolicy(),
            scope.ServiceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            Options.Create(new AttachmentValidationOptions { InconclusiveWindow = Window }),
            new MutableTimeProvider(Now),
            NullLogger<AttachmentValidation>.Instance);
        AttachmentValidationOutcome outcome = await validation.ValidateAsync(
            AttachmentReference.Create(reference).Value.ShouldNotBeNull(),
            CancellationToken.None);
        outcome.Status.ShouldBe(AttachmentValidationStatus.Inconclusive);
    }

    private async Task<AttachmentClaimOutcome> ClaimAsync(string application, string reference)
    {
        IAttachmentClaim claim = fixture.Services.GetRequiredService<IAttachmentClaim>();
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted);
        AttachmentClaimOutcome outcome = await claim.ClaimAsync(
            transaction,
            new AttachmentClaimRequest
            {
                NotificationId = Guid.CreateVersion7(),
                Application = application,
                ClaimKey = $"claim-{Guid.NewGuid():N}",
                References = AttachmentReferences.Of([reference]),
            },
            CancellationToken.None);
        await transaction.CommitAsync();
        return outcome;
    }

    /// <summary>
    /// Writes one generation straight to the store under the key the record
    /// derives, without any of it reaching the record. It is the durable shape
    /// of a write this module never learned the outcome of.
    /// </summary>
    private async Task PlantOrphanAsync(Guid contentId)
    {
        // Spelled out here as well as derived in production, so the two are
        // held against each other. A derivation the arrangement also called
        // would agree with itself for any rule at all, and the repair would go
        // looking wherever the write had gone.
        AttachmentObjectKeys.For(contentId).ShouldBe($"attachments/{contentId:N}");
        await PlantAtAsync(AttachmentObjectKeys.For(contentId));
    }

    private async Task PlantAtAsync(string key)
        => await fixture.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = AttachmentManagementApiFixture.Bucket,
            Key = key,
            ContentBody = "geracao-orfa",
        });

    /// <summary>
    /// Releases the attachment through the operation that owns the state
    /// machine, so the retry that follows meets the state a released
    /// attachment really has.
    /// </summary>
    private async Task ReleaseAsync(string reference)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentValidation validation = new(
            scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>(),
            new ApprovingPolicy(),
            scope.ServiceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            Options.Create(new AttachmentValidationOptions { InconclusiveWindow = Window }),
            new MutableTimeProvider(Now),
            NullLogger<AttachmentValidation>.Instance);
        AttachmentValidationOutcome outcome = await validation.ValidateAsync(
            AttachmentReference.Create(reference).Value.ShouldNotBeNull(),
            CancellationToken.None);
        outcome.Status.ShouldBe(AttachmentValidationStatus.Released);
    }

    private Task MarkCustodyAsync(string reference)
        => WriteLiabilityAsync(reference, AttachmentLiabilities.CustodyUnreclaimed);

    private async Task WriteLiabilityAsync(string reference, string liability)
    {
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE attachmentmanagement.attachment SET reconciliation_liability = @liability "
            + "WHERE reference = @reference";
        DbParameter value = command.CreateParameter();
        value.ParameterName = "liability";
        value.Value = liability;
        command.Parameters.Add(value);
        DbParameter key = command.CreateParameter();
        key.ParameterName = "reference";
        key.Value = reference;
        command.Parameters.Add(key);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    /// <summary>
    /// The generations under exactly one key. The provider enumerates by
    /// prefix, so the equality is what keeps a neighbouring key from being
    /// counted as this attachment's, which is the same confusion the repair
    /// itself has to avoid.
    /// </summary>
    private async Task<AttachmentObjectVersion[]> VersionsOfAsync(Guid contentId)
    {
        var key = AttachmentObjectKeys.For(contentId);
        return [.. (await fixture.ObjectVersionsAsync(key))
            .Where(version => !version.IsDeleteMarker
                && string.Equals(version.Key, key, StringComparison.Ordinal))];
    }

    private async Task RetryShouldSucceedAsync(RegisteredAttachment registered)
    {
        using HttpClient client = fixture.CreateProducerClient(registered.Principal);
        using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
            client, registered.Reference, Content);
        upload.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .State.ShouldBe(AttachmentStates.Received);
    }

    private IAttachmentObjectStore RealStore()
        => fixture.Services.GetRequiredService<IAttachmentObjectStore>();

    private sealed record RegisteredAttachment(
        string Reference,
        Guid Id,
        Guid ContentId,
        string Principal);

    /// <summary>
    /// Writes the bytes through the store the module runs on and then answers
    /// that no generation was named, which is the shape of a store that took a
    /// write and gave back nothing to pin it by.
    /// </summary>
    private sealed class UnnamedGenerationStore(IAttachmentObjectStore inner)
        : IAttachmentObjectStore
    {
        public async Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            AttachmentObjectCapture capture = await inner.PutAsync(
                request, content, cancellationToken);
            capture.Status.ShouldBe(
                AttachmentObjectCaptureStatus.Captured,
                "o arranjo precisa dos bytes realmente gravados; sem eles o teste mediria "
                + "uma chave vazia.");
            return AttachmentObjectCapture.Unidentified();
        }

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
            => inner.OpenAsync(locator, cancellationToken);

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
            => inner.DiscardAsync(locator, cancellationToken);
    }

    /// <summary>
    /// Writes through and refuses every removal, which is how a store that
    /// cannot be reached at compensation time reads from the upload path.
    /// </summary>
    private sealed class RefusingRemovalStore(IAttachmentObjectStore inner)
        : IAttachmentObjectStore
    {
        internal int DiscardCalls { get; private set; }

        public Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
            => inner.PutAsync(request, content, cancellationToken);

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
            => inner.OpenAsync(locator, cancellationToken);

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            DiscardCalls++;
            return Task.FromResult(AttachmentObjectDiscard.Unavailable);
        }
    }

    private sealed class ThrowBeforeCommitSaveOperation : IAttachmentSaveOperation
    {
        public Task SaveChangesAsync(
            AttachmentManagementDbContext dbContext,
            CancellationToken cancellationToken)
        {
            _ = dbContext;
            _ = cancellationToken;
            return Task.FromException(new IOException("Falha simulada antes do commit."));
        }
    }

    /// <summary>Answers a verdict that does not conclude, every time.</summary>
    private sealed class OpenVerdictPolicy : IAttachmentContentPolicy
    {
        public Task<AttachmentPolicyVerdict> EvaluateAsync(
            AttachmentContentSubject subject,
            CancellationToken cancellationToken)
        {
            _ = subject;
            _ = cancellationToken;
            return Task.FromResult(
                AttachmentPolicyVerdict.DidNotConclude("verifier-did-not-answer"));
        }
    }

    /// <summary>
    /// Approves whatever it is asked about. The round hands this to the
    /// validation on purpose: past the deadline the policy is not consulted at
    /// all, so a policy that would release is what shows the deadline is
    /// closing the wait rather than a verdict being asked for again.
    /// </summary>
    private sealed class ApprovingPolicy : IAttachmentContentPolicy
    {
        public Task<AttachmentPolicyVerdict> EvaluateAsync(
            AttachmentContentSubject subject,
            CancellationToken cancellationToken)
        {
            _ = subject;
            _ = cancellationToken;
            return Task.FromResult(AttachmentPolicyVerdict.Approve());
        }
    }
}
