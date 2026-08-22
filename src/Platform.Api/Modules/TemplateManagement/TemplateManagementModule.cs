using FluentValidation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;
using NotificationHub.Api.Modules.TemplateManagement.Features.Queries;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Caching;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

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
        services.AddTemplateManagementTemplating(configuration);
        services.AddValidatorsFromAssembly(typeof(TemplateManagementModule).Assembly, includeInternalTypes: true);
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<CreateTemplate.Handler>();
        services.AddScoped<CreateTemplateVersion.Handler>();
        services.AddScoped<PutTemplateVersionContent.Handler>();
        services.AddScoped<PutTemplateVersionVariablesSchema.Handler>();
        services.AddScoped<PutTemplateVersionLayout.Handler>();
        services.AddScoped<PublishTemplateVersion.Handler>();
        services.AddScoped<DeprecateTemplate.Handler>();
        services.AddScoped<DisableTemplate.Handler>();
        services.AddScoped<RollbackTemplate.Handler>();
        services.AddScoped<ListTemplates.Handler>();
        services.AddScoped<GetTemplate.Handler>();
        services.AddScoped<GetTemplateVersion.Handler>();
        services.AddScoped<ValidateTemplateVersion.Handler>();
        services.AddScoped<RenderTemplateVersion.Handler>();
        services.AddScoped<DiffTemplateVersions.Handler>();
        services.AddScoped<CreateLayout.Handler>();
        services.AddScoped<CreateLayoutVersion.Handler>();
        services.AddScoped<PutLayoutVersionContent.Handler>();
        services.AddScoped<PublishLayoutVersion.Handler>();
        services.AddScoped<DeprecateLayout.Handler>();
        services.AddScoped<DisableLayout.Handler>();
        services.AddScoped<RollbackLayout.Handler>();
        services.AddScoped<ListLayouts.Handler>();
        services.AddScoped<GetLayout.Handler>();
        services.AddScoped<GetLayoutVersion.Handler>();
        services.AddScoped<ValidateLayoutVersion.Handler>();
        services.AddScoped<DiffLayoutVersions.Handler>();
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder templates = app.MapGroup("/v1/templates");
        CreateTemplate.MapEndpoint(templates);
        CreateTemplateVersion.MapEndpoint(templates);
        PutTemplateVersionContent.MapEndpoint(templates);
        PutTemplateVersionVariablesSchema.MapEndpoint(templates);
        PublishTemplateVersion.MapEndpoint(templates);
        DeprecateTemplate.MapEndpoint(templates);
        DisableTemplate.MapEndpoint(templates);
        RollbackTemplate.MapEndpoint(templates);
        ListTemplates.MapEndpoint(templates);
        GetTemplate.MapEndpoint(templates);
        GetTemplateVersion.MapEndpoint(templates);
        ValidateTemplateVersion.MapEndpoint(templates);
        RenderTemplateVersion.MapEndpoint(templates);
        PutTemplateVersionLayout.MapEndpoint(templates);
        DiffTemplateVersions.MapEndpoint(templates);

        RouteGroupBuilder layouts = app.MapGroup("/v1/layouts");
        CreateLayout.MapEndpoint(layouts);
        CreateLayoutVersion.MapEndpoint(layouts);
        PutLayoutVersionContent.MapEndpoint(layouts);
        PublishLayoutVersion.MapEndpoint(layouts);
        DeprecateLayout.MapEndpoint(layouts);
        DisableLayout.MapEndpoint(layouts);
        RollbackLayout.MapEndpoint(layouts);
        ListLayouts.MapEndpoint(layouts);
        GetLayout.MapEndpoint(layouts);
        GetLayoutVersion.MapEndpoint(layouts);
        ValidateLayoutVersion.MapEndpoint(layouts);
        DiffLayoutVersions.MapEndpoint(layouts);
    }
}
