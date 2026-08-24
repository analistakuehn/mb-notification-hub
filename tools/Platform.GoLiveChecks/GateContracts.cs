using System.Text.Json.Serialization;

namespace NotificationHub.Platform.GoLiveChecks;

internal interface IGoLiveCheckSource
{
    string Identifier { get; }

    ValueTask<GoLiveSourceCheck> CheckAsync(CancellationToken cancellationToken);
}

internal interface IReceiptWriter
{
    ValueTask WriteAsync(
        string path,
        GoLiveReceipt receipt,
        CancellationToken cancellationToken);
}

internal sealed record GoLiveVerifiedIdentity(
    Guid TenantId,
    Guid ApplicationId,
    Guid ServicePrincipalId,
    Guid RoleId,
    string RoleValue);

internal sealed record GoLiveSourceCheck(
    int Count,
    GoLiveVerifiedIdentity? VerifiedIdentity = null);

internal sealed record GoLiveSourceReceipt(
    string Identifier,
    int? Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    GoLiveVerifiedIdentity? VerifiedIdentity = null);

internal sealed record GoLiveReceipt(
    string Timestamp,
    string Status,
    IReadOnlyList<GoLiveSourceReceipt> Sources,
    IReadOnlyList<string> Reasons);

internal sealed record GateRunResult(GoLiveReceipt Receipt, int ExitCode);

internal static class GoLiveSourceIdentifiers
{
    public const string TemplateManagement = "template-management.published-operational-templates";
    public const string MicrosoftGraph = "microsoft-graph.operational-role-assignments";
}

internal static class GoLiveStatuses
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Error = "error";
}

internal static class GoLiveExitCodes
{
    public const int Pass = 0;
    public const int Violation = 1;
    public const int Error = 2;
}

internal static class GoLiveReasons
{
    public const string PublishedOperationalTemplatesPresent = "published-operational-templates-present";
    public const string OperationalRoleAssignmentsPresent = "operational-role-assignments-present";
    public const string ConfigurationUnavailable = "configuration-unavailable";

    public static string SourceUnavailable(string identifier) => $"source-unavailable:{identifier}";
}
