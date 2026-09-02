using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using NotificationHub.Api.Composition;
using NotificationHub.SharedKernel;
using NotificationHub.Worker;

namespace NotificationHub.ArchTests;

public sealed class ArchitectureTests
{
    // Both hosts are production: the API assemblies plus the worker host,
    // composed here because a host-to-host project reference would be
    // circular. Every rule below scans the same set.
    private static readonly Assembly[] Production =
        [.. SolutionAssemblies.All, typeof(AssemblyMarker).Assembly];

    private static readonly string[] ModuleNamespaceRoots =
    [
        "NotificationHub.Api.Modules.",
    ];

    private const string PublishedContractSegment = "Integration.V1";

    [Fact]
    public void Module_domain_must_stay_technology_free()
    {
        TestResult result = Types
            .InAssemblies(Production)
            .That()
            .ResideInNamespaceMatching(@"^NotificationHub\.Api\.Modules\.[^.]+\.Domain(\.|$)")
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore",
                "MongoDB.Driver",
                "RabbitMQ.Client",
                "Confluent.Kafka",
                "StackExchange.Redis",
                "Amazon",
                "AWSSDK")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue();
    }

    [Fact]
    public void Shared_kernel_must_stay_technology_free()
    {
        TestResult result = Types
            .InAssemblies(Production)
            .That()
            .ResideInNamespaceMatching(@"^NotificationHub\.SharedKernel(\.|$)")
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore",
                "MongoDB.Driver",
                "RabbitMQ.Client",
                "Confluent.Kafka",
                "StackExchange.Redis")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue();
    }

    [Fact]
    public void Bounded_contexts_must_not_depend_on_each_other()
    {
        var modules = DiscoveredModules();
        (string Namespace, string FullName)[] candidates = ProductionTypeNames();

        foreach (var module in modules)
        {
            var forbidden = ForbiddenDependencies(module, candidates);

            if (forbidden.Length == 0)
            {
                continue;
            }

            foreach (var root in ModuleNamespaceRoots)
            {
                TestResult result = Types
                    .InAssemblies(Production)
                    .That()
                    .ResideInNamespaceMatching($@"^{Regex.Escape(root + module)}(\.|$)")
                    .ShouldNot()
                    .HaveDependencyOnAny(forbidden)
                    .GetResult();

                result.IsSuccessful.ShouldBeTrue();
            }
        }
    }

    [Fact]
    public void Cross_module_dependency_targets_allow_only_the_published_integration_surface()
    {
        (string Namespace, string FullName)[] candidates =
        [
            ("NotificationHub.Api.Modules.Consumer.Domain", "NotificationHub.Api.Modules.Consumer.Domain.Order"),
            ("NotificationHub.Api.Modules.Provider", "NotificationHub.Api.Modules.Provider.ProviderModule"),
            ("NotificationHub.Api.Modules.Provider.Domain", "NotificationHub.Api.Modules.Provider.Domain.Aggregate"),
            ("NotificationHub.Api.Modules.Provider.Features.Orders", "NotificationHub.Api.Modules.Provider.Features.Orders.Handler"),
            ("NotificationHub.Api.Modules.Provider.Infrastructure.Persistence", "NotificationHub.Api.Modules.Provider.Infrastructure.Persistence.ProviderDbContext"),
            ("NotificationHub.Api.Modules.Provider.Integration", "NotificationHub.Api.Modules.Provider.Integration.UnversionedContract"),
            ("NotificationHub.Api.Modules.Provider.Integration.V1", "NotificationHub.Api.Modules.Provider.Integration.V1.PublishedContract"),
            ("NotificationHub.Api.Modules.Provider.Integration.V1.Events", "NotificationHub.Api.Modules.Provider.Integration.V1.Events.ContractPublished"),
            ("NotificationHub.SharedKernel", "NotificationHub.SharedKernel.Result"),
        ];

        var forbidden = ForbiddenDependencies("Consumer", candidates);

        // Rejected direction: every namespace of the other module outside the
        // published surface stays a forbidden dependency target, including a
        // contract namespace without a version segment.
        forbidden.ShouldContain("NotificationHub.Api.Modules.Provider.Domain");
        forbidden.ShouldContain("NotificationHub.Api.Modules.Provider.Features.Orders");
        forbidden.ShouldContain("NotificationHub.Api.Modules.Provider.Infrastructure.Persistence");
        forbidden.ShouldContain("NotificationHub.Api.Modules.Provider.ProviderModule");
        forbidden.ShouldContain("NotificationHub.Api.Modules.Provider.Integration.UnversionedContract");

        // Accepted direction: the published surface never enters the forbidden
        // set, and no ancestor namespace entry can re-forbid it through the
        // sub-namespace matching of the dependency search.
        forbidden.ShouldAllBe(entry =>
            !entry.StartsWith("NotificationHub.Api.Modules.Provider.Integration.V1", StringComparison.Ordinal));
        forbidden.ShouldNotContain("NotificationHub.Api.Modules.Provider");
        forbidden.ShouldNotContain("NotificationHub.Api.Modules.Provider.Integration");

        // The rule only ever targets foreign module namespaces: never the
        // module's own namespaces, never the shared kernel.
        forbidden.ShouldAllBe(entry =>
            entry.StartsWith("NotificationHub.Api.Modules.Provider", StringComparison.Ordinal));
    }

    [Fact]
    public void Platform_infrastructure_must_not_depend_on_modules()
    {
        TestResult result = Types
            .InAssemblies(Production)
            .That()
            .ResideInNamespaceMatching(@"^NotificationHub\.Api\.Infrastructure(\.|$)")
            .ShouldNot()
            .HaveDependencyOnAny("NotificationHub.Api.Modules")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue();
    }

    [Fact]
    public void Worker_host_must_not_depend_on_modules()
    {
        TestResult result = Types
            .InAssemblies(Production)
            .That()
            .ResideInNamespaceMatching(@"^NotificationHub\.Worker(\.|$)")
            .ShouldNot()
            .HaveDependencyOnAny("NotificationHub.Api.Modules")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue();
    }

    [Fact]
    public void Handlers_must_use_the_declared_result_axis()
    {
        MethodInfo[] handlers = Production
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name == "Handler")
            .Select(type => type.GetMethod(
                "HandleAsync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .OfType<MethodInfo>()
            .ToArray();

        var invalid = handlers
            .Where(method => !ReturnsResult(method.ReturnType))
            .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}")
            .ToArray();

        invalid.ShouldBeEmpty();
    }

    [Fact]
    public void Shared_kernel_must_remain_small()
    {
        var publicTypeCount = typeof(Result).Assembly
            .GetExportedTypes()
            .Count(type => type.Namespace == "NotificationHub.SharedKernel");

        publicTypeCount.ShouldBeLessThanOrEqualTo(12);
    }

    private static (string Namespace, string FullName)[] ProductionTypeNames()
        => Production
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsNested: false, Namespace: not null, FullName: not null })
            .Select(type => (type.Namespace!, type.FullName!))
            .ToArray();

    // The published contract surface of a module is the only cross-module
    // dependency target another module may use; every other namespace of a
    // foreign module stays forbidden. Ancestor namespaces of the published
    // surface are banned type by type, because a namespace entry would also
    // match the published surface underneath it in the dependency search.
    private static string[] ForbiddenDependencies(
        string module,
        IEnumerable<(string Namespace, string FullName)> productionTypes)
    {
        var forbidden = new SortedSet<string>(StringComparer.Ordinal);

        foreach ((var typeNamespace, var typeFullName) in productionTypes)
        {
            foreach (var root in ModuleNamespaceRoots)
            {
                if (!typeNamespace.StartsWith(root, StringComparison.Ordinal))
                {
                    continue;
                }

                var owner = typeNamespace[root.Length..].Split('.')[0];
                if (owner.Length == 0 || owner.Equals(module, StringComparison.Ordinal))
                {
                    continue;
                }

                var published = $"{root}{owner}.{PublishedContractSegment}";
                if (typeNamespace.Equals(published, StringComparison.Ordinal)
                    || typeNamespace.StartsWith($"{published}.", StringComparison.Ordinal))
                {
                    continue;
                }

                if (published.StartsWith($"{typeNamespace}.", StringComparison.Ordinal))
                {
                    forbidden.Add(typeFullName);
                }
                else
                {
                    forbidden.Add(typeNamespace);
                }
            }
        }

        return [.. forbidden];
    }

    private static string[] DiscoveredModules()
        => Production
            .SelectMany(assembly => assembly.GetTypes())
            .Select(type => type.Namespace)
            .OfType<string>()
            .SelectMany(value => ModuleNamespaceRoots
                .Where(root => value.StartsWith(root, StringComparison.Ordinal))
                .Select(root => value[root.Length..].Split('.')[0]))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool ReturnsResult(Type returnType)
    {
        Type candidate = returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Task<>)
                ? returnType.GetGenericArguments()[0]
                : returnType;

        return candidate == typeof(Result)
            || candidate.IsGenericType
            && candidate.GetGenericTypeDefinition() == typeof(Result<>);
    }
}
