using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// The closing cycle and the guards around it. The cycle is the only path in
/// the system that stops accepting writes and detaches data, so these tests
/// assert both what it does when everything holds and what it refuses to do
/// when anything does not.
/// </summary>
[Collection(AuditMaintenanceCollectionDefinition.Name)]
public sealed class AuditClosingCycleTests(AuditMaintenanceFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_cycle_revokes_verifies_exports_checks_the_copy_and_detaches_without_dropping()
    {
        DateTimeOffset month = MonthOffset(-14);
        MonthlyPartitionWindow window = WindowOf(month);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"closing-1-{Guid.CreateVersion7():N}", month.AddDays(3));
        await fixture.AppendAsync($"closing-2-{Guid.CreateVersion7():N}", month.AddDays(9));

        // The grant must exist before the revoke, otherwise the revoke would
        // remove nothing and the assertion below would prove nothing.
        (await HasInsertPrivilegeAsync(window.PartitionName)).ShouldBeTrue();

        await using ServiceProvider provider = fixture.BuildProvider(ClosingGates());
        IReadOnlyList<PartitionClosingOutcome> outcomes = await RunClosingAsync(provider, window);

        PartitionClosingOutcome outcome = outcomes.ShouldHaveSingleItem();
        outcome.Closed.ShouldBeTrue(outcome.Failure);
        outcome.Stage.ShouldBe("detach");

        (await HasInsertPrivilegeAsync(window.PartitionName)).ShouldBeFalse();
        (await IsAttachedAsync(window.PartitionName)).ShouldBeFalse();

        // Detached, never destroyed: the drop gate stayed off.
        (await TableExistsAsync(window.PartitionName)).ShouldBeTrue();

        var manifestKey = ClosingFolder(window.PartitionName) + AuditExportKeys.ManifestObject;
        (await ActionsOfAsync(window.PartitionName))
            .ShouldBe([AuditActions.AuditChainVerified, AuditActions.AuditExported, AuditActions.AuditPartitionClosed]);
        (await DetailsOfAsync(window.PartitionName, AuditActions.AuditPartitionClosed))
            .ShouldContain(manifestKey);
    }

    [RequiresDockerFact]
    public async Task A_copy_that_does_not_read_back_stops_the_cycle_before_anything_is_detached()
    {
        DateTimeOffset month = MonthOffset(-15);
        MonthlyPartitionWindow window = WindowOf(month);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"closing-corrupt-{Guid.CreateVersion7():N}", month.AddDays(4));

        await using ServiceProvider provider = fixture.BuildProvider(
            ClosingGates(),
            services => services.AddScoped<IWormObjectStore>(serviceProvider =>
                new CorruptingWormObjectStore(
                    ActivatorUtilities.CreateInstance<S3WormObjectStore>(serviceProvider),
                    AuditExportKeys.EventsObject)));

        IReadOnlyList<PartitionClosingOutcome> outcomes = await RunClosingAsync(provider, window);

        PartitionClosingOutcome outcome = outcomes.ShouldHaveSingleItem();
        outcome.Closed.ShouldBeFalse();
        outcome.Stage.ShouldBe("copy");

        // The write revoke already happened, which is safe and reversible;
        // nothing beyond it did.
        (await IsAttachedAsync(window.PartitionName)).ShouldBeTrue();
        (await TableExistsAsync(window.PartitionName)).ShouldBeTrue();
        (await ActionsOfAsync(window.PartitionName)).ShouldNotContain(AuditActions.AuditPartitionClosed);
        (await ActionsOfAsync(window.PartitionName)).ShouldContain(AuditActions.AuditChainVerificationFailed);
    }

    [RequiresDockerFact]
    public async Task A_closed_partition_refuses_an_insert_routed_through_the_trail()
    {
        DateTimeOffset month = MonthOffset(-16);
        MonthlyPartitionWindow window = WindowOf(month);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"closed-insert-{Guid.CreateVersion7():N}", month.AddDays(5));

        await using ServiceProvider provider = fixture.BuildProvider();
        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<ClosedPartitionGuard>()
                .RevokeWritesAsync(window, CancellationToken.None);
        }

        (await HasInsertPrivilegeAsync(window.PartitionName)).ShouldBeFalse();

        PostgresException failure = await Should.ThrowAsync<PostgresException>(
            () => fixture.AppendAsync($"closed-insert-late-{Guid.CreateVersion7():N}", month.AddDays(6)));

        failure.MessageText.ShouldContain("fechada");
    }

    [RequiresDockerFact]
    public async Task The_appender_role_may_append_and_read_the_trail_and_nothing_else()
    {
        (await HasPrivilegeAsync("audit.audit_event", "INSERT")).ShouldBeTrue();
        (await HasPrivilegeAsync("audit.audit_event", "SELECT")).ShouldBeTrue();
        (await HasPrivilegeAsync("audit.audit_event", "UPDATE")).ShouldBeFalse();
        (await HasPrivilegeAsync("audit.audit_event", "DELETE")).ShouldBeFalse();
        (await HasPrivilegeAsync("audit.approval", "INSERT")).ShouldBeTrue();
        (await HasPrivilegeAsync("audit.approval", "UPDATE")).ShouldBeFalse();
        (await HasPrivilegeAsync("audit.approval", "DELETE")).ShouldBeFalse();

        // A grant role, never a login: environment users are provisioned by
        // infrastructure and granted into it.
        (await fixture.ScalarAsync<bool>(
            "SELECT rolcanlogin FROM pg_roles WHERE rolname = 'audit_appender'")).ShouldBeFalse();
    }

    private static Dictionary<string, string?> ClosingGates()
        => new()
        {
            ["Modules:Audit:PartitionManager:EnableRetentionCycle"] = "true",
            ["Modules:Audit:PartitionManager:EnableRevokeOnClosedPartitions"] = "true",
            ["Modules:Audit:PartitionManager:EnableDropDetachedPartitions"] = "false",
            ["Modules:Audit:PartitionManager:ClosingGraceDays"] = "0",
        };

    private static async Task<IReadOnlyList<PartitionClosingOutcome>> RunClosingAsync(
        ServiceProvider provider,
        MonthlyPartitionWindow window)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<PartitionClosingCycle>()
            .RunAsync([window], [], CancellationToken.None);
    }

    private Task<bool> HasInsertPrivilegeAsync(string partition)
        => HasPrivilegeAsync($"audit.{partition}", "INSERT");

    private Task<bool> HasPrivilegeAsync(string table, string privilege)
        => fixture.ScalarAsync<bool>(
            $"SELECT has_table_privilege('audit_appender', '{table}', '{privilege}')");

    private Task<bool> IsAttachedAsync(string partition)
        => fixture.ScalarAsync<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM pg_inherits
                WHERE inhparent = 'audit.audit_event'::regclass
                  AND inhrelid = 'audit.{partition}'::regclass)
            """);

    private Task<bool> TableExistsAsync(string partition)
        => fixture.ScalarAsync<bool>($"SELECT to_regclass('audit.{partition}') IS NOT NULL");

    /// <summary>Actions recorded by the maintenance for one partition, in chain order.</summary>
    private Task<List<string>> ActionsOfAsync(string partition)
        => fixture.QueryTextsAsync($"""
            SELECT action FROM audit.audit_event
            WHERE entity_type = 'audit_partition' AND entity_id = '{partition}'
            ORDER BY seq
            """);

    private async Task<string> DetailsOfAsync(string partition, string action)
    {
        List<string> details = await fixture.QueryTextsAsync($"""
            SELECT details::text FROM audit.audit_event
            WHERE entity_type = 'audit_partition' AND entity_id = '{partition}' AND action = '{action}'
            ORDER BY seq
            """);
        return string.Join('\n', details);
    }

    private static string ClosingFolder(string partition)
        => AuditExportKeys.ClosingFolder("audit-export/v1", "audit_event", partition);

    private static MonthlyPartitionWindow WindowOf(DateTimeOffset month)
        => MonthlyPartitions.Plan("audit_event", month, 0)[0];

    private static DateTimeOffset MonthOffset(int months)
    {
        DateTime utc = DateTime.UtcNow;
        return new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero)
            .AddMonths(months);
    }
}

/// <summary>
/// Returns a byte-altered copy of one object family on read. It stands in for
/// storage that lost or changed what was written, which is the only way to
/// observe whether the cycle really checks its copy before it detaches.
/// </summary>
internal sealed class CorruptingWormObjectStore(IWormObjectStore inner, string keySuffix) : IWormObjectStore
{
    public Task<WormObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
        => inner.HeadAsync(key, cancellationToken);

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var content = await inner.GetAsync(key, cancellationToken);
        if (content is null || content.Length == 0 || !key.EndsWith(keySuffix, StringComparison.Ordinal))
        {
            return content;
        }

        var altered = (byte[])content.Clone();
        altered[^1] ^= 0x01;
        return altered;
    }

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
        => inner.PutAsync(key, content, contentType, cancellationToken);
}
