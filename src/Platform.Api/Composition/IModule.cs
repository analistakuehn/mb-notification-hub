namespace NotificationHub.Api.Composition;

/// <summary>Service registration contract for one bounded context.</summary>
public interface IModule
{
    static abstract void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
