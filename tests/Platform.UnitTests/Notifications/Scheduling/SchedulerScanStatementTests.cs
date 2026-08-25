using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.UnitTests.Notifications.Scheduling;

/// <summary>
/// The scheduler's statements and the schema they read are two implementations
/// of one rule, and this confronts them instead of restating either.
/// <para>
/// A partial index only answers a statement whose quals imply its predicate,
/// and a predicate the planner cannot prove leaves the scan walking every
/// partition of the table without any of it failing. The plan test measures
/// that against a real database; these assertions catch the same defect at the
/// moment someone edits one side of it, and they fail with the name of the
/// index whose predicate went missing.
/// </para>
/// </summary>
public sealed partial class SchedulerScanStatementTests
{
    private const string OverdueIndex = "ix_notification_attempt_fallback_due";
    private const string UnknownIndex = "ix_notification_attempt_unknown_due";
    private const string InFlightIndex = "ix_notification_attempt_fallback_inflight";
    private const string ReleaseIndex = "ix_notification_release_due";

    [Fact]
    public void The_deadline_scan_carries_the_predicate_of_the_index_that_answers_it()
        => ShouldCarryPredicateOf<NotificationAttempt>(
            OverdueFallbackScan.DeadlineClaimSql, OverdueIndex);

    [Fact]
    public void The_unknown_scan_carries_the_predicate_of_the_index_that_answers_it()
        => ShouldCarryPredicateOf<NotificationAttempt>(
            OverdueFallbackScan.UnknownClaimSql, UnknownIndex);

    [Fact]
    public void The_stale_request_release_carries_the_predicate_of_the_index_that_answers_it()
        => ShouldCarryPredicateOf<NotificationAttempt>(
            OverdueFallbackScan.ReleaseStaleRequestSql, InFlightIndex);

    [Fact]
    public void The_release_scan_carries_the_predicate_of_the_index_that_answers_it()
        => ShouldCarryPredicateOf<Notification>(
            DeferredReleaseScan.CandidateSql, ReleaseIndex);

    /// <summary>
    /// The statuses and the class the statements spell out are the same values
    /// the rest of the module writes. They have to be literals for the planner,
    /// which means the compiler cannot catch a rename here; this can.
    /// </summary>
    [Fact]
    public void The_statements_spell_the_same_vocabulary_the_module_writes()
    {
        OverdueFallbackScan.DeadlineClaimSql.ShouldContain(
            $"attempt.status = '{NotificationAttemptStatuses.Sent}'");
        OverdueFallbackScan.UnknownClaimSql.ShouldContain(
            $"attempt.status = '{NotificationAttemptStatuses.Unknown}'");
        OverdueFallbackScan.DeadlineClaimSql.ShouldContain(
            $"notification.status = '{NotificationStatuses.Dispatched}'");
        OverdueFallbackScan.UnknownClaimSql.ShouldContain(
            $"notification.status = '{NotificationStatuses.Dispatched}'");
        OverdueFallbackScan.UnknownClaimSql.ShouldContain(
            $"notification.class = '{NotificationClasses.Critical}'");
        DeferredReleaseScan.CandidateSql.ShouldContain(
            $"notification.status = '{NotificationStatuses.Deferred}'");
        DeferredReleaseScan.ClaimSql.ShouldContain(
            $"status = '{NotificationStatuses.Deferred}'");
    }

