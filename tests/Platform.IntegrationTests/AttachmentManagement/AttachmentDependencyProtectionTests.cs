using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentDependencyProtectionTests(AttachmentManagementApiFixture fixture)
{
    /// <summary>
    /// How long a call that has to be blocked is watched before the run
    /// accepts that it is blocked. A wrong verdict here can only come from the
    /// call completing, never from the wait being too short.
    /// </summary>
    private static readonly TimeSpan BlockedBudget = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling on a wait that a correct run always ends in milliseconds.</summary>
    private static readonly TimeSpan ArrivalBudget = TimeSpan.FromSeconds(30);

    [RequiresDockerFact]
    public async Task A_confirmed_claim_keeps_the_object_from_being_discarded()
        => await AssertDependencyProtectsAsync(
            "claim-dependency-producer",
            AttachmentDependencyReasons.ClaimConfirmed);

    [RequiresDockerFact]
    public async Task An_attempt_in_flight_keeps_the_object_from_being_discarded()
        => await AssertDependencyProtectsAsync(
            "sending-dependency-producer",
            AttachmentDependencyReasons.AttemptSending);

    [RequiresDockerFact]
    public async Task An_attempt_with_an_unknown_outcome_keeps_the_object_from_being_discarded()
        => await AssertDependencyProtectsAsync(
            "unknown-dependency-producer",
            AttachmentDependencyReasons.AttemptUnknown);

    /// <summary>
    /// The three cases above name the states the rule lists, and they travel
    /// the same code path, because what makes a dependency live is the absence
    /// of a release and never the reason. This case is what turns that into a
    /// claim: a reason this module never listed protects the object just as
    /// well, so no future filter on the reason can pass unnoticed.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_reason_this_module_never_listed_protects_the_object_just_the_same()
        => await AssertDependencyProtectsAsync(
            "unlisted-dependency-producer",
            "reason-nobody-listed");

    private async Task AssertDependencyProtectsAsync(string principal, string reason)
    {
        const string content = "protected-attachment-content";
        Attachment attachment = await UploadAsync(principal, content);
        AttachmentObjectGeneration generation = await SingleGenerationAsync(attachment.Id);
        var holder = $"holder-{Guid.NewGuid():N}";

        using IServiceScope scope = fixture.Services.CreateScope();
        (await Registry(scope).HoldAsync(
            attachment.Reference,
            reason,
            holder,
            CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);

        AttachmentDisposalOutcome outcome = await Disposal(scope).DiscardAsync(
            attachment.Reference,
            CancellationToken.None);

        outcome.Status.ShouldBe(AttachmentDisposalStatus.HeldByDependency);
        outcome.LiveDependencies.ShouldBe(1);
        outcome.DiscardedGenerations.ShouldBe(0);
        await AssertBytesAreStillThereAsync(generation, content);
    }

    [RequiresDockerFact]
    public async Task Time_alone_never_ends_a_dependency()
    {
        const string content = "aged-dependency-content";
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(start);
        Attachment attachment = await UploadAsync("aged-dependency-producer", content);
        AttachmentObjectGeneration generation = await SingleGenerationAsync(attachment.Id);
        var holder = $"holder-{Guid.NewGuid():N}";

        using IServiceScope scope = fixture.Services.CreateScope();
        (await RegistryWith(scope, clock).HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.AttemptUnknown,
            holder,
            CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);
        AttachmentDependency recorded = await SingleDependencyAsync(attachment.Id);
        recorded.AcquiredAt.ShouldBe(start);
        recorded.ReleasedAt.ShouldBeNull();

        clock.Advance(TimeSpan.FromDays(400));

        AttachmentDisposalOutcome outcome = await Disposal(scope).DiscardAsync(
            attachment.Reference,
            CancellationToken.None);

        outcome.Status.ShouldBe(AttachmentDisposalStatus.HeldByDependency);
        outcome.LiveDependencies.ShouldBe(1);
        (await SingleDependencyAsync(attachment.Id)).ReleasedAt.ShouldBeNull();
        await AssertBytesAreStillThereAsync(generation, content);
    }

    [RequiresDockerFact]
    public async Task The_last_dependency_ending_lets_the_discard_through()
    {
        const string content = "released-dependency-content";
        Attachment attachment = await UploadAsync("released-dependency-producer", content);
        AttachmentObjectGeneration generation = await SingleGenerationAsync(attachment.Id);
        var first = $"holder-{Guid.NewGuid():N}";
        var second = $"holder-{Guid.NewGuid():N}";

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentDependencyRegistry registry = Registry(scope);
        await registry.HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.ClaimConfirmed,
            first,
            CancellationToken.None);
        await registry.HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.AttemptSending,
            second,
            CancellationToken.None);

        (await registry.ReleaseAsync(attachment.Reference, first, CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);
        AttachmentDisposalOutcome whileSecondLives = await Disposal(scope).DiscardAsync(
            attachment.Reference,
            CancellationToken.None);

        whileSecondLives.Status.ShouldBe(AttachmentDisposalStatus.HeldByDependency);
        whileSecondLives.LiveDependencies.ShouldBe(1);
        await AssertBytesAreStillThereAsync(generation, content);

        (await registry.ReleaseAsync(attachment.Reference, second, CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);
        AttachmentDisposalOutcome released = await Disposal(scope).DiscardAsync(
            attachment.Reference,
            CancellationToken.None);

        released.Status.ShouldBe(AttachmentDisposalStatus.Discarded);
        released.DiscardedGenerations.ShouldBe(1);
        released.UnconfirmedRemovals.ShouldBe(0);
        (await fixture.ObjectVersionsAsync(generation.Key)).ShouldBeEmpty();
        using AttachmentStoreOpen gone = await fixture.Services
            .GetRequiredService<IAttachmentObjectStore>()
            .OpenAsync(generation.Locator(), CancellationToken.None);
        gone.Status.ShouldBe(AttachmentStoreOpenStatus.Missing);
    }

    [RequiresDockerFact]
    public async Task Taking_and_ending_the_same_dependency_twice_changes_nothing()
    {
        Attachment attachment = await UploadAsync("idempotent-dependency-producer", "twice");
        var holder = $"holder-{Guid.NewGuid():N}";

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentDependencyRegistry registry = Registry(scope);
        (await registry.HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.ClaimConfirmed,
            holder,
            CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);
        (await registry.HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.ClaimConfirmed,
            holder,
            CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.AlreadyHeld);

        AttachmentDependency held = await SingleDependencyAsync(attachment.Id);
        held.ReleasedAt.ShouldBeNull();
        (await Disposal(scope).DiscardAsync(attachment.Reference, CancellationToken.None))
            .LiveDependencies
            .ShouldBe(1);

        await registry.ReleaseAsync(attachment.Reference, holder, CancellationToken.None);
        AttachmentDependency released = await SingleDependencyAsync(attachment.Id);
        (await registry.ReleaseAsync(attachment.Reference, holder, CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);

        AttachmentDependency after = await SingleDependencyAsync(attachment.Id);
        after.Version.ShouldBe(released.Version);
        after.ReleasedAt.ShouldBe(released.ReleasedAt);
        (await Disposal(scope).DiscardAsync(attachment.Reference, CancellationToken.None))
            .Status
            .ShouldBe(AttachmentDisposalStatus.Discarded);
    }

    /// <summary>
    /// A live hold answers a second declaration by keeping what it was taken
    /// with, so the reason and the instant always describe the acquisition
    /// that is running. The caller is told, because the declaration it made
    /// was not written and nothing else would say so.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_second_hold_by_the_same_dependent_keeps_what_it_was_taken_with()
    {
        Attachment attachment = await UploadAsync("redeclared-dependency-producer", "redeclared");
        var holder = $"holder-{Guid.NewGuid():N}";
        var start = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var clock = new MutableTimeProvider(start);

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentDependencyRegistry registry = RegistryWith(scope, clock);
        (await registry.HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.ClaimConfirmed,
            holder,
            CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);

        clock.Advance(TimeSpan.FromHours(3));
        (await registry.HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.AttemptSending,
            holder,
            CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.AlreadyHeld);

        AttachmentDependency live = await SingleDependencyAsync(attachment.Id);
        live.Reason.ShouldBe(AttachmentDependencyReasons.ClaimConfirmed);
        live.AcquiredAt.ShouldBe(start);
        live.Version.ShouldBe(1);

        // Ending the hold is the only thing that opens the row to a new
        // declaration, and there the record takes the one just made.
        (await registry.ReleaseAsync(attachment.Reference, holder, CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);
        (await registry.HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.AttemptSending,
            holder,
            CancellationToken.None))
            .ShouldBe(AttachmentDependencyOutcome.Recorded);

        AttachmentDependency revived = await SingleDependencyAsync(attachment.Id);
        revived.Reason.ShouldBe(AttachmentDependencyReasons.AttemptSending);
        revived.AcquiredAt.ShouldBe(start.AddHours(3));
        revived.ReleasedAt.ShouldBeNull();
        revived.Version.ShouldBe(3);
    }

    [RequiresDockerFact]
    public async Task A_removal_the_store_never_confirmed_is_not_reported_as_a_discard()
    {
        const string content = "unconfirmed-removal-content";
        Attachment attachment = await UploadAsync("unconfirmed-removal-producer", content);
        AttachmentObjectGeneration generation = await SingleGenerationAsync(attachment.Id);

        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentDisposalOutcome outcome = await DisposalOver(scope, new RefusingObjectStore())
            .DiscardAsync(attachment.Reference, CancellationToken.None);

        outcome.Status.ShouldBe(AttachmentDisposalStatus.StoreUnavailable);
        outcome.DiscardedGenerations.ShouldBe(0);
        outcome.UnconfirmedRemovals.ShouldBe(1);
        await AssertBytesAreStillThereAsync(generation, content);
    }

    /// <summary>
    /// This operation has no handler of its own, on purpose: translating a
    /// failure of the store belongs to the adapter. What that costs is stated
    /// here, because until the adapter answered for an elapsed deadline the
    /// whole disposal threw instead of reporting an unconfirmed removal.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_store_that_accepts_and_never_answers_ends_as_an_unconfirmed_removal()
    {
        const string content = "silent-store-content";
        Attachment attachment = await UploadAsync("silent-store-producer", content);
        AttachmentObjectGeneration generation = await SingleGenerationAsync(attachment.Id);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new List<TcpClient>();
        Task accepting = AcceptAndHoldAsync(listener, accepted);
        try
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            using var silent = new S3AttachmentObjectStore(
                SilentEndpointClient(port),
                SilentEndpointClient(port),
                AttachmentManagementApiFixture.Bucket);

            AttachmentDisposalOutcome outcome = await DisposalOver(scope, silent)
                .DiscardAsync(attachment.Reference, CancellationToken.None);

            outcome.Status.ShouldBe(AttachmentDisposalStatus.StoreUnavailable);
            outcome.DiscardedGenerations.ShouldBe(0);
            outcome.UnconfirmedRemovals.ShouldBe(1);
            await AssertBytesAreStillThereAsync(generation, content);
        }
        finally
        {
            listener.Stop();
            await accepting;
            foreach (TcpClient client in accepted)
            {
                client.Dispose();
            }
        }
    }

    private static async Task AcceptAndHoldAsync(TcpListener listener, List<TcpClient> accepted)
    {
        try
        {
            while (true)
            {
                accepted.Add(await listener.AcceptTcpClientAsync());
            }
        }
        catch (ObjectDisposedException)
        {
            // The listener closing is how this loop ends.
        }
        catch (SocketException)
        {
            // The listener closing is how this loop ends.
        }
    }

    private static AmazonS3Client SilentEndpointClient(int port)
        => new(
            new BasicAWSCredentials(
                AttachmentManagementApiFixture.AccessKey,
                AttachmentManagementApiFixture.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = $"http://127.0.0.1:{port}",
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
                Timeout = TimeSpan.FromSeconds(2),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                MaxErrorRetry = 0,
            });

    /// <summary>
    /// The dangerous interleaving: a dependency that is being taken right now,
    /// with nothing committed yet, against a sweep that is about to decide the
    /// attachment is abandoned. The hold owns the attachment row from before
    /// it writes until after it commits, which is the whole of what keeps the
    /// sweep from reading a liveness that is already out of date.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_hold_still_in_flight_keeps_a_discard_from_removing_the_bytes()
    {
        const string content = "in-flight-hold-content";
        Attachment attachment = await UploadAsync("in-flight-hold-producer", content);
        AttachmentObjectGeneration generation = await SingleGenerationAsync(attachment.Id);
        var holder = $"holder-{Guid.NewGuid():N}";
        var clock = new PausingTimeProvider(
            new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));

        using IServiceScope holdScope = fixture.Services.CreateScope();
        using IServiceScope disposalScope = fixture.Services.CreateScope();
        AttachmentDependencyRegistry registry = RegistryWith(holdScope, clock);
        var holding = Task.Run(() => registry.HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.ClaimConfirmed,
            holder,
            CancellationToken.None));

        // The clock is read after the attachment row is taken and before the
        // dependency is written, so a run stopped here owns the row with
        // nothing committed. The count is what proves the second half.
        (await Task.WhenAny(clock.Reached, Task.Delay(ArrivalBudget))).ShouldBe(clock.Reached);
        (await LiveDependencyCountAsync(attachment.Id)).ShouldBe(0);

        Task<AttachmentDisposalOutcome> discarding = Disposal(disposalScope).DiscardAsync(
            attachment.Reference,
            CancellationToken.None);

        (await Task.WhenAny(discarding, Task.Delay(BlockedBudget))).ShouldNotBe(
            discarding,
            "a discard cannot decide while another writer holds the attachment row.");

        clock.Release();
        (await holding).ShouldBe(AttachmentDependencyOutcome.Recorded);
        AttachmentDisposalOutcome outcome = await discarding;

        outcome.Status.ShouldBe(AttachmentDisposalStatus.HeldByDependency);
        outcome.LiveDependencies.ShouldBe(1);
        outcome.DiscardedGenerations.ShouldBe(0);
        await AssertBytesAreStillThereAsync(generation, content);
    }

    /// <summary>
    /// The other side of the same lock: while the bytes are being removed, a
    /// dependency cannot be written. Without it a dependent would come away
    /// believing it holds an object whose removal was already under way.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_discard_that_is_removing_the_bytes_holds_off_a_new_hold()
    {
        const string content = "serialized-removal-content";
        Attachment attachment = await UploadAsync("serialized-removal-producer", content);
        AttachmentObjectGeneration generation = await SingleGenerationAsync(attachment.Id);
        var holder = $"holder-{Guid.NewGuid():N}";
        var store = new PausingObjectStore(
            fixture.Services.GetRequiredService<IAttachmentObjectStore>());

        using IServiceScope disposalScope = fixture.Services.CreateScope();
        using IServiceScope holdScope = fixture.Services.CreateScope();
        Task<AttachmentDisposalOutcome> discarding = DisposalOver(disposalScope, store)
            .DiscardAsync(attachment.Reference, CancellationToken.None);
        (await Task.WhenAny(store.Reached, Task.Delay(ArrivalBudget))).ShouldBe(store.Reached);

        Task<AttachmentDependencyOutcome> holding = Registry(holdScope).HoldAsync(
            attachment.Reference,
            AttachmentDependencyReasons.ClaimConfirmed,
            holder,
            CancellationToken.None);

        (await Task.WhenAny(holding, Task.Delay(BlockedBudget))).ShouldNotBe(
            holding,
            "a hold cannot be written while the bytes it would protect are being removed.");
        (await LiveDependencyCountAsync(attachment.Id)).ShouldBe(0);

        store.Release();
        AttachmentDisposalOutcome outcome = await discarding;

        outcome.Status.ShouldBe(AttachmentDisposalStatus.Discarded);
        outcome.DiscardedGenerations.ShouldBe(1);
        (await holding).ShouldBe(AttachmentDependencyOutcome.Recorded);
        (await fixture.ObjectVersionsAsync(generation.Key)).ShouldBeEmpty();
    }

    private static AttachmentDependencyRegistry Registry(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<AttachmentDependencyRegistry>();

    private static AttachmentDependencyRegistry RegistryWith(
        IServiceScope scope,
        TimeProvider clock)
        => new(
            scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>(),
            clock);

    private static AttachmentDisposal Disposal(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<AttachmentDisposal>();

    private static AttachmentDisposal DisposalOver(
        IServiceScope scope,
        IAttachmentObjectStore objectStore)
        => new(
            scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>(),
            objectStore,
            NullLogger<AttachmentDisposal>.Instance);

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

    private async Task AssertBytesAreStillThereAsync(
        AttachmentObjectGeneration generation,
        string content)
    {
        (await fixture.ObjectVersionsAsync(generation.Key))
            .Select(version => version.VersionId)
            .ShouldBe([generation.Version]);
        (await fixture.ReadObjectAsync(
            new AttachmentObjectVersion(generation.Key, generation.Version, false)))
            .ShouldBe(content);
    }

    private async Task<AttachmentObjectGeneration> SingleGenerationAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .ObjectGenerations
            .AsNoTracking()
            .SingleAsync(generation => generation.AttachmentId == attachmentId);
    }

    private async Task<AttachmentDependency> SingleDependencyAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .AttachmentDependencies
            .AsNoTracking()
            .SingleAsync(dependency => dependency.AttachmentId == attachmentId);
    }

    private async Task<int> LiveDependencyCountAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .AttachmentDependencies
            .AsNoTracking()
            .CountAsync(dependency => dependency.AttachmentId == attachmentId
                && dependency.ReleasedAt == null);
    }

    /// <summary>
    /// Stops the first reading of the clock until it is let go, and reports
    /// when that reading arrived. The seam costs the production path nothing:
    /// a hold already reads a clock between taking the attachment row and
    /// writing the dependency, which is the window under test.
    /// </summary>
    private sealed class PausingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _readings;

        internal Task Reached => _reached.Task;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _readings) == 1)
            {
                _reached.SetResult();
                _release.Task.GetAwaiter().GetResult();
            }

            return now;
        }

        internal void Release() => _release.SetResult();
    }

    /// <summary>
    /// Answers that the removal did not happen, and refuses every other call,
    /// so a run that takes a path this test does not describe fails loudly.
    /// </summary>
    private sealed class RefusingObjectStore : IAttachmentObjectStore
    {
        private const string OnlyRemovals = "This store answers removals and nothing else.";

        public Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
            => throw new NotSupportedException(OnlyRemovals);

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
            => throw new NotSupportedException(OnlyRemovals);

        public Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _ = locator;
            _ = cancellationToken;
            return Task.FromResult(AttachmentObjectDiscard.Unavailable);
        }
    }

    /// <summary>
    /// Holds the removal open until it is let go, so a test can act while a
    /// disposal sits between its decision and the bytes being gone. The
    /// removal itself, when it finally runs, is the real store's.
    /// </summary>
    private sealed class PausingObjectStore(IAttachmentObjectStore inner) : IAttachmentObjectStore
    {
        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Reached => _reached.Task;

        public Task<AttachmentObjectCapture> PutAsync(
            AttachmentObjectRequest request,
            Stream content,
            CancellationToken cancellationToken)
            => inner.PutAsync(request, content, cancellationToken);

        public Task<AttachmentStoreOpen> OpenAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
            => inner.OpenAsync(locator, cancellationToken);

        public async Task<AttachmentObjectDiscard> DiscardAsync(
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            _reached.TrySetResult();
            await _release.Task;
            return await inner.DiscardAsync(locator, cancellationToken);
        }

        internal void Release() => _release.SetResult();
    }
}
