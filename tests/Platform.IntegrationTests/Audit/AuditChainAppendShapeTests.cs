using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// The shape of the append inside the chain lock: how many statements it holds
/// the lock for, and which isolation levels it accepts. Both are correctness
/// properties rather than performance ones, because the number of statements is
/// only safe to reduce while every statement after the lock takes a fresh
/// snapshot.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class AuditChainAppendShapeTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task An_append_sends_three_statements_over_the_caller_transaction()
    {
        var counter = new DatabaseRoundTrips();
        await using CountedTransaction transaction = await counter.BeginAsync(fixture.PostgresConnectionString);

        await new TransactionalAuditTrail().AppendAsync(
            transaction, EntryFor($"shape-round-trips-{Guid.CreateVersion7():N}"), CancellationToken.None);
        await transaction.CommitAsync();

        // The lock, the sequence value and the isolation check ride in one
        // statement; the previous hash needs a statement of its own so that it
        // reads a snapshot taken after the lock was granted; the insert closes
        // the window. A fourth statement means the fold came apart, a second
        // means the tail read was folded back in and the chain can fork.
        counter.Count.ShouldBe(3);
    }

    [RequiresDockerFact]
    public async Task An_append_in_repeatable_read_is_refused_before_it_takes_the_chain_lock()
    {
        var counter = new DatabaseRoundTrips();
        await using CountedTransaction transaction = await counter.BeginAsync(
            fixture.PostgresConnectionString, IsolationLevel.RepeatableRead);

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => new TransactionalAuditTrail().AppendAsync(
                transaction, EntryFor($"shape-repeatable-{Guid.CreateVersion7():N}"), CancellationToken.None));

        failure.Message.ShouldContain("READ COMMITTED");
        failure.Message.ShouldContain("fork the chain");

        // Refused with nothing sent: the caller that picked the level on
        // purpose never reaches the lock, so a rejected append cannot hold the
        // partition while its transaction unwinds.
        counter.Count.ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task An_append_in_serializable_is_refused()
    {
        var counter = new DatabaseRoundTrips();
        await using CountedTransaction transaction = await counter.BeginAsync(
            fixture.PostgresConnectionString, IsolationLevel.Serializable);

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => new TransactionalAuditTrail().AppendAsync(
                transaction, EntryFor($"shape-serializable-{Guid.CreateVersion7():N}"), CancellationToken.None));

        failure.Message.ShouldContain("READ COMMITTED");
        counter.Count.ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task An_append_is_refused_when_the_transaction_raised_its_level_after_it_began()
    {
        var counter = new DatabaseRoundTrips();
        await using CountedTransaction transaction = await counter.BeginAsync(fixture.PostgresConnectionString);

        // The driver still reports what the caller asked for, so only the
        // server knows the transaction is no longer READ COMMITTED. This is the
        // shape a session, database or role default takes as well: nobody
        // writes a stronger level at the call site and the snapshot moves
        // anyway.
        await using (NpgsqlCommand raise = transaction.Inner.Connection!.CreateCommand())
        {
            raise.Transaction = transaction.Inner;
            raise.CommandText = "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE";
            await raise.ExecuteNonQueryAsync();
        }

        transaction.IsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => new TransactionalAuditTrail().AppendAsync(
                transaction, EntryFor($"shape-raised-{Guid.CreateVersion7():N}"), CancellationToken.None));

        failure.Message.ShouldContain("serializable");
        failure.Message.ShouldContain("READ COMMITTED");
    }

    [RequiresDockerFact]
    public async Task An_append_in_the_level_the_driver_opens_by_default_is_accepted()
    {
        var entityId = $"shape-default-{Guid.CreateVersion7():N}";
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        // The guard has to accept what every writer of the repository actually
        // opens, and what the server runs that transaction under is the fact
        // the guard reads: a driver that stopped normalizing the level, or a
        // server default someone changed, would show up right here.
        await using (NpgsqlCommand observed = connection.CreateCommand())
        {
            observed.Transaction = transaction;
            observed.CommandText = "SELECT current_setting('transaction_isolation')";
            (await observed.ExecuteScalarAsync()).ShouldBe("read committed");
        }

        await new TransactionalAuditTrail().AppendAsync(
            transaction, EntryFor(entityId), CancellationToken.None);
        await transaction.CommitAsync();

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent stored = await db.AuditEvents.AsNoTracking()
                .SingleAsync(auditEvent => auditEvent.EntityId == entityId);
            stored.Hash.ShouldNotBeNull();
        });
    }

    private static AuditEntry EntryFor(string entityId) => new()
    {
        ActorType = AuditActorTypes.System,
        ActorId = "append-shape-tests",
        Action = AuditActions.TemplateCreated,
        EntityType = AuditEntityTypes.Template,
        EntityId = entityId,
        DetailsJson = """{"origin":"append-shape-test"}""",
        OccurredAt = DateTimeOffset.UtcNow,
    };
}
