using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NotificationHub.Api.Modules.Compliance;

namespace NotificationHub.UnitTests.Compliance;

/// <summary>
/// The module composes evidence and owns none of it: no store, no schema, no
/// migration. The dependency-direction rule of the architecture suite already
/// forbids reaching into another context's persistence; what it cannot see is
/// this module growing a store of its own, which is the shortcut a member the
/// published contracts do not carry would invite.
/// </summary>
public sealed class ComplianceStoreBoundaryTests
{
    private const string ModuleNamespace = "NotificationHub.Api.Modules.Compliance";

    [Fact]
    public void The_module_declares_no_store_of_its_own()
    {
        var findings = ModuleTypes()
            .Where(type => typeof(DbContext).IsAssignableFrom(type)
                || typeof(Migration).IsAssignableFrom(type)
                || type.GetCustomAttribute<DbContextAttribute>() is not null
                || type.GetInterfaces().Any(contract => contract.IsGenericType
                    && contract.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>)))
            .Select(type => type.FullName!)
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void No_collaborator_of_the_module_is_a_store()
    {
        // A context injected from elsewhere is the same boundary breach as a
        // context declared here, and it is the shape a reader tempted by a
        // column the published contract does not carry would reach for first.
        var findings = ModuleTypes()
            .SelectMany(type => type.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => typeof(DbContext).IsAssignableFrom(parameter.ParameterType))
                .Select(parameter => $"{type.FullName}.{parameter.Name}"))
            .ToArray();

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void The_walk_actually_reaches_the_module()
    {
        // The two checks above pass trivially over an empty set, so the walk
        // itself is pinned.
        ModuleTypes().ShouldContain(typeof(ComplianceModule));
        ModuleTypes().Length.ShouldBeGreaterThan(10);
    }

    private static Type[] ModuleTypes()
        => [.. typeof(ComplianceModule).Assembly
            .GetTypes()
            .Where(type => type.Namespace is not null
                && (type.Namespace.Equals(ModuleNamespace, StringComparison.Ordinal)
                    || type.Namespace.StartsWith($"{ModuleNamespace}.", StringComparison.Ordinal)))];
}
