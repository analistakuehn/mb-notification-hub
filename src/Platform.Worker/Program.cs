namespace NotificationHub.Worker;

/// <summary>
/// Thin composition root of the worker host: resolve the configured role,
/// compose what that role owns, run. A namespaced entry point on purpose, so
/// the type never collides with the API host's <c>Program</c> in test
/// projects that reference both hosts.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        await builder.Build().RunAsync();
    }
}
