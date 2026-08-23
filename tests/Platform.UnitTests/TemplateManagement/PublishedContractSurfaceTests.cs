using System.Reflection;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Shape guard over the published contract surface: every type a public
/// member exposes must itself be publishable, so a consumer module can use
/// the contract without ever depending on this module's domain entities,
/// features or infrastructure.
/// </summary>
public sealed class PublishedContractSurfaceTests
{
    private const string PublishedNamespace = "NotificationHub.Api.Modules.TemplateManagement.Integration.V1";
    private const string SharedKernelNamespace = "NotificationHub.SharedKernel";

    [Fact]
    public void Public_members_of_the_published_contracts_expose_no_internal_module_type()
    {
        Assembly assembly = typeof(Channel).Assembly;
        Type[] contractTypes = assembly.GetTypes()
            .Where(type => type.Namespace == PublishedNamespace
                && (type.IsPublic || type.IsNestedPublic))
            .ToArray();
        contractTypes.ShouldNotBeEmpty();

        var leaks = contractTypes
            .SelectMany(ExposedTypes)
            .SelectMany(Unwrap)
            .Where(exposed => exposed.Assembly == assembly)
            .Where(exposed => exposed.Namespace != PublishedNamespace
                && exposed.Namespace != SharedKernelNamespace)
            .Select(exposed => exposed.FullName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        leaks.ShouldBeEmpty();
    }

    private static IEnumerable<Type> ExposedTypes(Type contract)
    {
        const BindingFlags visible = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        foreach (PropertyInfo property in contract.GetProperties(visible))
        {
            yield return property.PropertyType;
        }

        foreach (FieldInfo field in contract.GetFields(visible))
        {
            yield return field.FieldType;
        }

        foreach (ConstructorInfo constructor in contract.GetConstructors(visible))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (MethodInfo method in contract.GetMethods(visible).Where(candidate => !candidate.IsSpecialName))
        {
            yield return method.ReturnType;
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        if (contract.BaseType is { } baseType)
        {
            yield return baseType;
        }
    }

    /// <summary>Expands arrays and generic arguments so a wrapped leak still surfaces.</summary>
    private static IEnumerable<Type> Unwrap(Type exposed)
    {
        if (exposed.IsGenericParameter)
        {
            yield break;
        }

        if (exposed.HasElementType && exposed.GetElementType() is { } element)
        {
            foreach (Type nested in Unwrap(element))
            {
                yield return nested;
            }

            yield break;
        }

        if (exposed.IsGenericType)
        {
            yield return exposed.GetGenericTypeDefinition();
            foreach (Type nested in exposed.GetGenericArguments().SelectMany(Unwrap))
            {
                yield return nested;
            }

            yield break;
        }

        yield return exposed;
    }
}
