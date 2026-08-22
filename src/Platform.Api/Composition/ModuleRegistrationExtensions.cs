using System.Reflection;

namespace NotificationHub.Api.Composition;

public static class ModuleRegistrationExtensions
{
    private const string ConfigureServicesMethod = "ConfigureServices";

    public static IServiceCollection AddModules(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var module in DiscoverImplementations(typeof(IModule), assemblies))
        {
            var method = module.GetMethod(
                ConfigureServicesMethod,
                BindingFlags.Public | BindingFlags.Static);

            method?.Invoke(null, [services, configuration]);
        }

        return services;
    }

    public static IEnumerable<Type> DiscoverImplementations(Type contract, Assembly[] assemblies)
        => assemblies
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && type.GetInterfaces().Contains(contract))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);
}
