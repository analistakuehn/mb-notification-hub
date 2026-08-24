using System.Globalization;
using Npgsql;

namespace NotificationHub.Platform.GoLiveChecks;

internal sealed record CountQueryParameter(string Name, object Value);

internal sealed record CountQuery(
    string ConnectionString,
    string CommandText,
    IReadOnlyList<CountQueryParameter> Parameters);

internal interface ICountQueryExecutor
{
    ValueTask<int> ExecuteAsync(CountQuery query, CancellationToken cancellationToken);
}

internal sealed class PublishedOperationalTemplateSource(
    ICountQueryExecutor executor,
    string connectionString,
    string notificationClass,
    string versionStatus) : IGoLiveCheckSource
{
    private const string QueryText = """
        SELECT COUNT(*)
        FROM templatemanagement.template AS template
        INNER JOIN templatemanagement.template_version AS version
            ON version.template_key = template.key
        WHERE template.class = @notificationClass
          AND version.status = @versionStatus
        """;

    public string Identifier => GoLiveSourceIdentifiers.TemplateManagement;

    public ValueTask<int> CountAsync(CancellationToken cancellationToken)
        => executor.ExecuteAsync(
            new CountQuery(
                connectionString,
                QueryText,
                [
                    new CountQueryParameter("notificationClass", notificationClass),
                    new CountQueryParameter("versionStatus", versionStatus),
                ]),
            cancellationToken);

    public async ValueTask<GoLiveSourceCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var count = await CountAsync(cancellationToken);
        return new GoLiveSourceCheck(count);
    }
}

internal sealed class NpgsqlCountQueryExecutor : ICountQueryExecutor
{
    public async ValueTask<int> ExecuteAsync(CountQuery query, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(query.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = CreateCommand(connection, query);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return checked(Convert.ToInt32(scalar, CultureInfo.InvariantCulture));
    }

    internal static NpgsqlCommand CreateCommand(NpgsqlConnection connection, CountQuery query)
    {
        NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = query.CommandText;
        foreach (CountQueryParameter parameter in query.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return command;
    }
}
