using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// The claim as the module publishes it: a whole set taken inside a
/// transaction the caller owns, over the connection the caller opened.
/// <para>
/// The caller here is the test, which is the point. The contract promises to
/// join any transaction handed to it, and holding it against a transaction
/// this module never opened is what shows the promise is about the
/// transaction and not about a context of its own.
/// </para>
/// </summary>
[Collection(AttachmentManagementApiCollectionDefinition.Name)]
public sealed class AttachmentClaimTests(AttachmentManagementApiFixture fixture)
{
    private const string Application = "araia-claim";

    /// <summary>
    /// The set comes back in the order it was asked for, member by member,
    /// with what each attachment was released as.
    /// <para>
    /// The order asked for is deliberately not the order the references sort
    /// in, because the claim walks the set in sorted order to take its locks
    /// and the snapshot has to come back in the order of the request anyway.
    /// A snapshot built from the walk would pass an oracle whose request
    /// happened to be sorted already.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task Claims_the_whole_set_and_answers_it_in_the_order_the_request_declared()
    {
        SeededAttachment[] seeded =
        [
            await ClaimableAttachments.ReleasedAsync(fixture, Application, "primeiro.pdf", "application/pdf", 11),
            await ClaimableAttachments.ReleasedAsync(fixture, Application, "segundo.png", "image/png", 22),
            await ClaimableAttachments.ReleasedAsync(fixture, Application, "terceiro.txt", "text/plain", 33),
        ];
        SeededAttachment[] asked = [.. seeded.OrderByDescending(item => item.Reference, StringComparer.Ordinal)];
        var claimKey = NewKey();

        AttachmentClaimOutcome outcome = await ClaimAsync(
            claimKey, [.. asked.Select(item => item.Reference)], commit: true);

        outcome.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        AcceptedAttachmentSet accepted = outcome.Accepted.ShouldNotBeNull();
        accepted.Count.ShouldBe(3);
        accepted.Select(member => member.Reference)
            .ShouldBe(asked.Select(item => item.Reference), ignoreOrder: false);
        accepted.Select(member => member.ContentIdentity)
            .ShouldBe(asked.Select(item => item.ContentIdentity), ignoreOrder: false);
        accepted.Select(member => member.Name)
            .ShouldBe(asked.Select(item => item.Name), ignoreOrder: false);
        accepted.Select(member => member.MediaType)
            .ShouldBe(asked.Select(item => item.MediaType), ignoreOrder: false);
        accepted.Select(member => member.Length)
            .ShouldBe(asked.Select(item => item.Length), ignoreOrder: false);

        // The falsifying half of the ordering claim: the request order is not
        // the sorted order, so a snapshot in sorted order would be a different
        // sequence from the one asserted above.
        asked.Select(item => item.Reference)
            .ShouldNotBe(seeded.Select(item => item.Reference).Order(StringComparer.Ordinal));

        foreach (SeededAttachment attachment in seeded)
        {
            IReadOnlyList<HeldDependency> holds =
                await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id);
            HeldDependency hold = holds.ShouldHaveSingleItem();
            hold.Reason.ShouldBe("claim-confirmed");
            hold.Version.ShouldBe(1);
            hold.Holder.ShouldBe(HolderOf(claimKey));
        }
    }

    /// <summary>
    /// Nothing the claim wrote outlives a caller that rolls back. It is the
    /// whole reason the claim runs on the caller's transaction: the acceptance
    /// that asked for it decides, alone, whether any of it becomes durable.
    /// </summary>
    [RequiresDockerFact]
    public async Task Nothing_the_claim_wrote_survives_a_caller_that_rolls_back()
    {
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(fixture, Application);

        AttachmentClaimOutcome outcome = await ClaimAsync(
            NewKey(), [attachment.Reference], commit: false);

        outcome.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        (await ClaimableAttachments.HoldCountAsync(fixture, attachment.Id)).ShouldBe(0);
    }

    /// <summary>
    /// The same key over the same set is the same claim. It answers with the
    /// set it accepted and writes nothing a second time, which is what a retry
    /// of an acceptance whose commit was never confirmed depends on.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_same_key_over_the_same_set_answers_the_same_claim_and_writes_nothing_new()
    {
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(fixture, Application);
        var claimKey = NewKey();

        AttachmentClaimOutcome first = await ClaimAsync(claimKey, [attachment.Reference], commit: true);
        HeldDependency written =
            (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).ShouldHaveSingleItem();
        AttachmentClaimOutcome repeated = await ClaimAsync(claimKey, [attachment.Reference], commit: true);

        repeated.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        repeated.Accepted.ShouldBe(first.Accepted);

        // The row is the one the first claim wrote: same instant, same
        // version, and no second row beside it.
        HeldDependency after =
            (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).ShouldHaveSingleItem();
        after.Version.ShouldBe(1);
        after.AcquiredAt.ShouldBe(written.AcquiredAt);
        (await ClaimableAttachments.HoldCountAsync(fixture, attachment.Id)).ShouldBe(1);
    }

    /// <summary>
    /// The same key over another set is two different sets asking to be the
    /// one that key stands for, and the refusal leaves the set that was not
    /// claimed untouched.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_same_key_over_a_different_set_is_a_conflict_that_writes_nothing()
    {
        SeededAttachment claimed = await ClaimableAttachments.ReleasedAsync(fixture, Application);
        SeededAttachment other = await ClaimableAttachments.ReleasedAsync(fixture, Application);
        var claimKey = NewKey();
        await ClaimAsync(claimKey, [claimed.Reference], commit: true);

        AttachmentClaimOutcome conflict = await ClaimAsync(
            claimKey, [claimed.Reference, other.Reference], commit: true);

        conflict.Status.ShouldBe(AttachmentClaimStatus.ClaimKeyConflict);
        conflict.Accepted.ShouldBeNull();
        (await ClaimableAttachments.HoldCountAsync(fixture, other.Id)).ShouldBe(0);
        (await ClaimableAttachments.LiveHoldsAsync(fixture, claimed.Id)).Count.ShouldBe(1);
    }

    /// <summary>
    /// A claim accepts the whole set or it changes nothing. One member that
    /// may not be claimed is enough, and the members that could have been are
    /// left exactly as they were.
    /// </summary>
    [RequiresDockerTheory]
    [InlineData("received")]
    [InlineData("revoked")]
    [InlineData("unknown")]
    [InlineData("foreign")]
    public async Task One_member_that_cannot_be_claimed_leaves_the_whole_set_untouched(string spoiler)
    {
        SeededAttachment claimable = await ClaimableAttachments.ReleasedAsync(fixture, Application);
        (var reference, Guid? spoiled) = await SpoilerAsync(spoiler);

        AttachmentClaimOutcome outcome = await ClaimAsync(
            NewKey(), [claimable.Reference, reference], commit: true);

        outcome.Status.ShouldBe(AttachmentClaimStatus.NotClaimable);
        outcome.Accepted.ShouldBeNull();
        (await ClaimableAttachments.HoldCountAsync(fixture, claimable.Id)).ShouldBe(0);
        if (spoiled is { } spoiledId)
        {
            (await ClaimableAttachments.HoldCountAsync(fixture, spoiledId)).ShouldBe(0);
        }

        // The neighbour of the four refusals, so that none of them can grow
        // into a rule that refuses every set.
        AttachmentClaimOutcome alone = await ClaimAsync(NewKey(), [claimable.Reference], commit: true);
        alone.Status.ShouldBe(AttachmentClaimStatus.Claimed);
    }

    /// <summary>
    /// A claim that already happened keeps answering after the release behind
    /// it is taken back. The snapshot froze what was accepted and it froze no
    /// permission: whether the set may still be sent is read again, later, by
    /// the check that exists for it, and a retry of an acceptance that already
    /// happened must not be turned into a rejection by that reading.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_claim_that_already_happened_survives_the_release_being_taken_back()
    {
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(fixture, Application);
        var claimKey = NewKey();
        AttachmentClaimOutcome first = await ClaimAsync(claimKey, [attachment.Reference], commit: true);
        await RevokeAsync(attachment.Id);

        AttachmentClaimOutcome repeated = await ClaimAsync(claimKey, [attachment.Reference], commit: true);

        repeated.Status.ShouldBe(AttachmentClaimStatus.Claimed);
        repeated.Accepted.ShouldBe(first.Accepted);

        // The falsifying half: the state really did change, so a claim taken
        // now under another key is refused.
        AttachmentClaimOutcome fresh = await ClaimAsync(NewKey(), [attachment.Reference], commit: true);
        fresh.Status.ShouldBe(AttachmentClaimStatus.NotClaimable);
    }

    /// <summary>
    /// Two claims that name the same attachments in opposite orders wait for
    /// each other instead of dying. The set is walked in the order of the
    /// reference and not in the order of the request, so every claimer takes
    /// the same lock first and the second one queues behind the first.
    /// <para>
    /// The blocker is what makes the ordering observable. It holds the row
    /// that sorts first, so the claim that starts first is already waiting on
    /// it when the second starts: a claim that walked the request order would
    /// take the other row meanwhile, and the two would hold one row each and
    /// wait for the other.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task Two_sets_named_in_opposite_orders_wait_for_each_other_instead_of_deadlocking()
    {
        SeededAttachment[] pair =
        [
            await ClaimableAttachments.ReleasedAsync(fixture, Application),
            await ClaimableAttachments.ReleasedAsync(fixture, Application),
        ];
        SeededAttachment[] sorted = [.. pair.OrderBy(item => item.Reference, StringComparer.Ordinal)];

        await using var blocker = new NpgsqlConnection(fixture.PostgresConnectionString);
        await blocker.OpenAsync();
        NpgsqlTransaction hold = await blocker.BeginTransactionAsync();
        await using (NpgsqlCommand lockRow = blocker.CreateCommand())
        {
            lockRow.Transaction = hold;
            lockRow.CommandText =
                "SELECT id FROM attachmentmanagement.attachment WHERE reference = @reference FOR UPDATE";
            lockRow.Parameters.AddWithValue("reference", sorted[0].Reference);
            (await lockRow.ExecuteScalarAsync()).ShouldNotBeNull();
        }

        Task<AttachmentClaimOutcome> ascending = Task.Run(() => ClaimAsync(
            NewKey(), [sorted[0].Reference, sorted[1].Reference], commit: true));
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        Task<AttachmentClaimOutcome> descending = Task.Run(() => ClaimAsync(
            NewKey(), [sorted[1].Reference, sorted[0].Reference], commit: true));
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        // Both claims are waiting on the row this transaction holds; letting
        // it go is what starts the sequence a wrong order would deadlock in.
        await hold.RollbackAsync();
        await hold.DisposeAsync();

        AttachmentClaimOutcome[] outcomes = await Task.WhenAll(ascending, descending);
        outcomes.Select(outcome => outcome.Status)
            .ShouldAllBe(status => status == AttachmentClaimStatus.Claimed);
        foreach (SeededAttachment attachment in pair)
        {
            (await ClaimableAttachments.LiveHoldsAsync(fixture, attachment.Id)).Count.ShouldBe(2);
        }
    }

    private static string NewKey() => $"claim-{Guid.NewGuid():N}";

    private static string HolderOf(string claimKey)
        => TransactionalAttachmentClaim.HolderOf(Application, claimKey);

    /// <summary>
    /// Runs one claim as a caller that owns the transaction: it opens the
    /// connection, declares the level, hands the transaction over and decides
    /// alone whether any of it becomes durable.
    /// </summary>
    private async Task<AttachmentClaimOutcome> ClaimAsync(
        string claimKey,
        string[] references,
        bool commit)
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
                Application = Application,
                ClaimKey = claimKey,
                References = AttachmentReferences.Of(references),
            },
            CancellationToken.None);
        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return outcome;
    }

    /// <summary>One member that cannot be claimed, in each of the four ways there are.</summary>
    private async Task<(string Reference, Guid? Id)> SpoilerAsync(string kind)
    {
        switch (kind)
        {
            case "received":
                SeededAttachment received = await ClaimableAttachments.ReceivedAsync(fixture, Application);
                return (received.Reference, received.Id);
            case "revoked":
                SeededAttachment revoked = await ClaimableAttachments.RevokedAsync(fixture, Application);
                return (revoked.Reference, revoked.Id);
            case "foreign":
                SeededAttachment foreign = await ClaimableAttachments.ReleasedAsync(
                    fixture, Application + "-outra");
                return (foreign.Reference, foreign.Id);
            default:
                return ($"att_{Guid.NewGuid():N}", null);
        }
    }

    private async Task RevokeAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        Attachment attachment = await dbContext.Attachments
            .Where(candidate => candidate.Id == attachmentId)
            .SingleAsync();
        attachment.Revoke();
        await dbContext.SaveChangesAsync();
    }
}
