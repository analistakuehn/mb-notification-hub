namespace NotificationHub.Platform.GoLiveChecks;

/// <summary>
/// Counts the published policies whose delivery plan stops at its first step,
/// over every class that owes a fallback. A plan of a single step has nothing
/// to fall back to, so every notification governed by it rides on one channel
/// answering in time.
/// <para>
/// Which classes owe one is not a single name. The critical class owes it by
/// definition, and so does any class that hosts a published template of the
/// authentication purpose, because the rest of the design treats the two as one
/// unit: the scheduler asks for the next step on either, and the router sends
/// either to the authentication queue whatever the class is. A gate that read
/// only the class name would pass a transactional policy with a one-step plan
/// that happens to host the codes people log in with, which is the notification
/// this gate exists for.
/// </para>
/// <para>
/// The length is read from the stored definition rather than from a column,
/// because the definition is the published document itself: it is kept as the
/// submitted text so its content hash still verifies, and the plan is one of
/// its fields. A definition whose plan is absent counts as a violation, on the
/// same principle. A definition whose plan is not an array makes the query
/// fail, which the gate reads as an unavailable source and answers with the
/// error exit code, never with a pass.
/// </para>
/// <para>
/// What the gate still does not measure is runtime reach: it reads what is
/// published, not which policy a notification in flight was admitted under.
/// </para>
/// </summary>
internal sealed class CriticalPlanWithoutFallbackSource(
    ICountQueryExecutor executor,
    string connectionString,
    string notificationClass,
    string versionStatus,
    string authenticationPurpose,
    string templateVersionStatus) : IGoLiveCheckSource
{
    private const string QueryText = """
        SELECT COUNT(*)
        FROM templatemanagement.class_policy_version AS policy_version
        WHERE policy_version.status = @versionStatus
          AND COALESCE(
                jsonb_array_length((policy_version.definition)::jsonb -> 'deliveryPlan'),
                0) < 2
          AND (
                policy_version.class = @notificationClass
             OR EXISTS (
                    SELECT 1
                    FROM templatemanagement.template AS template
                    INNER JOIN templatemanagement.template_version AS version
                        ON version.template_key = template.key
                    WHERE template.class = policy_version.class
                      AND template.purpose = @authenticationPurpose
                      AND version.status = @templateVersionStatus
                )
          )
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
                    new CountQueryParameter("authenticationPurpose", authenticationPurpose),
                    new CountQueryParameter("templateVersionStatus", templateVersionStatus),
                ]),
            cancellationToken);

    public async ValueTask<GoLiveSourceCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var count = await CountAsync(cancellationToken);
        return new GoLiveSourceCheck(count);
    }
}
