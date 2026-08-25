using System.Globalization;

namespace NotificationHub.Platform.GoLiveChecks;

internal static class GoLiveEnvironmentVariables
{
    public const string TemplateManagementConnectionString = "GO_LIVE_TEMPLATE_MANAGEMENT_CONNECTION_STRING";
    public const string GraphAccessToken = "GO_LIVE_GRAPH_ACCESS_TOKEN";
    public const string GraphTenantId = "GO_LIVE_GRAPH_TENANT_ID";
    public const string GraphApplicationId = "GO_LIVE_GRAPH_APPLICATION_ID";
    public const string GraphServicePrincipalId = "GO_LIVE_GRAPH_SERVICE_PRINCIPAL_ID";
    public const string ReceiptPath = "GO_LIVE_RECEIPT_PATH";
}

internal sealed class GateConfiguration
{
    public required string TemplateManagementConnectionString { get; init; }

    public required string GraphAccessToken { get; init; }

    public required Guid GraphTenantId { get; init; }

    public required Guid GraphApplicationId { get; init; }

    public required Guid GraphServicePrincipalId { get; init; }

    public required string ReceiptPath { get; init; }

    public static GateConfiguration? Load(Func<string, string?> readEnvironment)
    {
        var connectionString = readEnvironment(GoLiveEnvironmentVariables.TemplateManagementConnectionString);
        var graphAccessToken = readEnvironment(GoLiveEnvironmentVariables.GraphAccessToken);
        var tenantValue = readEnvironment(GoLiveEnvironmentVariables.GraphTenantId);
        var applicationValue = readEnvironment(GoLiveEnvironmentVariables.GraphApplicationId);
        var servicePrincipalValue = readEnvironment(GoLiveEnvironmentVariables.GraphServicePrincipalId);
        var receiptPath = readEnvironment(GoLiveEnvironmentVariables.ReceiptPath);

        return string.IsNullOrWhiteSpace(connectionString)
            || string.IsNullOrWhiteSpace(graphAccessToken)
            || !TryReadRequiredGuid(tenantValue, out Guid tenantId)
            || !TryReadRequiredGuid(applicationValue, out Guid applicationId)
            || !Guid.TryParse(servicePrincipalValue, out Guid servicePrincipalId)
            || servicePrincipalId == Guid.Empty
            || string.IsNullOrWhiteSpace(receiptPath)
                ? null
                : new GateConfiguration
                {
                    TemplateManagementConnectionString = connectionString,
                    GraphAccessToken = graphAccessToken,
                    GraphTenantId = tenantId,
                    GraphApplicationId = applicationId,
                    GraphServicePrincipalId = servicePrincipalId,
                    ReceiptPath = receiptPath,
                };
    }

    private static bool TryReadRequiredGuid(string? value, out Guid id)
        => Guid.TryParse(value, out id) && id != Guid.Empty;
}

internal static class GoLiveCheckApplication
{
    public static async Task<int> RunAsync(
        Func<string, string?> readEnvironment,
        Func<GateConfiguration, TimeProvider, GoLiveGate> gateFactory,
        IReceiptWriter receiptWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var configuration = GateConfiguration.Load(readEnvironment);
        if (configuration is null)
        {
            var receiptPath = readEnvironment(GoLiveEnvironmentVariables.ReceiptPath);
            if (!string.IsNullOrWhiteSpace(receiptPath))
            {
                var errorReceipt = new GoLiveReceipt(
                    timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                    GoLiveStatuses.Error,
                    [
                        new GoLiveSourceReceipt(GoLiveSourceIdentifiers.TemplateManagement, null),
                        new GoLiveSourceReceipt(GoLiveSourceIdentifiers.MicrosoftGraph, null),
                        new GoLiveSourceReceipt(GoLiveSourceIdentifiers.CriticalPlans, null),
                    ],
                    [GoLiveReasons.ConfigurationUnavailable]);
                await receiptWriter.WriteAsync(receiptPath, errorReceipt, cancellationToken);
            }

            return GoLiveExitCodes.Error;
        }

        GoLiveGate gate = gateFactory(configuration, timeProvider);
        GateRunResult result = await gate.RunAsync(cancellationToken);
        await receiptWriter.WriteAsync(
            configuration.ReceiptPath,
            result.Receipt,
            cancellationToken);
        return result.ExitCode;
    }
}
