using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Caching;

namespace NotificationHub.Api.Modules.TemplateManagement;

public sealed class TemplateManagementModule : IModule, IEndpointModule
{
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddEntityFramework(configuration);
        services.AddRedis(configuration);
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
    }
}
