namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

public static class TemplatingSetup
{
    public static IServiceCollection AddTemplateManagementTemplating(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TemplatingOptions>()
            .Bind(configuration.GetSection(TemplatingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<ScribanTemplateEngine>();
        services.AddSingleton<TemplateVersionAnalyzer>();
        services.AddSingleton<LayoutVersionAnalyzer>();
        return services;
    }
}
