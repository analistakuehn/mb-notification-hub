using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// The state machine against the database it runs on.
/// <para>
/// What these oracles prove: that the released state is reachable from one
/// place and that every other outcome leaves the attachment unreleased; that
/// the type rule refuses over real bytes written to and read back from the
/// store; that the deadline of a verdict that did not conclude is written once
/// and reached once; and that a failure to persist leaves nothing behind.
/// </para>
/// <para>
/// What they do not prove: anything about hostile content, and anything about
/// a file nobody can open. The approval side is driven by a double here, and
/// the policy the module ships never opens a file, so a document of an
/// admitted type that needs a password is approved by the same path that
/// approves a plain one. Those two stay unproven until a real verifier sits
/// behind the same seam.
/// </para>
/// </summary>
[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentValidationTests(AttachmentManagementApiFixture fixture)
{
    private const string PdfContent = "%PDF-1.7 sample attachment body";
    private const string GifContent = "GIF89a sample attachment body";
    private const string UnsignedContent = "plain bytes that no signature describes";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-02T12:00:00Z",
        CultureInfo.InvariantCulture);

    private static readonly TimeSpan Validity = TimeSpan.FromDays(30);

    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>
    /// The policy the module registers, over bytes that really went to the
    /// store and came back. Nothing is admitted by default, so the attachment
    /// that agrees with its own declaration is refused just the same, and that
    /// is what closing by default means: the gate is shut until an operator
    /// opens it, not until a check fails.
    /// </summary>
    [RequiresDockerTheory]
    [InlineData(PdfContent, "application/pdf", "content-type-not-admitted")]
    [InlineData(PdfContent, "image/gif", "content-type-divergent")]
    [InlineData(UnsignedContent, "application/pdf", "content-not-inspectable")]
    public async Task Every_content_refusal_ends_the_attachment_under_one_public_reason(
        string content,
        string declaredContentType,
        string expectedDetail)
    {
        Attachment uploaded = await UploadAsync(content, declaredContentType);

        AttachmentValidationOutcome outcome = await ValidateWithShippedPolicyAsync(
            uploaded.Reference);

        outcome.Status.ShouldBe(AttachmentValidationStatus.Rejected);
        outcome.Detail.ShouldBe(expectedDetail);
        outcome.PublicReason.ShouldBe(ErrorCodes.ContentRefused);

        Attachment settled = await fixture.QueryAttachmentAsync(uploaded.Reference.Value);
        settled.State.ShouldBe(AttachmentStates.Rejected);
        settled.ValidationDetail.ShouldBe(expectedDetail);
        (await ReleasesAsync(uploaded.Id)).ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task An_explicit_approval_is_what_writes_the_release()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        var policy = new RecordingPolicy(AttachmentPolicyVerdict.Approve());

        AttachmentValidationOutcome outcome = await ValidateAsync(uploaded.Reference, policy);

        outcome.Status.ShouldBe(AttachmentValidationStatus.Released);
        outcome.PublicReason.ShouldBeNull();
        policy.Calls.ShouldBe(1);

        Attachment released = await fixture.QueryAttachmentAsync(uploaded.Reference.Value);
        released.State.ShouldBe(AttachmentStates.Released);
        released.ValidationDetail.ShouldBeNull();

        AttachmentRelease release = (await ReleasesAsync(uploaded.Id)).ShouldHaveSingleItem();
        release.ReleasedAt.ShouldBe(Now);
        release.ExpiresAt.ShouldBe(Now + Validity);

        // The release names the generation it released, so what was approved is
        // a set of bytes and not just a reference.
        release.GenerationId.ShouldBe((await SingleGenerationAsync(uploaded.Id)).Id);
    }

    /// <summary>
    /// The policy is told what was declared and what the bytes were recognized
    /// as, and nothing that could name the object. A policy that could name it
    /// could hand that name to whatever it talks to.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_policy_is_told_the_types_and_no_storage_coordinate()
    {
        Attachment uploaded = await UploadAsync(GifContent, "image/gif");
        var policy = new RecordingPolicy(AttachmentPolicyVerdict.Approve());

        await ValidateAsync(uploaded.Reference, policy);

        AttachmentContentSubject subject = policy.Subjects.ShouldHaveSingleItem();
        AttachmentObjectGeneration generation = await SingleGenerationAsync(uploaded.Id);

        var rendered = $"{subject.DeclaredContentType}|{subject.DetectedContentType}";
        rendered.ShouldNotContain(generation.Key, Case.Insensitive);
        rendered.ShouldNotContain(generation.Version, Case.Insensitive);
        rendered.ShouldNotContain(generation.Store, Case.Insensitive);
        rendered.ShouldNotContain(uploaded.ContentId.ToString("N"), Case.Insensitive);

        subject.DeclaredContentType.ShouldBe("image/gif");
        subject.DetectedContentType.ShouldBe("image/gif");
        subject.SizeBytes.ShouldBe(ByteCount(GifContent));
    }

    [RequiresDockerFact]
    public async Task A_policy_that_throws_leaves_the_attachment_as_unreleased_as_it_was()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");

        AttachmentValidationOutcome outcome = await ValidateAsync(
            uploaded.Reference,
            new ThrowingPolicy());

        outcome.Status.ShouldBe(AttachmentValidationStatus.PolicyUnavailable);
        await AssertUntouchedAsync(uploaded, AttachmentStates.Received);
    }

    [RequiresDockerFact]
    public async Task A_policy_that_answers_nothing_leaves_the_attachment_as_unreleased_as_it_was()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");

        AttachmentValidationOutcome outcome = await ValidateAsync(
            uploaded.Reference,
            new SilentPolicy());

        outcome.Status.ShouldBe(AttachmentValidationStatus.PolicyUnavailable);
        await AssertUntouchedAsync(uploaded, AttachmentStates.Received);
    }

    /// <summary>
    /// A refusal the durable state cannot hold is not written as a refusal with
    /// the detail cut off, and it is not written as a release either. It is the
    /// same closed answer as no verdict at all.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_detail_the_durable_state_cannot_hold_is_treated_as_no_answer()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        var oversized = new string('d', Attachment.MaxValidationDetailLength + 1);
        var policy = new RecordingPolicy(AttachmentPolicyVerdict.Refuse(oversized));

        AttachmentValidationOutcome outcome = await ValidateAsync(uploaded.Reference, policy);

        outcome.Status.ShouldBe(AttachmentValidationStatus.PolicyUnavailable);
        policy.Calls.ShouldBe(1);
        await AssertUntouchedAsync(uploaded, AttachmentStates.Received);
    }

    [RequiresDockerFact]
    public async Task An_open_verdict_starts_a_deadline_that_repeating_it_does_not_move()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        var policy = new RecordingPolicy(
            AttachmentPolicyVerdict.DidNotConclude("verifier-did-not-answer"));
        var clock = new MutableTimeProvider(Now);

        AttachmentValidationOutcome first = await ValidateAsync(
            uploaded.Reference,
            policy,
            clock);
        Attachment waiting = await fixture.QueryAttachmentAsync(uploaded.Reference.Value);

        clock.Advance(Window - TimeSpan.FromMinutes(1));
        AttachmentValidationOutcome second = await ValidateAsync(
            uploaded.Reference,
            policy,
            clock);
        Attachment stillWaiting = await fixture.QueryAttachmentAsync(uploaded.Reference.Value);

        first.Status.ShouldBe(AttachmentValidationStatus.Inconclusive);
        second.Status.ShouldBe(AttachmentValidationStatus.Inconclusive);
        waiting.State.ShouldBe(AttachmentStates.Inconclusive);
        waiting.InconclusiveUntil.ShouldBe(Now + Window);

        // A second reading is news; a second clock is not. Nothing restarts the
        // wait, so the deadline the first verdict set is the deadline.
        stillWaiting.InconclusiveUntil.ShouldBe(Now + Window);
        (await ReleasesAsync(uploaded.Id)).ShouldBeEmpty();
    }

    /// <summary>
    /// The strongest oracle of the wait: past the deadline the policy is not
    /// consulted at all, and the policy used here is one that approves. Asking
    /// it after the deadline would be a way back in.
    /// </summary>
    [RequiresDockerFact]
    public async Task Past_the_deadline_no_verdict_is_asked_for_and_the_attachment_is_refused()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        var clock = new MutableTimeProvider(Now);
        var open = new RecordingPolicy(
            AttachmentPolicyVerdict.DidNotConclude("verifier-did-not-answer"));
        var approving = new RecordingPolicy(AttachmentPolicyVerdict.Approve());

        await ValidateAsync(uploaded.Reference, open, clock);
        clock.Advance(Window);
        AttachmentValidationOutcome outcome = await ValidateAsync(
            uploaded.Reference,
            approving,
            clock);

        outcome.Status.ShouldBe(AttachmentValidationStatus.Rejected);
        outcome.Detail.ShouldBe("inconclusive-window-elapsed");
        outcome.PublicReason.ShouldBe(ErrorCodes.ContentRefused);
        approving.Calls.ShouldBe(0);

        Attachment settled = await fixture.QueryAttachmentAsync(uploaded.Reference.Value);
        settled.State.ShouldBe(AttachmentStates.Rejected);
        settled.ValidationDetail.ShouldBe("inconclusive-window-elapsed");
        (await ReleasesAsync(uploaded.Id)).ShouldBeEmpty();
    }

    /// <summary>
    /// A verdict that arrives before the deadline still ends the wait, and the
    /// release it writes is dated by the instant it arrived. Without this, the
    /// only proven way out of the wait would be its deadline.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_verdict_that_arrives_before_the_deadline_still_releases()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        var clock = new MutableTimeProvider(Now);
        var open = new RecordingPolicy(
            AttachmentPolicyVerdict.DidNotConclude("verifier-did-not-answer"));

        // A second short of the deadline, and not a tick short of it: the
        // column keeps microseconds, so a tick-level offset is truncated on the
        // way back and the assertion below would compare two instants the
        // storage cannot tell apart. The tick-level edge of the deadline is
        // measured where it lives, on the aggregate.
        await ValidateAsync(uploaded.Reference, open, clock);
        DateTimeOffset justBefore = Now + Window - TimeSpan.FromSeconds(1);
        clock.Advance(Window - TimeSpan.FromSeconds(1));
        AttachmentValidationOutcome outcome = await ValidateAsync(
            uploaded.Reference,
            new RecordingPolicy(AttachmentPolicyVerdict.Approve()),
            clock);

        outcome.Status.ShouldBe(AttachmentValidationStatus.Released);

        AttachmentRelease release = (await ReleasesAsync(uploaded.Id)).ShouldHaveSingleItem();
        release.ReleasedAt.ShouldBe(justBefore);
        release.ExpiresAt.ShouldBe(justBefore + Validity);
    }

    /// <summary>
    /// Nothing restarts the clock of a release. Repeating a validation over a
    /// released attachment is not a revalidation: a revalidation is explicit
    /// and writes a row of its own, and repeating writes nothing.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_released_attachment_is_not_released_again_by_repeating_the_validation()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        var clock = new MutableTimeProvider(Now);
        var policy = new RecordingPolicy(AttachmentPolicyVerdict.Approve());

        await ValidateAsync(uploaded.Reference, policy, clock);
        clock.Advance(TimeSpan.FromDays(10));
        AttachmentValidationOutcome repeated = await ValidateAsync(
            uploaded.Reference,
            policy,
            clock);

        repeated.Status.ShouldBe(AttachmentValidationStatus.AlreadyDecided);
        policy.Calls.ShouldBe(1);

        AttachmentRelease release = (await ReleasesAsync(uploaded.Id)).ShouldHaveSingleItem();
        release.ReleasedAt.ShouldBe(Now);
        release.ExpiresAt.ShouldBe(Now + Validity);
    }

    /// <summary>
    /// A refusal is not reopened either, and the policy is never asked about
    /// one. Without this, only the released side of the settled rule would be
    /// measured.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_refused_attachment_is_not_reopened_by_repeating_the_validation()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        await ValidateWithShippedPolicyAsync(uploaded.Reference);
        var approving = new RecordingPolicy(AttachmentPolicyVerdict.Approve());

        AttachmentValidationOutcome repeated = await ValidateAsync(
            uploaded.Reference,
            approving);

        repeated.Status.ShouldBe(AttachmentValidationStatus.AlreadyDecided);
        approving.Calls.ShouldBe(0);

        Attachment settled = await fixture.QueryAttachmentAsync(uploaded.Reference.Value);
        settled.State.ShouldBe(AttachmentStates.Rejected);
        settled.ValidationDetail.ShouldBe("content-type-not-admitted");
        (await ReleasesAsync(uploaded.Id)).ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task An_attachment_whose_content_never_arrived_takes_no_verdict()
    {
        var principal = $"validation-not-uploaded-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        (HttpResponseMessage response, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, ByteCount(PdfContent));
        response.Dispose();
        var policy = new RecordingPolicy(AttachmentPolicyVerdict.Approve());

        AttachmentValidationOutcome outcome = await ValidateAsync(
            AttachmentReference.Create(registered.Reference).Value.ShouldNotBeNull(),
            policy);

        outcome.Status.ShouldBe(AttachmentValidationStatus.NotReceived);
        policy.Calls.ShouldBe(0);
        (await fixture.QueryAttachmentAsync(registered.Reference)).State
            .ShouldBe(AttachmentStates.AwaitingUpload);
    }

    /// <summary>
    /// A state that says the bytes arrived, with no identity record under it.
    /// The module cannot say which bytes a verdict would be about, so it does
    /// not ask for one.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attachment_with_no_identity_record_is_not_released()
    {
        Attachment seeded = Attachment.Register(
                AttachmentApi.Application,
                "no-generation.pdf",
                "application/pdf",
                ByteCount(PdfContent),
                Now)
            .Value
            .ShouldNotBeNull();
        seeded.MarkReceived(ByteCount(PdfContent), Now)
            .ShouldBe(AttachmentReceiveOutcome.Received);
        using (IServiceScope seeding = fixture.Services.CreateScope())
        {
            AttachmentManagementDbContext dbContext = Context(seeding);
            dbContext.Attachments.Add(seeded);
            await dbContext.SaveChangesAsync();
        }

        var policy = new RecordingPolicy(AttachmentPolicyVerdict.Approve());
        AttachmentValidationOutcome outcome = await ValidateAsync(seeded.Reference, policy);

        outcome.Status.ShouldBe(AttachmentValidationStatus.IdentityUnavailable);
        policy.Calls.ShouldBe(0);
        await AssertUntouchedAsync(seeded, AttachmentStates.Received);
    }

    /// <summary>
    /// Two identity records under one attachment. A release names the
    /// generation it released, so with two of them the module cannot say which
    /// bytes an approval was about, and it refuses to guess.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attachment_with_more_than_one_identity_record_is_not_released()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        AttachmentObjectGeneration first = await SingleGenerationAsync(uploaded.Id);
        using (IServiceScope seeding = fixture.Services.CreateScope())
        {
            AttachmentManagementDbContext dbContext = Context(seeding);
            dbContext.ObjectGenerations.Add(AttachmentObjectGeneration.Capture(
                uploaded.Id,
                AttachmentObjectLocator.FromStoredRow(
                    first.Store,
                    first.Key,
                    $"{Guid.NewGuid():N}"),
                first.Proof(),
                first.DetectedContentType,
                Now));
            await dbContext.SaveChangesAsync();
        }

        var policy = new RecordingPolicy(AttachmentPolicyVerdict.Approve());
        AttachmentValidationOutcome outcome = await ValidateAsync(uploaded.Reference, policy);

        outcome.Status.ShouldBe(AttachmentValidationStatus.IdentityUnavailable);
        policy.Calls.ShouldBe(0);
        await AssertUntouchedAsync(uploaded, AttachmentStates.Received);
    }

    [RequiresDockerFact]
    public async Task An_unknown_reference_decides_nothing()
    {
        var policy = new RecordingPolicy(AttachmentPolicyVerdict.Approve());

        AttachmentValidationOutcome outcome = await ValidateAsync(
            AttachmentReference.Generate(),
            policy);

        outcome.Status.ShouldBe(AttachmentValidationStatus.UnknownAttachment);
        outcome.PublicReason.ShouldBeNull();
        policy.Calls.ShouldBe(0);
    }

    /// <summary>
    /// The state and the release row become durable together or not at all. A
    /// release the state does not carry would be a release nothing points at,
    /// and a state without the row would be a release nothing can date.
    /// <para>
    /// The failure injected here writes first and fails afterwards, which is
    /// the only shape that measures anything: a save that never writes leaves
    /// nothing durable however the operation is written, and an oracle over it
    /// would pass with the transaction removed.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_failure_to_persist_leaves_no_release_behind()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        var policy = new RecordingPolicy(AttachmentPolicyVerdict.Approve());

        await Should.ThrowAsync<InvalidOperationException>(
            () => ValidateAsync(
                uploaded.Reference,
                policy,
                saveOperation: new WritingThenFailingSaveOperation()));

        policy.Calls.ShouldBe(1);
        await AssertUntouchedAsync(uploaded, AttachmentStates.Received);
    }

    /// <summary>
    /// The policy is evaluated once, at validation, and the upload path never
    /// touches it. The policy registered for this host throws on any call, and
    /// the upload goes through anyway.
    /// <para>
    /// Evaluating it at ingress would look harmless and would not be: a change
    /// to the admitted list would then decide the fate of bytes already
    /// accepted, and a value meant to be adjustable would become a breaking
    /// change.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_upload_path_never_asks_the_policy_for_a_verdict()
    {
        var principal = $"validation-upload-untouched-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAttachmentContentPolicy>();
                services.AddSingleton<IAttachmentContentPolicy>(new ThrowingPolicy());
            }));
        using HttpClient client = fixture.CreateProducerClient(host, principal);

        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, ByteCount(PdfContent));
        registration.Dispose();
        using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
            client,
            registered.Reference,
            PdfContent);

        upload.StatusCode.ShouldBe(HttpStatusCode.OK);
        Attachment received = await fixture.QueryAttachmentAsync(registered.Reference);
        received.State.ShouldBe(AttachmentStates.Received);
        received.ValidationDetail.ShouldBeNull();
        received.InconclusiveUntil.ShouldBeNull();
    }

    /// <summary>
    /// The recognized type is measured when the bytes are read back and stays
    /// on the generation row, where the policy reads it later. What is recorded
    /// at ingress is the measurement, never the verdict.
    /// </summary>
    [RequiresDockerTheory]
    [InlineData(PdfContent, "application/pdf")]
    [InlineData(GifContent, "image/gif")]
    [InlineData(UnsignedContent, null)]
    public async Task The_recognized_type_is_recorded_with_the_generation(
        string content,
        string? expected)
    {
        Attachment uploaded = await UploadAsync(content, "application/pdf");

        (await SingleGenerationAsync(uploaded.Id)).DetectedContentType.ShouldBe(expected);
    }

    /// <summary>
    /// What a validation writes to the log. The reference is the correlator and
    /// has to be there; everything that names the bytes, the object, the store
    /// or the credential must not be.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_validation_names_the_attachment_and_nothing_that_locates_its_bytes()
    {
        Attachment uploaded = await UploadAsync(PdfContent, "application/pdf");
        AttachmentObjectGeneration generation = await SingleGenerationAsync(uploaded.Id);
        fixture.Logs.Events.Clear();

        await ValidateWithShippedPolicyAsync(uploaded.Reference);

        string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
        logs.ShouldContain(
            fragment => fragment.Contains(uploaded.Reference.Value, StringComparison.Ordinal),
            "the refusal has to name the attachment it refused.");

        string[] prohibited =
        [
            AttachmentManagementApiFixture.Bucket,
            AttachmentManagementApiFixture.AccessKey,
            AttachmentManagementApiFixture.SecretKey,
            fixture.AwsEndpoint,
            generation.Store,
            generation.Key,
            generation.Version,
            uploaded.ContentId.ToString("N"),
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
    /// The machine and the policy come out of the composed host, wired the way
    /// the module registers them. Nothing in production resolves either one
    /// yet, so without this the registration could be missing and every oracle
    /// above would still pass on instances the tests built themselves.
    /// </summary>
    [RequiresDockerFact]
    public void The_composed_host_hands_out_the_machine_and_the_policy_it_registered()
    {
        using IServiceScope scope = fixture.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<AttachmentValidation>().ShouldNotBeNull();
        scope.ServiceProvider
            .GetRequiredService<IAttachmentContentPolicy>()
            .ShouldBeOfType<AdmittedTypeContentPolicy>();
    }

    private async Task AssertUntouchedAsync(Attachment attachment, string state)
    {
        Attachment durable = await fixture.QueryAttachmentAsync(attachment.Reference.Value);
        durable.State.ShouldBe(state);
        durable.ValidationDetail.ShouldBeNull();
        durable.InconclusiveUntil.ShouldBeNull();
        (await ReleasesAsync(attachment.Id)).ShouldBeEmpty();
    }

    /// <summary>
    /// One validation on a scope of its own, so every call reads the durable
    /// state instead of an instance a previous call left tracked.
    /// </summary>
    private async Task<AttachmentValidationOutcome> ValidateAsync(
        AttachmentReference reference,
        IAttachmentContentPolicy policy,
        TimeProvider? clock = null,
        IAttachmentSaveOperation? saveOperation = null)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentValidation validation = new(
            Context(scope),
            policy,
            saveOperation ?? scope.ServiceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            Options.Create(Settings()),
            clock ?? new MutableTimeProvider(Now),
            scope.ServiceProvider.GetRequiredService<ILogger<AttachmentValidation>>());
        return await validation.ValidateAsync(reference, CancellationToken.None);
    }

    private async Task<AttachmentValidationOutcome> ValidateWithShippedPolicyAsync(
        AttachmentReference reference)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await ValidateAsync(
            reference,
            scope.ServiceProvider.GetRequiredService<IAttachmentContentPolicy>());
    }

    private static AttachmentValidationOptions Settings()
        => new()
        {
            ReleaseValidity = Validity,
            InconclusiveWindow = Window,
        };

    private static AttachmentManagementDbContext Context(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>();

    /// <summary>
    /// What the upload actually sends. The declared size travels as a byte
    /// count and never as a character count, so a body that is not plain ASCII
    /// cannot make a test fail on a size nobody meant to change.
    /// </summary>
    private static long ByteCount(string content) => Encoding.UTF8.GetByteCount(content);

    private async Task<Attachment> UploadAsync(string content, string declaredContentType)
    {
        var principal = $"validation-producer-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using HttpClient client = fixture.CreateProducerClient(principal);
        (HttpResponseMessage registration, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(client, ByteCount(content), declaredContentType);
        registration.Dispose();
        using HttpResponseMessage upload = await AttachmentApi.PutContentAsync(
            client,
            registered.Reference,
            content);
        upload.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await fixture.QueryAttachmentAsync(registered.Reference);
    }

    private async Task<AttachmentRelease[]> ReleasesAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await Context(scope)
            .Releases
            .AsNoTracking()
            .Where(release => release.AttachmentId == attachmentId)
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

    /// <summary>Answers one verdict and remembers what it was asked about.</summary>
    private sealed class RecordingPolicy(AttachmentPolicyVerdict verdict)
        : IAttachmentContentPolicy
    {
        private readonly List<AttachmentContentSubject> _subjects = [];

        internal int Calls => _subjects.Count;

        internal IReadOnlyList<AttachmentContentSubject> Subjects => _subjects;

        public Task<AttachmentPolicyVerdict> EvaluateAsync(
            AttachmentContentSubject subject,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _subjects.Add(subject);
            return Task.FromResult(verdict);
        }
    }

    private sealed class ThrowingPolicy : IAttachmentContentPolicy
    {
        public Task<AttachmentPolicyVerdict> EvaluateAsync(
            AttachmentContentSubject subject,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("A política de conteúdo falhou.");
    }

    /// <summary>Answers the call and hands back no verdict at all.</summary>
    private sealed class SilentPolicy : IAttachmentContentPolicy
    {
        public Task<AttachmentPolicyVerdict> EvaluateAsync(
            AttachmentContentSubject subject,
            CancellationToken cancellationToken)
        {
            _ = subject;
            _ = cancellationToken;
            return Task.FromResult<AttachmentPolicyVerdict>(null!);
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
