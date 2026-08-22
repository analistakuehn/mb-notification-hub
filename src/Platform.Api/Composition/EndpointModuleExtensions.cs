using System.Reflection;

namespace NotificationHub.Api.Composition;

public static class EndpointModuleExtensions
{
    private const string MapEndpointsMethod = "MapEndpoints";

    public static IEndpointRouteBuilder MapModuleEndpoints(
        this IEndpointRouteBuilder app,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (var module in ModuleRegistrationExtensions.DiscoverImplementations(typeof(IEndpointModule), assemblies))
        {
            var method = module.GetMethod(
                MapEndpointsMethod,
                BindingFlags.Public | BindingFlags.Static);

            method?.Invoke(null, [app]);
        }

        return app;
    }
}
