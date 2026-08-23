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

        foreach (Type module in DiscoverImplementations(typeof(IModule), assemblies))
        {
            MethodInfo? method = module.GetMethod(
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

    /// <summary>
    /// Discovers every module-owned worker role: role name to its composition
    /// entry. The worker host merges these with the platform-owned roles, so
    /// hosting a module role never adds a host-to-module reference.
    /// </summary>
    public static IReadOnlyDictionary<string, Action<IServiceCollection, IConfiguration>> DiscoverWorkerRoles(
        params Assembly[] assemblies)
    {
        var roles = new Dictionary<string, Action<IServiceCollection, IConfiguration>>(StringComparer.Ordinal);
        foreach (Type module in DiscoverImplementations(typeof(IWorkerRoleModule), assemblies))
        {
            var role = (string?)module
                .GetProperty(nameof(IWorkerRoleModule.Role), BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null)
                ?? throw new InvalidOperationException(
                    $"O papel de worker '{module.FullName}' não expõe a propriedade estática Role.");
            MethodInfo configure = module.GetMethod(
                ConfigureServicesMethod, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"O papel de worker '{module.FullName}' não expõe o método estático {ConfigureServicesMethod}.");
            if (!roles.TryAdd(role, (services, configuration) => configure.Invoke(null, [services, configuration])))
            {
                throw new InvalidOperationException(
                    $"O papel de worker '{role}' está declarado por mais de um módulo.");
            }
        }

        return roles;
    }
}
