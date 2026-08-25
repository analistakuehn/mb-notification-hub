using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Platform.GoLiveChecks;

internal static class Program
{
    private static readonly HttpClient GraphClient = new()
    {
        BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static async Task<int> Main()
    {
        try
        {
            return await GoLiveCheckApplication.RunAsync(
                Environment.GetEnvironmentVariable,
                CreateGate,
                new FileReceiptWriter(),
                TimeProvider.System,
                CancellationToken.None);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Go-live checks could not persist a safe receipt.");
            return GoLiveExitCodes.Error;
        }
    }

    private static GoLiveGate CreateGate(GateConfiguration configuration, TimeProvider timeProvider)
        => new(
            new PublishedOperationalTemplateSource(
                new NpgsqlCountQueryExecutor(),
                configuration.TemplateManagementConnectionString,
                NotificationClasses.Operational,
                TemplateVersionStatuses.Published),
            new MicrosoftGraphOperationalRoleSource(
                GraphClient,
                configuration.GraphAccessToken,
                configuration.GraphTenantId,
                configuration.GraphApplicationId,
                configuration.GraphServicePrincipalId),
            new CriticalPlanWithoutFallbackSource(
                new NpgsqlCountQueryExecutor(),
                configuration.TemplateManagementConnectionString,
                NotificationClasses.Critical,
                ClassPolicyVersionStatuses.Published),
            timeProvider);
}