    /// <summary>
    /// The claim of the plan advance belongs to the handler, and a scan that
    /// took it would make the handler drop the trigger the scan just wrote.
    /// Asking for the absence of the write is the only way to state that,
    /// because the defect it guards against looks like ordinary code.
    /// </summary>
    [Fact]
    public void No_scan_writes_the_plan_advance()
    {
        string[] statements =
        [
            OverdueFallbackScan.DeadlineClaimSql,
            OverdueFallbackScan.UnknownClaimSql,
            OverdueFallbackScan.StampRequestSql,
            OverdueFallbackScan.ReleaseStaleRequestSql,
            DeferredReleaseScan.CandidateSql,
            DeferredReleaseScan.ClaimSql,
        ];

        statements.ShouldAllBe(statement => !statement.Contains(
            "SET plan_advanced_at", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Both overdue scans have to leave a concluded notification alone. An
    /// attempt whose plan ended without advancing its step keeps a deadline and
    /// an empty claim forever, so a scan that did not read the state of the
    /// notification would ask for its next step once per round until the
    /// partition is dropped.
    /// </summary>
    [Fact]
    public void Both_overdue_scans_join_the_notification_they_decide_about()
    {
        foreach (var statement in new[]
        {
            OverdueFallbackScan.DeadlineClaimSql,
            OverdueFallbackScan.UnknownClaimSql,
        })
        {
            statement.ShouldContain("JOIN notifications.notification");
            statement.ShouldContain("notification.created_at > attempt.created_at - @attemptWindow");
            statement.ShouldContain("notification.created_at <= attempt.created_at");
        }
    }

    /// <summary>
    /// Every value that varies between rounds is a bind value. The literals are
    /// only the ones the planner needs to see, and none of them comes from
    /// outside this assembly.
    /// </summary>
    [Fact]
    public void The_batch_size_and_every_instant_reach_the_database_as_parameters()
    {
        OverdueFallbackScan.DeadlineClaimSql.ShouldContain("LIMIT @batchSize");
        OverdueFallbackScan.UnknownClaimSql.ShouldContain("LIMIT @batchSize");
        DeferredReleaseScan.CandidateSql.ShouldContain("LIMIT @batchSize");
        OverdueFallbackScan.DeadlineClaimSql.ShouldContain("attempt.fallback_deadline < @now");
        OverdueFallbackScan.UnknownClaimSql.ShouldContain("attempt.status_changed_at < @threshold");
        DeferredReleaseScan.CandidateSql.ShouldContain("notification.release_at <= @now");
    }

    /// <summary>
    /// Two replicas of this role must not act on the same row inside a round,
    /// and skipping is what makes the loser cheap: it does not wait for the
    /// winner, it finds nothing and moves on.
    /// </summary>
    [Fact]
    public void Every_claim_skips_the_rows_another_replica_already_holds()
    {
        OverdueFallbackScan.DeadlineClaimSql.ShouldContain("FOR UPDATE OF attempt SKIP LOCKED");
        OverdueFallbackScan.UnknownClaimSql.ShouldContain("FOR UPDATE OF attempt SKIP LOCKED");
        DeferredReleaseScan.ClaimSql.ShouldContain("FOR UPDATE SKIP LOCKED");
    }

    private static void ShouldCarryPredicateOf<TEntity>(string statement, string indexName)
        where TEntity : class
    {
        var filter = FilterOf<TEntity>(indexName);
        var normalizedStatement = Normalize(statement);
        foreach (var conjunct in filter.Split(" AND ", StringSplitOptions.TrimEntries))
        {
            normalizedStatement.ShouldContain(
                Normalize(conjunct),
                customMessage: $"a consulta perdeu '{conjunct}', que é parte do predicado de "
                    + $"'{indexName}'; sem ele o planejador não prova a implicação e o índice "
                    + "parcial deixa de ser usado, o que transforma a varredura em leitura "
                    + "sequencial de todas as partições sem nada falhar.");
        }
    }

    /// <summary>
    /// The filter as the schema declares it, read from the model rather than
    /// copied here: a copy would agree with itself forever.
    /// </summary>
    private static string FilterOf<TEntity>(string indexName)
        where TEntity : class
    {
        DbContextOptions<NotificationsDbContext> options =
            new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql("Host=schema-only;Database=schema-only;Username=schema-only")
                .Options;
        using var db = new NotificationsDbContext(options);
        IEntityType entity = db.Model.FindEntityType(typeof(TEntity))!;
        IIndex index = entity.GetIndexes().Single(
            candidate => candidate.GetDatabaseName() == indexName);
        return index.GetFilter()
            ?? throw new InvalidOperationException($"O índice '{indexName}' deixou de ser parcial.");
    }

    private static string Normalize(string sql)
        => WhitespaceRun().Replace(sql, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
