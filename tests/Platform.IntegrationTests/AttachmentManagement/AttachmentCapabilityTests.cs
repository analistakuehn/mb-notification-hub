using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capability;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;
using Npgsql;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// What a deployment that has not switched the capability on does, and what it
/// keeps doing.
/// <para>
/// Two doors close and nothing else does. A new attachment is not minted and a
/// set nobody holds yet is not taken; everything that works on an attachment
/// which already exists carries on, because switching the capability off is a
/// block on new acceptances and not a freeze of what was accepted.
/// </para>
/// <para>
/// Every case here builds a host of its own with the capability off. The
/// collection's own fixture runs with it on, so each arm below is measured
/// against a neighbour that differs in the switch and in nothing else: without
/// the neighbour, a refusal would be satisfied by a grant, a reference or an
/// arrangement that could not have worked anyway.
/// </para>
/// <para>
/// What none of it proves: that the closed state is what a production host
/// actually starts in. That is a property of configuration, and the oracle for
/// it is the one that binds an empty section rather than any host built here.
/// </para>
/// </summary>
[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentCapabilityTests(AttachmentManagementApiFixture fixture)
{
    private const string Application = "araia-capability";

    /// <summary>
    /// The first door. A registration is answered before the metadata is even
    /// judged, no reference is minted, and the answer names the deployment
    /// state instead of anything about the file.
    /// </summary>
    [RequiresDockerFact]
    public async Task Registering_a_new_attachment_is_refused_and_writes_nothing_while_the_capability_is_off()
    {
        var principal = $"capability-register-{Guid.NewGuid():N}";
        await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
        using WebApplicationFactory<Program> closed = WithCapability(enabled: false);
        using HttpClient blocked = fixture.CreateProducerClient(closed, principal);
        fixture.Logs.Events.Clear();
        var before = await RegisteredCountAsync();

        using HttpResponseMessage refused = await blocked.PostAsJsonAsync(
            "/v1/attachments",
            AttachmentApi.Registration(sizeBytes: 4, application: AttachmentApi.Application));
        var body = await refused.Content.ReadAsStringAsync();

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, body);
        body.ShouldContain(ErrorCodes.CapabilityNotEnabled);
        (await RegisteredCountAsync()).ShouldBe(
            before,
            "uma linha gravada seria uma referência cunhada por uma implantação que não "
            + "aceita anexos novos.");

        // The word an operator reads in an incident. It says the capability is
        // not enabled and it deliberately does not say blocked: the emergency
        // control is a different artifact with a different meaning, and an
        // operator who confuses them looks for a decision nobody took.
        string[] logs = [.. fixture.Logs.Events.SelectMany(AttachmentApi.LogFragments)];
        logs.ShouldContain(
            fragment => fragment.Contains("não está habilitada", StringComparison.Ordinal),
            "a recusa precisa dizer que a capacidade não está habilitada nesta implantação.");

        // The neighbour: same grant, same body, same host configuration except
        // the switch. Without it the refusal above would be satisfied by a
        // grant or a payload that could not have registered anything anyway.
        using HttpClient allowed = fixture.CreateProducerClient(principal);
        (HttpResponseMessage response, AttachmentApi.ApiResponse registered) =
            await AttachmentApi.RegisterAsync(allowed, sizeBytes: 4);
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            registered.State.ShouldBe(AttachmentStates.AwaitingUpload);
        }
    }

    /// <summary>
    /// The second door. Attachments released while the capability was on are
    /// still released, and a claim nobody holds yet is still a new acceptance:
    /// it is refused, and the transaction the caller owns comes out of it with
    /// nothing written.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_set_nobody_holds_yet_is_not_claimed_while_the_capability_is_off()
    {
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(fixture, Application);
        fixture.Logs.Events.Clear();

        AttachmentClaimOutcome refused = await ClaimAsync(
            enabled: false, NewKey(), [attachment.Reference]);

        // The word names the deployment and not the set. Answering the refusal
        // every unclaimable set gets would tell the caller to stop sending this
        // set, when the set is fine and what is missing is the capability.
        refused.Status.ShouldBe(AttachmentClaimStatus.CapabilityNotEnabled);
        refused.Accepted.ShouldBeNull();
        (await ClaimableAttachments.HoldCountAsync(fixture, attachment.Id)).ShouldBe(
            0,
            "um vínculo gravado seria um aceite novo tomado por uma implantação que não "
            + "aceita anexos novos.");

        // The attachment is untouched by the refusal: it is still released, and
        // it is the same content it was released over. The door closed on the
        // acceptance and not on the attachment.
        (await fixture.QueryAttachmentAsync(attachment.Reference))
            .State.ShouldBe(AttachmentStates.Released);

        // The neighbour: the very same released set, a new key, and the switch
        // the other way. Without it the refusal above would be satisfied by an
        // attachment that was never claimable to begin with.
        AttachmentClaimOutcome claimed = await ClaimAsync(
            enabled: true, NewKey(), [attachment.Reference]);
        claimed.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        claimed.Accepted.ShouldNotBeNull().Count.ShouldBe(1);
    }

    /// <summary>
    /// The line between blocking new acceptances and freezing what is already
    /// accepted. A claim that already happened is answered again with the set
    /// it accepted, because a retry of an accepted notification is not a new
    /// acceptance and refusing it would turn every such retry into a rejection
    /// the day the capability was switched off.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_claim_that_already_happened_is_answered_again_while_the_capability_is_off()
    {
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(fixture, Application);
        var claimKey = NewKey();
        AttachmentClaimOutcome first = await ClaimAsync(
            enabled: true, claimKey, [attachment.Reference]);
        first.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        HeldDependency written =
            (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).ShouldHaveSingleItem();

        AttachmentClaimOutcome repeated = await ClaimAsync(
            enabled: false, claimKey, [attachment.Reference]);

        repeated.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        repeated.Accepted.ShouldBe(first.Accepted);

        // The same row the first claim wrote, and no second one: the repeat
        // answered from what was already held instead of taking a new hold.
        HeldDependency after =
            (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).ShouldHaveSingleItem();
        after.ShouldBe(written);
        (await ClaimableAttachments.HoldCountAsync(fixture, attachment.Id)).ShouldBe(1);
    }

    /// <summary>
    /// The repair round is one of the things the switch never reaches. An
    /// attachment left waiting on a verdict that did not conclude is still
    /// found and still settled by a round composed out of a host that takes no
    /// new attachments at all.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_reconciliation_round_still_settles_an_outstanding_repair_while_the_capability_is_off()
    {
        SeededAttachment attachment = await ClaimableAttachments.ReceivedAsync(fixture, Application);
        await HoldInconclusiveAsync(attachment.Id);
        (await fixture.QueryAttachmentAsync(attachment.Reference))
            .ReconciliationLiability.ShouldBe(AttachmentLiabilities.VerdictOpen);

        using WebApplicationFactory<Program> closed = WithCapability(enabled: false);
        using IServiceScope scope = closed.Services.CreateScope();
        AttachmentReconciliationResult round = await scope.ServiceProvider
            .GetRequiredService<AttachmentReconciliationScan>()
            .RunAsync(CancellationToken.None);

        round.Examined.ShouldBeGreaterThanOrEqualTo(1);
        Attachment settled = await fixture.QueryAttachmentAsync(attachment.Reference);
        settled.ReconciliationLiability.ShouldBeNull();
        settled.State.ShouldBe(AttachmentStates.Rejected);
    }

    /// <summary>
    /// The sweep of abandoned content is another. An attachment whose release
    /// was taken back long enough ago still loses its content to a round
    /// composed out of the same closed host, which is what keeps a deployment
    /// that switched the capability off from accumulating bytes nobody can
    /// reach and nobody removes.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_abandonment_sweep_still_discards_at_the_deadline_while_the_capability_is_off()
    {
        DateTimeOffset longAgo = DateTimeOffset.UtcNow - TimeSpan.FromDays(400);
        SeededAttachment attachment = await ClaimableAttachments.ReleasedWithContentAsync(
            fixture, Application, grantedAt: longAgo);
        await RevokeAsync(attachment.Id, longAgo + TimeSpan.FromDays(1));
        (await StoredVersionsAsync(attachment)).Length.ShouldBe(1);

        using WebApplicationFactory<Program> closed = WithCapability(enabled: false);
        using IServiceScope scope = closed.Services.CreateScope();
        AttachmentAbandonmentResult swept = await scope.ServiceProvider
            .GetRequiredService<AttachmentAbandonmentScan>()
            .RunAsync(CancellationToken.None);

        swept.Discarded.ShouldBeGreaterThanOrEqualTo(1);
        (await fixture.QueryAttachmentAsync(attachment.Reference))
            .State.ShouldBe(AttachmentStates.Discarded);
        (await StoredVersionsAsync(attachment)).ShouldBeEmpty();
    }

    /// <summary>
    /// Investigating what exists is the third. The authorized reading answers
    /// for an attachment over a host that takes nothing new, and so does the
    /// evidence of the bytes an acceptance was taken over: an operator looking
    /// into an item accepted before the reversal is not left without either.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_authorized_readings_still_answer_for_what_exists_while_the_capability_is_off()
    {
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(fixture, Application);
        using WebApplicationFactory<Program> closed = WithCapability(enabled: false);

        using HttpClient operations = fixture.CreateOperationsClient(
            closed,
            $"capability-operator-{Guid.NewGuid():N}",
            AuthorizationSetup.OperationsRole);
        using HttpResponseMessage lifecycle = await operations.GetAsync(
            $"/v1/attachment-operations/{attachment.Reference}");
        var read = await lifecycle.Content.ReadAsStringAsync();

        lifecycle.StatusCode.ShouldBe(HttpStatusCode.OK, read);
        read.ShouldContain(AttachmentStates.Released);

        using IServiceScope scope = closed.Services.CreateScope();
        IReadOnlyDictionary<string, AttachmentEvidence> evidence = await scope.ServiceProvider
            .GetRequiredService<IAttachmentEvidence>()
            .DescribeAcceptedContentAsync([attachment.ContentIdentity], CancellationToken.None);

        evidence.Keys.ShouldContain(attachment.ContentIdentity);
        AttachmentEvidence described = evidence[attachment.ContentIdentity];
        described.Reference.ShouldBe(attachment.Reference);
        described.State.ShouldBe(AttachmentStates.Released);
        described.DigestedLengthBytes.ShouldBe(attachment.Length);
    }

    /// <summary>
    /// The reversal itself. Everything an acceptance made durable is read back
    /// out of the database after the switch goes the other way: the attachment,
    /// the generation its identity is pinned to, the release it was accepted
    /// under, and the hold the claim took. Nothing is removed, because nothing
    /// on the closing path removes anything.
    /// <para>
    /// It reads the rows rather than counting the statements a rollback issued.
    /// A test that only asserted that no delete was sent would pass over an
    /// implementation that dropped the rows by any other means.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task Switching_the_capability_off_leaves_every_durable_row_where_it_was()
    {
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(fixture, Application);
        AttachmentClaimOutcome accepted = await ClaimAsync(
            enabled: true, NewKey(), [attachment.Reference]);
        accepted.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        DurableAttachment before = await DurableAsync(attachment);

        // The reversal: a host that takes nothing new, asked for exactly the
        // two things the switch closes, so the reading below happens after the
        // closed path has run rather than merely after the switch was flipped.
        using (WebApplicationFactory<Program> closed = WithCapability(enabled: false))
        {
            var principal = $"capability-rollback-{Guid.NewGuid():N}";
            await AttachmentAuthorizationTestData.SeedStandardGrantAsync(fixture.Services, principal);
            using HttpClient producer = fixture.CreateProducerClient(closed, principal);
            using HttpResponseMessage registration = await producer.PostAsJsonAsync(
                "/v1/attachments",
                AttachmentApi.Registration(sizeBytes: 4, application: AttachmentApi.Application));
            registration.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await ClaimAsync(enabled: false, NewKey(), [attachment.Reference]))
            .Status.ShouldBe(AttachmentClaimStatus.CapabilityNotEnabled);

        DurableAttachment after = await DurableAsync(attachment);
        after.ShouldBe(before);
        after.State.ShouldBe(AttachmentStates.Released);
        after.Generations.ShouldBe(1);
        after.Releases.ShouldBe(1);
        after.LiveHolds.ShouldBe(1);
    }

    /// <summary>What the acceptance left durable, as the database still holds it.</summary>
    private sealed record DurableAttachment(
        string State,
        Guid ContentId,
        int Generations,
        int Releases,
        int LiveHolds,
        int Dependencies);

    private static string NewKey() => $"capability-{Guid.NewGuid():N}";

    /// <summary>
    /// A host of the fixture's own configuration with the deployment state of
    /// the capability chosen by the caller. Both values are stated: an arm that
    /// inherited the enabled one would measure the fixture instead of the
    /// switch.
    /// </summary>
    private WebApplicationFactory<Program> WithCapability(bool enabled)
        => fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{AttachmentCapabilityOptions.SectionName}:AcceptsNewAttachments"] =
                        enabled ? "true" : "false",
                })));

    /// <summary>
    /// One claim as a caller that owns the transaction, over a host whose
    /// deployment state the case chooses. The transaction is always committed,
    /// because what a refusal has to leave behind is nothing even when the
    /// caller commits.
    /// </summary>
    private async Task<AttachmentClaimOutcome> ClaimAsync(
        bool enabled,
        string claimKey,
        string[] references)
    {
        using WebApplicationFactory<Program> host = WithCapability(enabled);
        IAttachmentClaim claim = host.Services.GetRequiredService<IAttachmentClaim>();
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted);
        AttachmentClaimOutcome outcome = await claim.ClaimAsync(
            transaction,
            new AttachmentClaimRequest
            {
                NotificationId = Guid.CreateVersion7(),
                Application = Application,
                ClaimKey = claimKey,
                References = AttachmentReferences.Of(references),
            },
            CancellationToken.None);
        await transaction.CommitAsync();
        return outcome;
    }

    private async Task<DurableAttachment> DurableAsync(SeededAttachment attachment)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        Attachment row = await fixture.QueryAttachmentAsync(attachment.Reference);
        return new DurableAttachment(
            row.State,
            row.ContentId,
            await dbContext.ObjectGenerations
                .AsNoTracking()
                .CountAsync(generation => generation.AttachmentId == attachment.Id),
            await dbContext.Releases
                .AsNoTracking()
                .CountAsync(release => release.AttachmentId == attachment.Id),
            await dbContext.AttachmentDependencies
                .AsNoTracking()
                .CountAsync(dependency => dependency.AttachmentId == attachment.Id
                    && dependency.ReleasedAt == null),
            await dbContext.AttachmentDependencies
                .AsNoTracking()
                .CountAsync(dependency => dependency.AttachmentId == attachment.Id));
    }

    /// <summary>
    /// Records a verdict that did not conclude, with a deadline already in the
    /// past, which is the state a repair round is meant to find.
    /// </summary>
    private async Task HoldInconclusiveAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        Attachment attachment = await dbContext.Attachments
            .SingleAsync(candidate => candidate.Id == attachmentId);
        attachment.HoldInconclusive(
            "verdict-never-concluded",
            DateTimeOffset.UtcNow - TimeSpan.FromDays(2),
            TimeSpan.FromHours(1))
            .ShouldBe(AttachmentValidationTransition.Applied);
        await dbContext.SaveChangesAsync();
    }

    private async Task RevokeAsync(Guid attachmentId, DateTimeOffset at)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        Attachment attachment = await dbContext.Attachments
            .SingleAsync(candidate => candidate.Id == attachmentId);
        attachment.Revoke(at).ShouldBe(AttachmentRevocationTransition.Applied);
        await dbContext.SaveChangesAsync();
    }

    private async Task<AttachmentObjectVersion[]> StoredVersionsAsync(SeededAttachment attachment)
    {
        Attachment row = await fixture.QueryAttachmentAsync(attachment.Reference);
        return await fixture.ObjectVersionsAsync($"attachments/{row.ContentId:N}");
    }

    private async Task<int> RegisteredCountAsync()
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Attachments
            .AsNoTracking()
            .CountAsync(attachment => attachment.Application == AttachmentApi.Application);
    }
}
