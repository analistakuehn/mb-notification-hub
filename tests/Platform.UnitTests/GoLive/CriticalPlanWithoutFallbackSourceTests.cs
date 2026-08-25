using NotificationHub.Platform.GoLiveChecks;

namespace NotificationHub.UnitTests.GoLive;

public sealed class CriticalPlanWithoutFallbackSourceTests
{
    [Fact]
    public async Task Source_measures_the_published_plan_on_the_fixed_schema_with_parameterized_filters()
    {
        const string connectionString = "Host=database;Password=secret";
        using var cancellation = new CancellationTokenSource();
        var executor = new RecordingCountQueryExecutor(2);
        var source = new CriticalPlanWithoutFallbackSource(
            executor,
            connectionString,
            "critical",
            "published");

        var count = await source.CountAsync(cancellation.Token);

        count.ShouldBe(2);
        source.Identifier.ShouldBe(GoLiveSourceIdentifiers.CriticalPlans);
        executor.CancellationToken.ShouldBe(cancellation.Token);
        executor.Query!.ConnectionString.ShouldBe(connectionString);
        executor.Query.CommandText.ShouldContain(
            "FROM templatemanagement.class_policy_version AS policy_version");
        executor.Query.CommandText.ShouldContain("policy_version.class = @notificationClass");
        executor.Query.CommandText.ShouldContain("policy_version.status = @versionStatus");

        // The count is of plans that stop at their first step, and the shape of
        // that question is what the assertion has to name: a plan of one step
        // has nothing to fall back to, and a definition with no plan at all is
        // counted the same way instead of slipping through as null.
        executor.Query.CommandText.ShouldContain("jsonb_array_length");
        executor.Query.CommandText.ShouldContain("'deliveryPlan'");
        executor.Query.CommandText.ShouldContain("COALESCE");
        executor.Query.CommandText.ShouldContain("< 2");
        executor.Query.CommandText.ShouldNotContain("'critical'");
        executor.Query.CommandText.ShouldNotContain("'published'");
        executor.Query.Parameters.ShouldBe([
            new CountQueryParameter("notificationClass", "critical"),
            new CountQueryParameter("versionStatus", "published"),
        ]);
    }

    [Fact]
    public async Task An_unreadable_plan_leaves_the_count_unavailable_instead_of_zero()
    {
        var source = new CriticalPlanWithoutFallbackSource(
            new ThrowingCountQueryExecutor(),
            "Host=database",
            "critical",
            "published");

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await source.CheckAsync(CancellationToken.None));
    }

    private sealed class RecordingCountQueryExecutor(int count) : ICountQueryExecutor
    {
        public CountQuery? Query { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public ValueTask<int> ExecuteAsync(CountQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            CancellationToken = cancellationToken;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class ThrowingCountQueryExecutor : ICountQueryExecutor
    {
        public ValueTask<int> ExecuteAsync(CountQuery query, CancellationToken cancellationToken)
            => ValueTask.FromException<int>(
                new InvalidOperationException("A definição publicada não é um plano legível."));
    }
}
