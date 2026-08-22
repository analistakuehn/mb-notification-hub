using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using NotificationHub.Api.Composition;
using NotificationHub.SharedKernel;

namespace NotificationHub.ArchTests;

public sealed class ArchitectureTests
{
    private static readonly Assembly[] Production = SolutionAssemblies.All;

    private static readonly string[] ModuleNamespaceRoots =
    [
        "NotificationHub.Api.Modules.",
    ];

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
                "StackExchange.Redis")
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
        string[] modules = DiscoveredModules();

        foreach (string module in modules)
        {
            string[] forbidden = modules
                .Where(other => !other.Equals(module, StringComparison.Ordinal))
                .SelectMany(other => ModuleNamespaceRoots.Select(root => root + other))
                .ToArray();

            if (forbidden.Length == 0)
            {
                continue;
            }

            foreach (string root in ModuleNamespaceRoots)
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

        string[] invalid = handlers
            .Where(method => !ReturnsResult(method.ReturnType))
            .Select(method => $"{method.DeclaringType?.FullName}.{method.Name}")
            .ToArray();

        invalid.ShouldBeEmpty();
    }

    [Fact]
    public void Shared_kernel_must_remain_small()
    {
        int publicTypeCount = typeof(Result).Assembly
            .GetExportedTypes()
            .Count(type => type.Namespace == "NotificationHub.SharedKernel");

        publicTypeCount.ShouldBeLessThanOrEqualTo(12);
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
