namespace NotificationHub.Platform.GoLiveChecks;

/// <summary>
/// Counts the published policies of one class whose delivery plan stops at its
/// first step. A plan of a single step has nothing to fall back to, so every
/// notification governed by it rides on one channel answering in time.
/// <para>
/// The length is read from the stored definition rather than from a column,
/// because the definition is the published document itself: it is kept as the
/// submitted text so its content hash still verifies, and the plan is one of
/// its fields. A definition whose plan is absent counts as a violation, on the
/// same principle. A definition whose plan is not an array makes the query
/// fail, which the gate reads as an unavailable source and answers with the
/// error exit code, never with a pass.
/// </para>
/// </summary>
internal sealed class CriticalPlanWithoutFallbackSource(
    ICountQueryExecutor executor,
    string connectionString,
    string notificationClass,
    string versionStatus) : IGoLiveCheckSource
{
    private const string QueryText = """
        SELECT COUNT(*)
        FROM templatemanagement.class_policy_version AS policy_version
        WHERE policy_version.class = @notificationClass
          AND policy_version.status = @versionStatus
          AND COALESCE(
                jsonb_array_length((policy_version.definition)::jsonb -> 'deliveryPlan'),
                0) < 2
        """;

    public string Identifier => GoLiveSourceIdentifiers.CriticalPlans;

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
