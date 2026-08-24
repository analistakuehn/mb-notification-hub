using NotificationHub.Platform.GoLiveChecks;
using Npgsql;

namespace NotificationHub.UnitTests.GoLive;

public sealed class PublishedOperationalTemplateSourceTests
{
    [Fact]
    public async Task Source_uses_the_fixed_schema_and_parameterized_filters_with_cancellation()
    {
        const string connectionString = "Host=database;Password=secret";
        using var cancellation = new CancellationTokenSource();
        var executor = new RecordingCountQueryExecutor(4);
        var source = new PublishedOperationalTemplateSource(
            executor,
            connectionString,
            "operational",
            "published");

        var count = await source.CountAsync(cancellation.Token);

        count.ShouldBe(4);
        executor.CancellationToken.ShouldBe(cancellation.Token);
        executor.Query!.ConnectionString.ShouldBe(connectionString);
        executor.Query.CommandText.ShouldContain("FROM templatemanagement.template AS template");
        executor.Query.CommandText.ShouldContain("INNER JOIN templatemanagement.template_version AS version");
        executor.Query.CommandText.ShouldContain("template.class = @notificationClass");
        executor.Query.CommandText.ShouldContain("version.status = @versionStatus");
        executor.Query.CommandText.ShouldNotContain("'operational'");
        executor.Query.CommandText.ShouldNotContain("'published'");
        executor.Query.Parameters.ShouldBe([
            new CountQueryParameter("notificationClass", "operational"),
            new CountQueryParameter("versionStatus", "published"),
        ]);
    }

    [Fact]
    public void Npgsql_executor_builds_parameters_instead_of_interpolating_values()
    {
        var query = new CountQuery(
            "not-used-to-create-the-command",
            "SELECT COUNT(*) FROM item WHERE class = @notificationClass",
            [new CountQueryParameter("notificationClass", "operational")]);
        using var connection = new NpgsqlConnection();

        using NpgsqlCommand command = NpgsqlCountQueryExecutor.CreateCommand(connection, query);

        command.CommandText.ShouldBe(query.CommandText);
        command.Parameters.Count.ShouldBe(1);
        command.Parameters[0].ParameterName.ShouldBe("notificationClass");
        command.Parameters[0].Value.ShouldBe("operational");
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
}
