using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.TemplateManagement;
using Npgsql;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// The sweep that removes the content of abandoned attachments, against the
/// store and the database the module runs on.
/// <para>
/// Every case arranges real durable bytes and reads the store back
/// afterwards. An arrangement that only wrote rows would grade the sweep's
/// bookkeeping and never its effect, and the effect is the only thing here
/// that cannot be undone.
/// </para>
/// <para>
/// The whole arrangement lives years in the past and every round is run with
/// a clock from those same years. The suite shares a database with every other
/// attachment test, and a clock in the future would make every neighbour's
/// attachment abandoned at once; dated this way, no row but this file's can be
/// due, and each oracle is still stated per attachment rather than as a count
/// over the table.
/// </para>
/// </summary>
[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentAbandonmentTests(AttachmentManagementApiFixture fixture)
{
    private const string Content = "conteudo-abandonado";
    private const string MediaType = "application/pdf";

    private static readonly DateTimeOffset Registered =
        new(2020, 1, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A year between the registration and the act that puts the attachment in
    /// its state. It is what separates a window counted from the last act from
    /// one counted from the creation: read from the creation, three of the four
    /// states would be abandoned a year early.
    /// </summary>
    private static readonly DateTimeOffset Acted = Registered.AddYears(1);

    /// <summary>
    /// Four different windows on purpose, so a rule that read one of them for
    /// another state fails instead of agreeing by coincidence. All four are
    /// above the floor the startup guard enforces.
    /// </summary>
    private static readonly AttachmentRetentionWindows Windows = new(
        UnstartedUpload: TimeSpan.FromDays(7),
        UnvalidatedContent: TimeSpan.FromDays(5),
        RefusedContent: TimeSpan.FromDays(3),
        WithdrawnRelease: TimeSpan.FromDays(11));

    public static TheoryData<string> AbandonableStates()
        => new()
        {
            AttachmentStates.AwaitingUpload,
            AttachmentStates.Received,
            AttachmentStates.Rejected,
            AttachmentStates.Revoked,
        };

    public static TheoryData<string, string> ProtectedByDependency()
    {
        var data = new TheoryData<string, string>();
        foreach (var state in (string[])
        [
            AttachmentStates.AwaitingUpload,
            AttachmentStates.Received,
            AttachmentStates.Rejected,
            AttachmentStates.Revoked,
        ])
        {
            data.Add(state, AttachmentDependencyReasons.ClaimConfirmed);
            data.Add(state, AttachmentDependencyReasons.AttemptSending);
            data.Add(state, AttachmentDependencyReasons.AttemptUnknown);

            // A reason this module never listed protects the content just as
            // well, because what makes a dependency live is the absence of a
            // release. No later filter on the reason can pass unnoticed.
            data.Add(state, "reason-nobody-listed");
        }

        return data;
    }

    /// <summary>
    /// The content of each abandonable state, before its window runs out and
    /// after. The pair is the whole oracle: the round before the deadline is
    /// what keeps the round after it from being satisfied by a sweep that
    /// removes everything it is offered.
    /// </summary>
    [Theory]
    [MemberData(nameof(AbandonableStates))]
    public async Task Content_is_taken_at_the_deadline_of_its_state_and_not_before(string state)
    {
        AbandonedAttachment seeded = await SeedAsync(state);
        DateTimeOffset deadline = DeadlineOf(state);

        await RunRoundAsync(deadline - TimeSpan.FromTicks(1));

        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);
        (await fixture.QueryAttachmentAsync(seeded.Reference)).State.ShouldBe(state);

        await RunRoundAsync(deadline);

        (await StoredVersionsAsync(seeded)).ShouldBeEmpty();
        Attachment discarded = await fixture.QueryAttachmentAsync(seeded.Reference);
        discarded.State.ShouldBe(AttachmentStates.Discarded);

        // The record outlives the content. Nothing is purged from it, and the
        // line of the generation still says what was stored and how long it
        // was, which is the only remaining answer to what this attachment held.
        await AssertRecordSurvivedAsync(seeded, state);
    }

    /// <summary>
    /// The window of every state but the first is counted from the act that
    /// put the attachment there, and the registration is a year older than
    /// that act. A rule reading the creation instant takes the content of all
    /// three here.
    /// </summary>
    [Theory]
    [InlineData(AttachmentStates.Received)]
    [InlineData(AttachmentStates.Rejected)]
    [InlineData(AttachmentStates.Revoked)]
    public async Task A_window_counts_from_the_last_act_and_never_from_the_registration(
        string state)
    {
        AbandonedAttachment seeded = await SeedAsync(state);

        // Past every window if they were counted from the registration, and
        // before all three of the real ones.
        await RunRoundAsync(Registered.AddDays(30));

        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);
        (await fixture.QueryAttachmentAsync(seeded.Reference)).State.ShouldBe(state);

        await RunRoundAsync(DeadlineOf(state));

        (await StoredVersionsAsync(seeded)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.Discarded);
    }

    /// <summary>
    /// The protection, in every state the rule can reach and for every reason
    /// a dependent can declare, with the clock a year past the deadline. Time
    /// alone never ends a dependency and a dependency never stops protecting.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProtectedByDependency))]
    public async Task A_live_dependency_keeps_the_content_whatever_the_state_and_the_clock(
        string state,
        string reason)
    {
        AbandonedAttachment seeded = await SeedAsync(state);
        await HoldAsync(seeded, reason);

        AttachmentAbandonmentResult round = await RunRoundAsync(LongAfter(state));

        round.Preserved.ShouldBeGreaterThanOrEqualTo(1);
        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);
        (await fixture.QueryAttachmentAsync(seeded.Reference)).State.ShouldBe(state);
        await AssertRecordSurvivedAsync(seeded, state);

        // And the same attachment, once nothing depends on it any more, is
        // taken by the next round. Without this the assertions above would
        // also hold for a sweep that never removes anything at all.
        await EndHoldAsync(seeded);
        await RunRoundAsync(LongAfter(state));

        (await StoredVersionsAsync(seeded)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.Discarded);
    }

    /// <summary>
    /// A release is never abandoned, whatever its age. It is the one state the
    /// whole flow exists to produce, and its expiry is computed from a validity
    /// read at comparison time, so a sweep keyed on it would remove content
    /// that the next change to that value would make usable again.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_release_is_never_abandoned_however_old_it_is()
    {
        AbandonedAttachment seeded = await SeedAsync(AttachmentStates.Released);

        AttachmentAbandonmentResult round = await RunRoundAsync(LongAfter(AttachmentStates.Revoked));

        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.Released);

        // The round did run and did take the attachments it may take, so the
        // reading above is about this state and not about a round that found
        // nothing to do.
        AbandonedAttachment refused = await SeedAsync(AttachmentStates.Rejected);
        await RunRoundAsync(LongAfter(AttachmentStates.Revoked));
        (await StoredVersionsAsync(refused)).ShouldBeEmpty();
        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);
        round.Examined.ShouldBeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// The generation nobody recorded, which is what a write whose answer was
    /// lost leaves behind. Nothing names it anywhere, and this is the one job
    /// that can reach it: the request that meets the resulting conflict cannot
    /// annotate the row without breaking a concurrent upload that is about to
    /// succeed, and here the row is held from the decision until after the
    /// removal.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_generation_nobody_recorded_is_taken_with_the_rest_of_the_key()
    {
        AbandonedAttachment seeded = await SeedAsync(AttachmentStates.AwaitingUpload);
        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);

        // Nothing in the record names it: the sweep reaches it by listing the
        // key the row derives, and not by reading the generations.
        (await RecordedGenerationsAsync(seeded.Id)).ShouldBe(0);

        await RunRoundAsync(DeadlineOf(AttachmentStates.AwaitingUpload) - TimeSpan.FromTicks(1));
        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);

        AttachmentAbandonmentResult round = await RunRoundAsync(
            DeadlineOf(AttachmentStates.AwaitingUpload));

        round.UnrecordedRemoved.ShouldBeGreaterThanOrEqualTo(1);
        (await StoredVersionsAsync(seeded)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.Discarded);
    }

    /// <summary>
    /// The same unaccounted generation, on a row that was annotated as owing a
    /// reclaim. The sweep carries the reclaim out by construction, so it takes
    /// the annotation back in the transaction that removes the bytes; left
    /// there it would send the repair round to a key with nothing under it,
    /// round after round, for an attachment nobody can upload to any more.
    /// </summary>
    [RequiresDockerFact]
    public async Task Taking_the_content_takes_back_the_reclaim_the_row_owed()
    {
        AbandonedAttachment seeded = await SeedAsync(AttachmentStates.AwaitingUpload);
        await WriteLiabilityAsync(seeded.Reference, AttachmentLiabilities.CustodyUnreclaimed);
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .ReconciliationLiability.ShouldBe(AttachmentLiabilities.CustodyUnreclaimed);

        await RunRoundAsync(DeadlineOf(AttachmentStates.AwaitingUpload));

        Attachment discarded = await fixture.QueryAttachmentAsync(seeded.Reference);
        discarded.State.ShouldBe(AttachmentStates.Discarded);
        discarded.ReconciliationLiability.ShouldBeNull();
        (await StoredVersionsAsync(seeded)).ShouldBeEmpty();
    }

    /// <summary>
    /// An inventory the store could not complete stops the sweep before it can
    /// say the key is empty, so the state is not written and the next round
    /// meets the attachment again. What was already removed stays removed:
    /// the recorded generations were named exactly and their removal was
    /// confirmed.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_inventory_the_store_could_not_complete_leaves_the_state_unwritten()
    {
        AbandonedAttachment seeded = await SeedAsync(AttachmentStates.Rejected);

        AttachmentAbandonmentResult blind = await RunRoundAsync(
            DeadlineOf(AttachmentStates.Rejected),
            inventory: new UnavailableAttachmentObjectStore());

        blind.Discarded.ShouldBe(0);
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.Rejected);

        await RunRoundAsync(DeadlineOf(AttachmentStates.Rejected));

        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.Discarded);
        (await StoredVersionsAsync(seeded)).ShouldBeEmpty();
    }

    /// <summary>
    /// A removal the store refused leaves the key occupied, so the state is
    /// not written either. Saying the content is gone while bytes are still
    /// under the key would take the row out of the selection with nothing left
    /// to find it by.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_removal_the_store_refused_leaves_the_state_unwritten()
    {
        AbandonedAttachment seeded = await SeedAsync(AttachmentStates.AwaitingUpload);

        await RunRoundAsync(
            DeadlineOf(AttachmentStates.AwaitingUpload),
            objectStore: new RefusingRemovalStore(RealStore()));

        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.AwaitingUpload);

        await RunRoundAsync(DeadlineOf(AttachmentStates.AwaitingUpload));

        (await StoredVersionsAsync(seeded)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.Discarded);
    }

    /// <summary>
    /// The candidate that stopped being abandoned between the selection and
    /// the row being taken. It is the interleaving this job could do real harm
    /// in: the producer of an attachment abandoned for days may be uploading
    /// to it right now, and a sweep that trusted its own selection would
    /// remove the bytes of that upload.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_candidate_that_stopped_being_abandoned_keeps_its_content()
    {
        AbandonedAttachment seeded = await SeedAsync(
            AttachmentStates.AwaitingUpload, withContent: false);
        var candidate = new AttachmentAbandonmentCandidate(
            seeded.Id,
            AttachmentReference.Create(seeded.Reference).Value.ShouldNotBeNull(),
            seeded.ContentId,
            AttachmentStates.AwaitingUpload);

        // What the producer did between the selection and now: the bytes
        // arrived, so the attachment is in a state whose own window has not
        // begun to run.
        await ReceiveAsync(seeded, DeadlineOf(AttachmentStates.AwaitingUpload));

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentAbandonmentReport report = await ScanIn(scope).DiscardAsync(
            candidate,
            DeadlineOf(AttachmentStates.AwaitingUpload),
            Windows,
            CancellationToken.None);

        report.Outcome.ShouldBe(AttachmentAbandonmentOutcome.NotAbandoned);
        report.GenerationsRemoved.ShouldBe(0);
        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.Received);
    }

    /// <summary>
    /// The upload a producer sends after the content was discarded. It has to
    /// be refused by the state, because nothing else refuses it any more: what
    /// kept a repeat out of a settled attachment was the conditional write
    /// meeting an occupied key, and removing those bytes is exactly what frees
    /// the key again.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_upload_over_discarded_content_is_refused_and_stores_nothing()
    {
        var principal = $"abandonment-producer-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, Encoding.UTF8.GetByteCount(Content));
        using (registration)
        {
            registration.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (HttpResponseMessage accepted = await AttachmentApi.PutContentAsync(
            client, registered.Reference, Content))
        {
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Attachment received = await fixture.QueryAttachmentAsync(registered.Reference);
        var key = AttachmentObjectKeys.For(received.ContentId);
        (await StoredVersionsAsync(key)).Length.ShouldBe(1);

        // The window of this state, counted from the instant the endpoint
        // wrote, so the round is due for this attachment and for no neighbour
        // that arrived after it.
        await RunRoundAsync(received.ReceivedAt!.Value + Windows.UnvalidatedContent);
        (await StoredVersionsAsync(key)).ShouldBeEmpty();

        using HttpResponseMessage refused = await AttachmentApi.PutContentAsync(
            client, registered.Reference, Content);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).ShouldContain(ErrorCodes.Discarded);
        (await StoredVersionsAsync(key)).ShouldBeEmpty();
        (await fixture.QueryAttachmentAsync(registered.Reference))
            .State.ShouldBe(AttachmentStates.Discarded);
    }

    /// <summary>
    /// Why no line of the record is purged with the bytes, measured rather
    /// than argued. A release names the exact generation it was granted over,
    /// and the database refuses to remove a line a release names; an
    /// attachment that was released and then had its release taken back never
    /// had a dependency at all, so a purge keyed on that would fail on exactly
    /// the population it was written for.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_line_a_release_names_cannot_be_purged_even_with_the_bytes_gone()
    {
        AbandonedAttachment seeded = await SeedAsync(AttachmentStates.Revoked);
        (await LiveDependenciesAsync(seeded.Id)).ShouldBe(0);
        (await AllDependenciesAsync(seeded.Id)).ShouldBe(0);

        await RunRoundAsync(DeadlineOf(AttachmentStates.Revoked));
        (await StoredVersionsAsync(seeded)).ShouldBeEmpty();

        DbUpdateException failure = await Should.ThrowAsync<DbUpdateException>(
            () => PurgeGenerationAsync(seeded.Id));

        failure.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        (await RecordedGenerationsAsync(seeded.Id)).ShouldBe(1);
    }

    /// <summary>
    /// A window nobody set is not a window of zero. The guard at startup
    /// refuses the value, and the rule refuses it again, so a section that
    /// somehow arrived empty removes nothing instead of removing everything.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_retention_nobody_configured_takes_no_content_at_all()
    {
        AbandonedAttachment seeded = await SeedAsync(AttachmentStates.Rejected);

        await RunRoundAsync(
            LongAfter(AttachmentStates.Revoked),
            windows: new AttachmentRetentionWindows(
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));

        (await StoredVersionsAsync(seeded)).Length.ShouldBe(1);
        (await fixture.QueryAttachmentAsync(seeded.Reference))
            .State.ShouldBe(AttachmentStates.Rejected);

        await RunRoundAsync(DeadlineOf(AttachmentStates.Rejected));

        (await StoredVersionsAsync(seeded)).ShouldBeEmpty();
    }

    /// <summary>
    /// Long past the deadline of the state, and still years before the
    /// instants the neighbouring suites write with the wall clock. A round run
    /// with a clock past those would find every attachment in the shared
    /// database abandoned at once, and the harm would land on a suite that
    /// never asked for a sweep.
    /// </summary>
    private static DateTimeOffset LongAfter(string state)
        => DeadlineOf(state).AddYears(1);

    private static DateTimeOffset DeadlineOf(string state)
        => state switch
        {
            AttachmentStates.AwaitingUpload => Registered + Windows.UnstartedUpload,
            AttachmentStates.Received => Acted + Windows.UnvalidatedContent,
            AttachmentStates.Rejected => Acted + Windows.RefusedContent,
            AttachmentStates.Revoked => Acted + Windows.WithdrawnRelease,
            _ => Acted.AddYears(100),
        };

    /// <summary>
    /// One round, composed as the maintenance role composes it, with a clock
    /// and a store the case chooses.
    /// </summary>
    private async Task<AttachmentAbandonmentResult> RunRoundAsync(
        DateTimeOffset now,
        IAttachmentObjectInventory? inventory = null,
        IAttachmentObjectStore? objectStore = null,
        AttachmentRetentionWindows? windows = null)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await ScanIn(scope, inventory, objectStore, windows, now)
            .RunAsync(CancellationToken.None);
    }

    private AttachmentAbandonmentScan ScanIn(
        IServiceScope scope,
        IAttachmentObjectInventory? inventory = null,
        IAttachmentObjectStore? objectStore = null,
        AttachmentRetentionWindows? windows = null,
        DateTimeOffset? now = null)
    {
        AttachmentRetentionWindows applied = windows ?? Windows;
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        return new AttachmentAbandonmentScan(
            dbContext,
            new AttachmentDisposal(
                dbContext,
                objectStore ?? RealStore(),
                scope.ServiceProvider.GetRequiredService<ILogger<AttachmentDisposal>>()),
            inventory ?? scope.ServiceProvider.GetRequiredService<IAttachmentObjectInventory>(),
            objectStore ?? RealStore(),
            Options.Create(new AttachmentRetentionOptions
            {
                UnstartedUpload = applied.UnstartedUpload,
                UnvalidatedContent = applied.UnvalidatedContent,
                RefusedContent = applied.RefusedContent,
                WithdrawnRelease = applied.WithdrawnRelease,
            }),
            new MutableTimeProvider(now ?? Acted),
            scope.ServiceProvider.GetRequiredService<ILogger<AttachmentAbandonmentScan>>());
    }

    /// <summary>
    /// One attachment in the state named, with its bytes really in custody
    /// when the state has any, written through the module's own store and
    /// pinned to the generation that write returned.
    /// <para>
    /// Every transition goes through the aggregate, so nothing here arranges a
    /// state the module cannot reach, and every instant is chosen: the
    /// registration is a year older than the act that ends the attachment,
    /// which is what a window counted from the wrong one fails on.
    /// </para>
    /// </summary>
    private async Task<AbandonedAttachment> SeedAsync(string state, bool withContent = true)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        var bytes = Encoding.UTF8.GetBytes(Content);
        Attachment attachment = Attachment
            .Register(
                $"abandonment-{Guid.NewGuid():N}",
                "comprovante.pdf",
                MediaType,
                bytes.Length,
                AttachmentApi.SeedSizeCeiling,
                Registered)
            .Value
            .ShouldNotBeNull();
        dbContext.Attachments.Add(attachment);

        if (state != AttachmentStates.AwaitingUpload)
        {
            withContent.ShouldBeTrue(
                "só o estado que espera o envio pode ser arranjado sem conteúdo; nos demais "
                + "os bytes são o que o caso mede.");

            AttachmentObjectCapture capture = await RealStore().PutAsync(
                new AttachmentObjectRequest(attachment.ContentId, MediaType, bytes.Length),
                new MemoryStream(bytes, writable: false),
                CancellationToken.None);
            capture.Status.ShouldBe(
                AttachmentObjectCaptureStatus.Captured,
                "o arranjo precisa dos bytes realmente na custódia; sem eles o caso mediria "
                + "uma chave vazia antes e depois.");
            AttachmentObjectGeneration generation = AttachmentObjectGeneration.Capture(
                attachment.Id,
                capture.Locator.ShouldNotBeNull(),
                AttachmentContentProof.Sha256Of(SHA256.HashData(bytes), bytes.Length),
                MediaType,
                Acted);
            attachment.MarkReceived(bytes.Length, Acted)
                .ShouldBe(AttachmentReceiveOutcome.Received);
            dbContext.ObjectGenerations.Add(generation);

            if (state is AttachmentStates.Released or AttachmentStates.Revoked)
            {
                attachment.Release().ShouldBe(AttachmentValidationTransition.Applied);
                dbContext.Releases.Add(AttachmentRelease.Grant(
                    attachment.Id, generation.Id, Acted, TimeSpan.FromDays(30)));
            }

            if (state == AttachmentStates.Revoked)
            {
                attachment.Revoke(Acted).ShouldBe(AttachmentRevocationTransition.Applied);
            }

            if (state == AttachmentStates.Rejected)
            {
                attachment.Reject("content-type-not-admitted", Acted)
                    .ShouldBe(AttachmentValidationTransition.Applied);
            }
        }

        attachment.State.ShouldBe(state);
        await dbContext.SaveChangesAsync();

        // The one shape durable bytes can take under an attachment whose
        // upload never landed: a generation nothing recorded, which is what a
        // write whose answer was lost leaves behind. Arranged here so every
        // state in this file has content to lose, and so the case that asks
        // about it can read the record and find nothing naming it.
        if (state == AttachmentStates.AwaitingUpload && withContent)
        {
            await PlantUnrecordedAsync(attachment.ContentId);
        }

        return new AbandonedAttachment(
            attachment.Reference.Value, attachment.Id, attachment.ContentId);
    }

    /// <summary>
    /// The bytes arriving on an attachment the sweep already picked as a
    /// candidate, written through the aggregate and the store exactly as an
    /// upload writes them.
    /// </summary>
    private async Task ReceiveAsync(AbandonedAttachment seeded, DateTimeOffset receivedAt)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        var bytes = Encoding.UTF8.GetBytes(Content);
        AttachmentObjectCapture capture = await RealStore().PutAsync(
            new AttachmentObjectRequest(seeded.ContentId, MediaType, bytes.Length),
            new MemoryStream(bytes, writable: false),
            CancellationToken.None);
        capture.Status.ShouldBe(AttachmentObjectCaptureStatus.Captured);

        Attachment attachment = await dbContext.Attachments
            .SingleAsync(candidate => candidate.Id == seeded.Id);
        attachment.MarkReceived(bytes.Length, receivedAt)
            .ShouldBe(AttachmentReceiveOutcome.Received);
        dbContext.ObjectGenerations.Add(AttachmentObjectGeneration.Capture(
            attachment.Id,
            capture.Locator.ShouldNotBeNull(),
            AttachmentContentProof.Sha256Of(SHA256.HashData(bytes), bytes.Length),
            MediaType,
            receivedAt));
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Bytes straight under the key the record derives, with none of it
    /// reaching the record. It is the durable shape of a write this module
    /// never learned the outcome of.
    /// </summary>
    private async Task PlantUnrecordedAsync(Guid contentId)
    {
        // Spelled out here as well as derived in production, so the two are
        // held against each other instead of agreeing with themselves.
        AttachmentObjectKeys.For(contentId).ShouldBe($"attachments/{contentId:N}");
        await fixture.S3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = AttachmentManagementApiFixture.Bucket,
            Key = AttachmentObjectKeys.For(contentId),
            ContentBody = "geracao-nao-registrada",
        });
    }

    private async Task HoldAsync(AbandonedAttachment seeded, string reason)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        (await scope.ServiceProvider
            .GetRequiredService<AttachmentDependencyRegistry>()
            .HoldAsync(
                AttachmentReference.Create(seeded.Reference).Value.ShouldNotBeNull(),
                reason,
                $"holder-{Guid.NewGuid():N}",
                CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);
        (await LiveDependenciesAsync(seeded.Id)).ShouldBe(1);
    }

    private async Task EndHoldAsync(AbandonedAttachment seeded)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        var holder = await dbContext.AttachmentDependencies
            .AsNoTracking()
            .Where(dependency => dependency.AttachmentId == seeded.Id
                && dependency.ReleasedAt == null)
            .Select(dependency => dependency.Holder)
            .SingleAsync();
        (await scope.ServiceProvider
            .GetRequiredService<AttachmentDependencyRegistry>()
            .ReleaseAsync(
                AttachmentReference.Create(seeded.Reference).Value.ShouldNotBeNull(),
                holder,
                CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);
        (await LiveDependenciesAsync(seeded.Id)).ShouldBe(0);
    }

    private async Task<int> LiveDependenciesAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .AttachmentDependencies
            .AsNoTracking()
            .CountAsync(dependency => dependency.AttachmentId == attachmentId
                && dependency.ReleasedAt == null);
    }

    private async Task<int> AllDependenciesAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .AttachmentDependencies
            .AsNoTracking()
            .CountAsync(dependency => dependency.AttachmentId == attachmentId);
    }

    private async Task<int> RecordedGenerationsAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .ObjectGenerations
            .AsNoTracking()
            .CountAsync(generation => generation.AttachmentId == attachmentId);
    }

    /// <summary>
    /// What the record still says once the bytes are gone. Nothing is purged
    /// with them, and the line keeps the measurement of the content it named.
    /// </summary>
    private async Task AssertRecordSurvivedAsync(AbandonedAttachment seeded, string state)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        List<AttachmentObjectGeneration> generations = await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .ObjectGenerations
            .AsNoTracking()
            .Where(generation => generation.AttachmentId == seeded.Id)
            .ToListAsync();

        // The count is asserted exactly, and it is what makes this an oracle
        // at all: read as a walk over whatever is left, it would be satisfied
        // by a record that had been emptied.
        generations.Count.ShouldBe(state == AttachmentStates.AwaitingUpload ? 0 : 1);
        foreach (AttachmentObjectGeneration generation in generations)
        {
            generation.LengthBytes.ShouldBe(Encoding.UTF8.GetByteCount(Content));
            generation.Version.ShouldNotBeNullOrWhiteSpace();
            generation.Digest.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(Content)));
        }
    }

    private async Task PurgeGenerationAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        List<AttachmentObjectGeneration> generations = await dbContext.ObjectGenerations
            .Where(generation => generation.AttachmentId == attachmentId)
            .ToListAsync();
        generations.ShouldNotBeEmpty();
        dbContext.ObjectGenerations.RemoveRange(generations);
        await dbContext.SaveChangesAsync();
    }

    private async Task WriteLiabilityAsync(string reference, string liability)
    {
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE attachmentmanagement.attachment SET reconciliation_liability = @liability "
            + "WHERE reference = @reference";
        command.Parameters.AddWithValue("liability", liability);
        command.Parameters.AddWithValue("reference", reference);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    /// <summary>
    /// The generations under exactly one key. The provider enumerates by
    /// prefix, so the equality is what keeps a neighbouring key from being
    /// counted as this attachment's.
    /// </summary>
    private Task<AttachmentObjectVersion[]> StoredVersionsAsync(AbandonedAttachment seeded)
        => StoredVersionsAsync(AttachmentObjectKeys.For(seeded.ContentId));

    private async Task<AttachmentObjectVersion[]> StoredVersionsAsync(string key)
        => [.. (await fixture.ObjectVersionsAsync(key))
            .Where(version => !version.IsDeleteMarker
                && string.Equals(version.Key, key, StringComparison.Ordinal))];

    private IAttachmentObjectStore RealStore()
        => fixture.Services.GetRequiredService<IAttachmentObjectStore>();

    private sealed record AbandonedAttachment(string Reference, Guid Id, Guid ContentId);

    /// <summary>
    /// Writes through and refuses every removal, which is how a store that
    /// cannot be reached at removal time reads from the sweep.
    /// </summary>
    private sealed class RefusingRemovalStore(IAttachmentObjectStore inner)
        : IAttachmentObjectStore
    {
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
            return Task.FromResult(AttachmentObjectDiscard.Unavailable);
        }
    }
}
