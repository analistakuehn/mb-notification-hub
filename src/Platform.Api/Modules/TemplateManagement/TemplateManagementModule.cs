using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;
using NotificationHub.Api.Modules.TemplateManagement.Features.Queries;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Caching;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;

namespace NotificationHub.Api.Modules.TemplateManagement;

public sealed class TemplateManagementModule : IModule, IEndpointModule
{
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddEntityFramework(configuration);
        services.AddRedis(configuration);
        services.AddTemplateManagementAuthorization();
        services.AddTemplateManagementRateLimiting();
        services.AddValidatorsFromAssembly(typeof(TemplateManagementModule).Assembly, includeInternalTypes: true);
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<CreateTemplate.Handler>();
        services.AddScoped<CreateTemplateVersion.Handler>();
        services.AddScoped<PutTemplateVersionContent.Handler>();
        services.AddScoped<PutTemplateVersionVariablesSchema.Handler>();
        services.AddScoped<ListTemplates.Handler>();
        services.AddScoped<GetTemplate.Handler>();
        services.AddScoped<GetTemplateVersion.Handler>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        var templates = app.MapGroup("/v1/templates");
        CreateTemplate.MapEndpoint(templates);
        CreateTemplateVersion.MapEndpoint(templates);
        PutTemplateVersionContent.MapEndpoint(templates);
        PutTemplateVersionVariablesSchema.MapEndpoint(templates);
        ListTemplates.MapEndpoint(templates);
        GetTemplate.MapEndpoint(templates);
        GetTemplateVersion.MapEndpoint(templates);
    }
}
