using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.TemplateManagement;
using StackExchange.Redis;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// The acceptance of a request that names attachments, as one transaction.
/// <para>
/// The invariant is a shape of durable state that must never exist: a
/// notification accepted over a set it does not hold. Nothing downstream can
/// repair it, because the acceptance is what the producer was told happened,
/// so the oracles here are about what is left behind at each point the unit
/// can fail, and never about what the answer said.
/// </para>
/// </summary>
[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class AcceptanceClaimTransactionTests(NotificationsApiFixture fixture)
{
    private const string Route = "/v1/notifications";

    /// <summary>
    /// A set that may not be claimed refuses the request instead of accepting
    /// a notification over files it does not hold, and the members that could
    /// have been claimed are left exactly as they were.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_manifest_that_cannot_be_claimed_never_becomes_a_notification()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-claim-refused", NotificationsApi.SendTransactional);
        SeededAttachment claimable = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);
        SeededAttachment waiting = await ClaimableAttachments.ReceivedAsync(
            fixture, NotificationsApi.Application);
        var idempotencyKey = $"claim-refused-{Guid.NewGuid():N}";

        HttpResponseMessage refused = await PostAsync(
            producer,
            Body(templateKey, [claimable.Reference, waiting.Reference]),
            idempotencyKey);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await NotificationsApi.ReadJsonAsync(refused);
        problem.GetProperty("type").GetString().ShouldBe("attachments-not-claimable");
        (await NotificationCountAsync(idempotencyKey)).ShouldBe(0);
        (await RegistrationCountAsync(idempotencyKey)).ShouldBe(0);
        (await ClaimableAttachments.HoldCountAsync(fixture, claimable.Id)).ShouldBe(0);

        // The falsifying half: the same request over a set this application
        // holds is accepted, so the refusal above belongs to the member that
        // was never released.
        HttpResponseMessage accepted = await PostAsync(
            producer,
            Body(templateKey, [claimable.Reference]),
            $"claim-allowed-{Guid.NewGuid():N}");
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ClaimableAttachments.LiveHoldsAsync(fixture, claimable.Id)).Count.ShouldBe(1);
    }

    /// <summary>
    /// Every point the unit can fail at, and the same durable state after each
    /// of them: none. The five points walk the decided order of the
    /// transaction, so a claim that had escaped it would show up as a hold
    /// nobody can name after a failure that happened later.
    /// </summary>
    [RequiresDockerTheory]
    [InlineData("claim")]
    [InlineData("after-claim")]
    [InlineData("after-notification")]
    [InlineData("after-outbox")]
    [InlineData("after-audit")]
    public async Task A_failure_before_the_commit_leaves_no_notification_and_no_claim(string point)
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);
        var idempotencyKey = $"failure-{point}-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        object body = Body(templateKey, [attachment.Reference], recipientId);

        using WebApplicationFactory<Program> broken = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(FailureAt(point)));
        HttpClient producer = fixture.CreateProducerClient(
            broken, "producer-failure", NotificationsApi.SendTransactional);

        HttpResponseMessage failed = await PostAsync(producer, body, idempotencyKey);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await NotificationCountAsync(idempotencyKey)).ShouldBe(0);
        (await RegistrationCountAsync(idempotencyKey)).ShouldBe(0);
        (await OutboxCountAsync(recipientId)).ShouldBe(0);
        (await ClaimableAttachments.HoldCountAsync(fixture, attachment.Id)).ShouldBe(0);

        // The healthy host answers the same key as a first acceptance, which
        // is only true because the failed unit registered nothing anywhere,
        // including in the fast path in front of the authority.
        HttpClient healthy = fixture.CreateProducerClient(
            "producer-failure", NotificationsApi.SendTransactional);
        HttpResponseMessage retried = await PostAsync(healthy, body, idempotencyKey);
        retried.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).Count.ShouldBe(1);
    }

    /// <summary>
    /// A commit whose answer never reached the producer, retried. The
    /// registration answers it, the claim is not taken a second time, and the
    /// hold that exists is the one the acceptance wrote.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_retry_after_an_answer_that_was_lost_claims_nothing_twice()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-lost-answer", NotificationsApi.SendTransactional);
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);
        var idempotencyKey = $"lost-answer-{Guid.NewGuid():N}";
        object body = Body(templateKey, [attachment.Reference]);

        HttpResponseMessage accepted = await PostAsync(producer, body, idempotencyKey);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        HeldDependency written =
            (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).ShouldHaveSingleItem();

        // The cached acceptance answers first, then the registration, once the
        // cached entry is gone: both authorities have to answer the retry the
        // same way, and only the second one is durable.
        HttpResponseMessage cached = await PostAsync(producer, body, idempotencyKey);
        await RemoveFastPathEntryAsync(idempotencyKey);
        HttpResponseMessage stored = await PostAsync(producer, body, idempotencyKey);

        cached.StatusCode.ShouldBe(HttpStatusCode.OK);
        stored.StatusCode.ShouldBe(HttpStatusCode.OK);
        var acceptedId = await NotificationIdAsync(accepted);
        (await NotificationIdAsync(cached)).ShouldBe(acceptedId);
        (await NotificationIdAsync(stored)).ShouldBe(acceptedId);
        (await NotificationCountAsync(idempotencyKey)).ShouldBe(1);

        HeldDependency after =
            (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).ShouldHaveSingleItem();
        after.Version.ShouldBe(1);
        after.AcquiredAt.ShouldBe(written.AcquiredAt);
        (await ClaimableAttachments.HoldCountAsync(fixture, attachment.Id)).ShouldBe(1);
    }

    /// <summary>
    /// The unit that loses the race on the idempotency key keeps nothing: not
    /// the claim it wrote, and not the row locks it took to write it.
    /// <para>
    /// The race is made deterministic rather than hoped for. The winning
    /// registration is committed on another connection at the one instant that
    /// matters, right after this unit claimed the set, so the insert that
    /// follows violates the unique key with a claim already written and every
    /// row of the set held.
    /// </para>
    /// <para>
    /// The locks are the half a count cannot see. The writer asks the server
    /// whether the session is still inside a transaction before it reads the
    /// winner, and answering yes is a refusal, so a read taken with the claim
    /// still held would end this request in a failure instead of the answer
    /// asserted below.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_loser_of_the_race_on_one_key_keeps_no_claim_and_no_lock()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-race-loser", NotificationsApi.SendTransactional);
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);
        var winnerKey = $"race-winner-{Guid.NewGuid():N}";
        var loserKey = $"race-loser-{Guid.NewGuid():N}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        object body = Body(templateKey, [attachment.Reference], recipientId);

        // One accepted request, to take the payload hash of this exact body
        // from the authority instead of recomputing it here.
        HttpResponseMessage first = await PostAsync(producer, body, winnerKey);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var payloadHash = await PayloadHashAsync(winnerKey);
        Guid winnerNotificationId = Guid.CreateVersion7();

        using WebApplicationFactory<Program> racing = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(AttachmentClaimDecoration.Wrap(inner =>
                new ClaimThenLoseTheRace(
                    inner,
                    fixture.PostgresConnectionString,
                    NotificationsApi.Application,
                    loserKey,
                    payloadHash,
                    winnerNotificationId))));
        HttpClient losing = fixture.CreateProducerClient(
            racing, "producer-race-loser", NotificationsApi.SendTransactional);

        HttpResponseMessage lost = await PostAsync(losing, body, loserKey);

        // The answer is the winner's, and it exists: a read taken inside the
        // losing transaction would have refused instead.
        lost.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NotificationIdAsync(lost)).ShouldBe(NotificationId(winnerNotificationId));

        // The losing unit left no hold of its own, and the one hold that
        // exists is the one the accepted request wrote.
        IReadOnlyList<HeldDependency> holds =
            await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id);
        HeldDependency hold = holds.ShouldHaveSingleItem();
        hold.Holder.ShouldBe(TransactionalAttachmentClaim.HolderOf(
            NotificationsApi.Application, winnerKey));
        holds.ShouldNotContain(candidate => candidate.Holder
            == TransactionalAttachmentClaim.HolderOf(NotificationsApi.Application, loserKey));
        (await ClaimableAttachments.HoldCountAsync(fixture, attachment.Id)).ShouldBe(1);
    }

    /// <summary>
    /// Two units in flight under one key, over one set. One notification, one
    /// claim, and one hold: the other unit answers with the acceptance that
    /// won and leaves nothing of its own.
    /// </summary>
    [RequiresDockerFact]
    public async Task Two_requests_in_flight_under_one_key_claim_the_set_once()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);
        var idempotencyKey = $"race-{Guid.NewGuid():N}";
        object body = Body(templateKey, [attachment.Reference]);
        HttpClient one = fixture.CreateProducerClient(
            "producer-race-one", NotificationsApi.SendTransactional);
        HttpClient two = fixture.CreateProducerClient(
            "producer-race-two", NotificationsApi.SendTransactional);

        HttpResponseMessage[] answers = await Task.WhenAll(
            PostAsync(one, body, idempotencyKey),
            PostAsync(two, body, idempotencyKey));

        answers.Select(answer => answer.StatusCode)
            .ShouldAllBe(status => status == HttpStatusCode.Accepted || status == HttpStatusCode.OK);
        answers.Count(answer => answer.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        var identifiers = await Task.WhenAll(answers.Select(NotificationIdAsync));
        identifiers.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        (await NotificationCountAsync(idempotencyKey)).ShouldBe(1);

        HeldDependency hold =
            (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).ShouldHaveSingleItem();
        hold.Version.ShouldBe(1);
        hold.Holder.ShouldBe(TransactionalAttachmentClaim.HolderOf(
            NotificationsApi.Application, idempotencyKey));
        (await ClaimableAttachments.HoldCountAsync(fixture, attachment.Id)).ShouldBe(1);
    }

    /// <summary>
    /// The three facts a reading of the source cannot settle, measured from
    /// inside the acceptance transaction while the claim runs: the level the
    /// server is running it under, what the acceptance had written by then,
    /// and which sessions hold the locks of the claim.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_claim_runs_under_read_committed_before_the_outbox_and_before_the_audit()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);
        var recipientId = $"cus_{Guid.NewGuid():N}";
        AttachmentClaimProbe? probe = null;
        using WebApplicationFactory<Program> probed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(AttachmentClaimDecoration.Wrap(inner =>
                probe = new AttachmentClaimProbe(inner, fixture.PostgresConnectionString))));
        HttpClient producer = fixture.CreateProducerClient(
            probed, "producer-probe", NotificationsApi.SendTransactional);

        var idempotencyKey = $"probe-{Guid.NewGuid():N}";
        HttpResponseMessage accepted = await PostAsync(
            producer,
            Body(templateKey, [attachment.Reference], recipientId),
            idempotencyKey);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        AttachmentClaimProbe observed = probe.ShouldNotBeNull();
        observed.Isolation.ShouldBe("read committed");

        // Nothing of the acceptance was written when the claim ran, and all
        // three rows exist once it committed: the claim is first, and the
        // audit append is still the last thing before the commit.
        observed.NotificationRowsBeforeClaim.ShouldBe(0);
        observed.OutboxRowsBeforeClaim.ShouldBe(0);
        observed.AuditRowsBeforeClaim.ShouldBe(0);
        (await OutboxCountAsync(recipientId)).ShouldBe(1);
        (await AuditCountAsync(await StoredNotificationIdAsync(idempotencyKey))).ShouldBe(1);

        // One session held the locks of the claim, and it is the session the
        // acceptance transaction runs on. A claim that had opened a connection
        // of its own would be a second session here.
        observed.SessionsHoldingAttachmentLocks.ShouldBe([observed.AcceptancePid]);
    }

    /// <summary>
    /// The claim uses the caller's connection and no other, measured by taking
    /// its own away: the module is pointed at a database that does not exist,
    /// and the acceptance still claims the set and commits it.
    /// <para>
    /// It is the half the lock reading cannot give. A session that opened a
    /// connection, wrote and closed it before the reading would not be there
    /// to be counted; a claim that needs a connection of its own cannot even
    /// start here.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_claim_opens_no_connection_of_the_module_it_belongs_to()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application);
        var idempotencyKey = $"no-second-connection-{Guid.NewGuid():N}";

        using WebApplicationFactory<Program> withoutStore = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{AttachmentManagementEfOptions.SectionName}:ConnectionString"] =
                        AbsentStoreConnectionString(),
                })));
        HttpClient producer = fixture.CreateProducerClient(
            withoutStore, "producer-no-second-connection", NotificationsApi.SendTransactional);

        HttpResponseMessage accepted = await PostAsync(
            producer, Body(templateKey, [attachment.Reference]), idempotencyKey);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).Count.ShouldBe(1);

        // The falsifying half: the connection string really is unusable, so a
        // claim that had opened one of its own could not have answered.
        using IServiceScope scope = withoutStore.Services.CreateScope();
        await Should.ThrowAsync<Exception>(async () => await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Database.OpenConnectionAsync());
    }

    /// <summary>
    /// The store the module is configured with, pointed at a database nobody
    /// created, on a host nobody answers.
    /// </summary>
    private static string AbsentStoreConnectionString()
        => "Host=127.0.0.1;Port=1;Database=anexos_inexistente;Username=ninguem;"
            + "Password=ninguem;Timeout=2;Command Timeout=2";

    private static Action<IServiceCollection> FailureAt(string point) => point switch
    {
        "claim" => services =>
        {
            services.RemoveAll<IAttachmentClaim>();
            services.AddSingleton<IAttachmentClaim>(new ThrowingAttachmentClaim());
        },
        "after-claim" => AttachmentClaimDecoration.Wrap(inner => new ClaimThenFail(inner)),
        "after-notification" => services =>
        {
            services.RemoveAll<IOutboxWriter>();
            services.AddSingleton<IOutboxWriter>(new FailingOutboxWriter());
        },
        "after-outbox" => services =>
        {
            services.RemoveAll<IAuditTrail>();
            services.AddSingleton<IAuditTrail>(new NeverAppendingAuditTrail());
        },
        "after-audit" => services =>
        {
            ServiceDescriptor original = services.Last(
                descriptor => descriptor.ServiceType == typeof(IAuditTrail));
            services.Remove(original);
            services.AddSingleton<IAuditTrail>(provider => new AppendThenFailAuditTrail(
                (IAuditTrail)ActivatorUtilities.CreateInstance(
                    provider, original.ImplementationType!)));
        },
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Ponto de falha desconhecido."),
    };

    private static string NotificationId(Guid value)
        => NotificationHub.Api.Modules.Notifications.Domain.NotificationId.Format(value);

    private static Dictionary<string, object?> Body(
        string templateKey,
        IReadOnlyList<string> attachments,
        string? recipientId = null)
        => new(StringComparer.Ordinal)
        {
            ["application"] = NotificationsApi.Application,
            ["recipientId"] = recipientId ?? $"cus_{Guid.NewGuid():N}",
            ["class"] = "transactional",
            ["templateKey"] = templateKey,
            ["variables"] = new { orderId = "ord-1" },
            ["ttlSeconds"] = 300,
            ["attachments"] = attachments,
        };

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<string> NotificationIdAsync(HttpResponseMessage response)
        => (await NotificationsApi.ReadJsonAsync(response))
            .GetProperty("notificationId").GetString()!;

    private async Task<int> NotificationCountAsync(string idempotencyKey)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey));

    private async Task<int> RegistrationCountAsync(string idempotencyKey)
        => await fixture.QueryNotificationsDbAsync(db => db.IdempotencyRegistrations
            .AsNoTracking()
            .CountAsync(candidate => candidate.IdempotencyKey == idempotencyKey));

    private async Task<string> PayloadHashAsync(string idempotencyKey)
        => await fixture.QueryNotificationsDbAsync(db => db.IdempotencyRegistrations
            .AsNoTracking()
            .Where(candidate => candidate.IdempotencyKey == idempotencyKey)
            .Select(candidate => candidate.PayloadHash)
            .SingleAsync());

    private async Task<int> OutboxCountAsync(string recipientId)
        => await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(candidate => candidate.MessageKey == recipientId));

    /// <summary>
    /// The identifier the acceptance persisted, in the spelling the audit
    /// trail records it under. The answer to the producer carries the public
    /// form of it, which is a different string.
    /// </summary>
    private async Task<string> StoredNotificationIdAsync(string idempotencyKey)
        => (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.IdempotencyKey == idempotencyKey)
            .Select(candidate => candidate.Id)
            .SingleAsync())).ToString();

    private async Task<int> AuditCountAsync(string notificationId)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(candidate => candidate.EntityId == notificationId));

    private async Task RemoveFastPathEntryAsync(string idempotencyKey)
    {
        ConfigurationOptions options = ConfigurationOptions.Parse(fixture.RedisConnectionString);
        options.AbortOnConnectFail = false;
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(options);
        await connection.GetDatabase().KeyDeleteAsync(
            $"{NotificationsApiFixture.RedisKeyPrefix}idem:{NotificationsApi.Application}:{idempotencyKey}");
    }
}

/// <summary>An audit trail that never appends: the failure point after the outbox.</summary>
internal sealed class NeverAppendingAuditTrail : IAuditTrail
{
    internal const string Message = "Falha induzida no append da trilha de auditoria.";

    public Task AppendAsync(
        System.Data.Common.DbTransaction transaction,
        AuditEntry entry,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException(Message);

    public Task RecordApprovalAsync(
        System.Data.Common.DbTransaction transaction,
        ApprovalGrant grant,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException(Message);
}
