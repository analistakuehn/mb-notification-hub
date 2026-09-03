using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Revocation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// The act that takes a release back, against the database it runs on.
/// <para>
/// What these oracles prove: that a withdrawal writes a row of its own naming
/// the grant in force and never touches the release line; that repeating it
/// writes nothing and keeps the instant and the reason of the first one; that
/// a state carrying no release is refused explicitly; that a withdrawn release
/// is not granted again by repeating a validation; and that a failure to
/// persist leaves nothing behind.
/// </para>
/// <para>
/// What they do not prove: anything about who is entitled to take content
/// back. The act is exposed to the principal that already holds the
/// application's grant, and no oracle here says that is the right authority
/// for it, only that the act is recorded, bounded and safe to repeat.
/// </para>
/// </summary>
[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentRevocationTests(AttachmentManagementApiFixture fixture)
{
    private const string PdfContent = "%PDF-1.7 sample attachment body";
    private const string Reason = "produtor-substituiu-o-arquivo";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-02T12:00:00Z",
        CultureInfo.InvariantCulture);

    private static readonly TimeSpan Validity = TimeSpan.FromDays(30);

    [RequiresDockerFact]
    public async Task Taking_a_release_back_records_the_grant_it_withdrew_and_revises_nothing()
    {
        Attachment released = await ReleasedAttachmentAsync();
        AttachmentRelease grant = (await ReleasesAsync(released.Id)).ShouldHaveSingleItem();
        var clock = new MutableTimeProvider(Now + TimeSpan.FromDays(2));

        AttachmentRevocationStatus status = await RevokeAsync(released.Reference, Reason, clock);

        status.ShouldBe(AttachmentRevocationStatus.Revoked);
        (await fixture.QueryAttachmentAsync(released.Reference.Value)).State
            .ShouldBe(AttachmentStates.Revoked);

        AttachmentRevocation withdrawal =
            (await RevocationsAsync(released.Id)).ShouldHaveSingleItem();
        withdrawal.ReleaseId.ShouldBe(grant.Id);
        withdrawal.Reason.ShouldBe(Reason);
        withdrawal.RevokedAt.ShouldBe(Now + TimeSpan.FromDays(2));

        // The release line is append-only, so the withdrawal had to find a
        // shape of its own. This is the assertion that says it did: the grant
        // is still there, still one row, and still says what it said.
        AttachmentRelease unchanged = (await ReleasesAsync(released.Id)).ShouldHaveSingleItem();
        unchanged.Id.ShouldBe(grant.Id);
        unchanged.ReleasedAt.ShouldBe(Now);
        unchanged.ExpiresAt.ShouldBe(Now + Validity);
    }

    /// <summary>
    /// The retry a caller sends after a lost response. It writes nothing, and
    /// the assertion that matters is the instant: a second row, or a first row
    /// redated, would be a withdrawal that reports when the retry arrived
    /// instead of when the content stopped being deliverable.
    /// </summary>
    [RequiresDockerFact]
    public async Task Repeating_a_withdrawal_adds_no_record_and_moves_no_instant()
    {
        Attachment released = await ReleasedAttachmentAsync();
        var clock = new MutableTimeProvider(Now + TimeSpan.FromDays(2));

        AttachmentRevocationStatus first = await RevokeAsync(released.Reference, Reason, clock);
        clock.Advance(TimeSpan.FromDays(5));
        AttachmentRevocationStatus repeated = await RevokeAsync(
            released.Reference,
            "outro-motivo-qualquer",
            clock);

        first.ShouldBe(AttachmentRevocationStatus.Revoked);
        repeated.ShouldBe(AttachmentRevocationStatus.AlreadyRevoked);

        AttachmentRevocation withdrawal =
            (await RevocationsAsync(released.Id)).ShouldHaveSingleItem();
        withdrawal.RevokedAt.ShouldBe(Now + TimeSpan.FromDays(2));
        withdrawal.Reason.ShouldBe(Reason);
        (await fixture.QueryAttachmentAsync(released.Reference.Value)).State
            .ShouldBe(AttachmentStates.Revoked);
    }

    [RequiresDockerTheory]
    [InlineData(AttachmentStates.AwaitingUpload)]
    [InlineData(AttachmentStates.Received)]
    [InlineData(AttachmentStates.Rejected)]
    public async Task A_state_that_carries_no_release_is_refused_and_written_to_nowhere(
        string state)
    {
        Attachment attachment = await AttachmentInAsync(state);

        AttachmentRevocationStatus status = await RevokeAsync(attachment.Reference, Reason);

        status.ShouldBe(AttachmentRevocationStatus.NotReleased);
        (await fixture.QueryAttachmentAsync(attachment.Reference.Value)).State.ShouldBe(state);
        (await RevocationsAsync(attachment.Id)).ShouldBeEmpty();
    }

    /// <summary>
    /// The rule the whole task turns on. A withdrawal ends the attachment, and
    /// a validation repeated over it is not a revalidation: the policy is never
    /// asked, no release is written, and the release the withdrawal took back
    /// stays exactly one row.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_withdrawn_release_is_not_granted_again_by_repeating_the_validation()
    {
        Attachment released = await ReleasedAttachmentAsync();
        (await RevokeAsync(released.Reference, Reason))
            .ShouldBe(AttachmentRevocationStatus.Revoked);
        var approving = new ApprovingPolicy();

        AttachmentValidationOutcome outcome = await ValidateAsync(released.Reference, approving);

        outcome.Status.ShouldBe(AttachmentValidationStatus.AlreadyDecided);
        approving.Calls.ShouldBe(0);
        (await fixture.QueryAttachmentAsync(released.Reference.Value)).State
            .ShouldBe(AttachmentStates.Revoked);
        (await ReleasesAsync(released.Id)).Length.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_reason_the_durable_state_cannot_hold_leaves_the_release_in_force()
    {
        Attachment released = await ReleasedAttachmentAsync();
        var oversized = new string('m', AttachmentRevocation.MaxReasonLength + 1);

        AttachmentRevocationStatus status = await RevokeAsync(released.Reference, oversized);

        status.ShouldBe(AttachmentRevocationStatus.InvalidReason);
        (await fixture.QueryAttachmentAsync(released.Reference.Value)).State
            .ShouldBe(AttachmentStates.Released);
        (await RevocationsAsync(released.Id)).ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task An_unknown_reference_withdraws_nothing()
    {
        AttachmentRevocationStatus status = await RevokeAsync(
            AttachmentReference.Generate(),
            Reason);

        status.ShouldBe(AttachmentRevocationStatus.UnknownAttachment);
    }

    /// <summary>
    /// A state that says released with no grant under it. The module cannot
    /// name what a withdrawal would be about, so it writes nothing, and nothing
    /// is deliverable either: what a later check reads is the release, and
    /// there is none.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_release_the_module_cannot_name_is_not_withdrawn()
    {
        Attachment seeded = Attachment.Register(
                AttachmentApi.Application,
                "no-release.pdf",
                "application/pdf",
                ByteCount(PdfContent),
                Now)
            .Value
            .ShouldNotBeNull();
        seeded.MarkReceived(ByteCount(PdfContent), Now)
            .ShouldBe(AttachmentReceiveOutcome.Received);
        seeded.Release().ShouldBe(AttachmentValidationTransition.Applied);
        using (IServiceScope seeding = fixture.Services.CreateScope())
        {
            AttachmentManagementDbContext dbContext = Context(seeding);
            dbContext.Attachments.Add(seeded);
            await dbContext.SaveChangesAsync();
        }

        AttachmentRevocationStatus status = await RevokeAsync(seeded.Reference, Reason);

        status.ShouldBe(AttachmentRevocationStatus.ReleaseUnavailable);
        (await fixture.QueryAttachmentAsync(seeded.Reference.Value)).State
            .ShouldBe(AttachmentStates.Released);
        (await RevocationsAsync(seeded.Id)).ShouldBeEmpty();
    }

    /// <summary>
    /// Two grants under one attachment, the shape the release line takes the
    /// day an explicit revalidation writes a second row into it. The withdrawal
    /// names the one in force, because taking back a grant that a later one had
    /// already superseded would leave the later one standing.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_withdrawal_names_the_grant_in_force_and_not_the_one_it_replaced()
    {
        Attachment released = await ReleasedAttachmentAsync();
        AttachmentRelease first = (await ReleasesAsync(released.Id)).ShouldHaveSingleItem();
        AttachmentObjectGeneration generation = await SingleGenerationAsync(released.Id);
        AttachmentRelease second = AttachmentRelease.Grant(
            released.Id,
            generation.Id,
            Now + TimeSpan.FromDays(1),
            Validity);
        using (IServiceScope seeding = fixture.Services.CreateScope())
        {
            AttachmentManagementDbContext dbContext = Context(seeding);
            dbContext.Releases.Add(second);
            await dbContext.SaveChangesAsync();
        }

        AttachmentRevocationStatus status = await RevokeAsync(released.Reference, Reason);

        status.ShouldBe(AttachmentRevocationStatus.Revoked);

        AttachmentRevocation withdrawal =
            (await RevocationsAsync(released.Id)).ShouldHaveSingleItem();
        withdrawal.ReleaseId.ShouldBe(second.Id);
        withdrawal.ReleaseId.ShouldNotBe(first.Id);
    }

    /// <summary>
    /// The storage says one grant is taken back at most once, and it says so
    /// without the state machine's help. The machine refuses the repeat under a
    /// row lock; this is what stands when the repeat arrives on a connection
    /// the lock never covered.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_storage_refuses_a_second_record_of_the_same_withdrawal()
    {
        Attachment released = await ReleasedAttachmentAsync();
        (await RevokeAsync(released.Reference, Reason))
            .ShouldBe(AttachmentRevocationStatus.Revoked);
        AttachmentRevocation existing =
            (await RevocationsAsync(released.Id)).ShouldHaveSingleItem();

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = Context(scope);
        dbContext.Revocations.Add(AttachmentRevocation.Record(
            released.Id,
            existing.ReleaseId,
            "segunda-tentativa",
            Now + TimeSpan.FromDays(9)));

        await Should.ThrowAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
        (await RevocationsAsync(released.Id)).ShouldHaveSingleItem()
            .RevokedAt.ShouldBe(existing.RevokedAt);
    }

    /// <summary>
    /// The state and the row become durable together or not at all. The failure
    /// injected here writes first and fails afterwards, which is the only shape
    /// that measures anything: a save that never writes leaves nothing durable
    /// however the operation is written.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_failure_to_persist_leaves_no_withdrawal_behind()
    {
        Attachment released = await ReleasedAttachmentAsync();

        await Should.ThrowAsync<InvalidOperationException>(
            () => RevokeAsync(
                released.Reference,
                Reason,
                saveOperation: new WritingThenFailingSaveOperation()));

        (await fixture.QueryAttachmentAsync(released.Reference.Value)).State
            .ShouldBe(AttachmentStates.Released);
        (await RevocationsAsync(released.Id)).ShouldBeEmpty();
        (await ReleasesAsync(released.Id)).ShouldHaveSingleItem();
    }

    /// <summary>
    /// What a withdrawal writes to the log. The reference is the correlator and
    /// has to be there; everything that names the bytes, the object, the store
    /// or the credential must not be.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_withdrawal_names_the_attachment_and_nothing_that_locates_its_bytes()
    {
        Attachment released = await ReleasedAttachmentAsync();
        AttachmentObjectGeneration generation = await SingleGenerationAsync(released.Id);
        fixture.Logs.Events.Clear();

        (await RevokeAsync(released.Reference, Reason))
            .ShouldBe(AttachmentRevocationStatus.Revoked);

        string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
        logs.ShouldContain(
            fragment => fragment.Contains(released.Reference.Value, StringComparison.Ordinal),
            "a revogação tem de nomear o anexo que revogou.");

        string[] prohibited =
        [
            AttachmentManagementApiFixture.Bucket,
            AttachmentManagementApiFixture.AccessKey,
            AttachmentManagementApiFixture.SecretKey,
            fixture.AwsEndpoint,
            generation.Store,
            generation.Key,
            generation.Version,
            released.ContentId.ToString("N"),
            PdfContent,
            AttachmentApi.FileName,
            AttachmentApi.ContentType,
            Convert.ToHexString(generation.Digest),
            Convert.ToHexString(generation.Digest).ToLowerInvariant(),
            Convert.ToBase64String(generation.Digest),
        ];
        foreach (var value in prohibited)
        {
            logs.ShouldAllBe(fragment =>
                !fragment.Contains(value, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// One withdrawal on a scope of its own, so every call reads the durable
    /// state instead of an instance a previous call left tracked.
    /// </summary>
    private async Task<AttachmentRevocationStatus> RevokeAsync(
        AttachmentReference reference,
        string reason,
        TimeProvider? clock = null,
        IAttachmentSaveOperation? saveOperation = null)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentRevocationOperation revocation = new(
            Context(scope),
            saveOperation ?? scope.ServiceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            clock ?? new MutableTimeProvider(Now),
            scope.ServiceProvider
                .GetRequiredService<ILogger<AttachmentRevocationOperation>>());
        return await revocation.RevokeAsync(reference, reason, CancellationToken.None);
    }

    private async Task<AttachmentValidationOutcome> ValidateAsync(
        AttachmentReference reference,
        IAttachmentContentPolicy policy)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentValidation validation = new(
            Context(scope),
            policy,
            scope.ServiceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            Options.Create(new AttachmentValidationOptions { ReleaseValidity = Validity }),
            new MutableTimeProvider(Now),
            scope.ServiceProvider.GetRequiredService<ILogger<AttachmentValidation>>());
        return await validation.ValidateAsync(reference, CancellationToken.None);
    }

    private async Task<Attachment> AttachmentInAsync(string state)
    {
        if (state == AttachmentStates.AwaitingUpload)
        {
            return await RegisteredAttachmentAsync();
        }

        Attachment uploaded = await UploadedAttachmentAsync();
        if (state == AttachmentStates.Received)
        {
            return uploaded;
        }

        // The policy the module ships admits nothing, so it refuses this one
        // and the refusal is the arrangement.
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentValidationOutcome outcome = await ValidateAsync(
            uploaded.Reference,
            scope.ServiceProvider.GetRequiredService<IAttachmentContentPolicy>());
        outcome.Status.ShouldBe(AttachmentValidationStatus.Rejected);
        return uploaded;
    }

    private async Task<Attachment> ReleasedAttachmentAsync()
    {
        Attachment uploaded = await UploadedAttachmentAsync();
        AttachmentValidationOutcome outcome = await ValidateAsync(
            uploaded.Reference,
            new ApprovingPolicy());
        outcome.Status.ShouldBe(AttachmentValidationStatus.Released);
        return uploaded;
    }

    private async Task<Attachment> RegisteredAttachmentAsync()
    {
        var principal = $"revocation-producer-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, ByteCount(PdfContent));
        registration.Dispose();
        return await fixture.QueryAttachmentAsync(registered.Reference);
    }

    private async Task<Attachment> UploadedAttachmentAsync()
    {
        var principal = $"revocation-producer-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, ByteCount(PdfContent));
        registration.Dispose();
        using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
            client,
            registered.Reference,
            PdfContent);
        upload.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await fixture.QueryAttachmentAsync(registered.Reference);
    }

    private static long ByteCount(string content) => Encoding.UTF8.GetByteCount(content);

    private static AttachmentManagementDbContext Context(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>();

    private async Task<AttachmentRelease[]> ReleasesAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await Context(scope)
            .Releases
            .AsNoTracking()
            .Where(release => release.AttachmentId == attachmentId)
            .ToArrayAsync();
    }

    private async Task<AttachmentRevocation[]> RevocationsAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await Context(scope)
            .Revocations
            .AsNoTracking()
            .Where(revocation => revocation.AttachmentId == attachmentId)
            .ToArrayAsync();
    }

    private async Task<AttachmentObjectGeneration> SingleGenerationAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await Context(scope)
            .ObjectGenerations
            .AsNoTracking()
            .SingleAsync(generation => generation.AttachmentId == attachmentId);
    }

    /// <summary>Approves whatever it is asked about, and counts the asking.</summary>
    private sealed class ApprovingPolicy : IAttachmentContentPolicy
    {
        internal int Calls { get; private set; }

        public Task<AttachmentPolicyVerdict> EvaluateAsync(
            AttachmentContentSubject subject,
            CancellationToken cancellationToken)
        {
            _ = subject;
            _ = cancellationToken;
            Calls++;
            return Task.FromResult(AttachmentPolicyVerdict.Approve());
        }
    }

    /// <summary>
    /// Writes what the operation asked for and then fails, the way a connection
    /// that drops after the statements and before the commit does.
    /// </summary>
    private sealed class WritingThenFailingSaveOperation : IAttachmentSaveOperation
    {
        public async Task SaveChangesAsync(
            AttachmentManagementDbContext dbContext,
            CancellationToken cancellationToken)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("A persistência falhou após a gravação.");
        }
    }
}
