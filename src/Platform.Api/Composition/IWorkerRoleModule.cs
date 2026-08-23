namespace NotificationHub.Api.Composition;

/// <summary>
/// Composition contract of one worker role owned by a module. The worker
/// host resolves roles through discovery over the solution assemblies, so it
/// never references a module namespace; each module keeps the composition of
/// the services its role hosts.
/// </summary>
public interface IWorkerRoleModule
{
    /// <summary>Stable role name matched against the worker's configured role.</summary>
    static abstract string Role { get; }

    static abstract void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
