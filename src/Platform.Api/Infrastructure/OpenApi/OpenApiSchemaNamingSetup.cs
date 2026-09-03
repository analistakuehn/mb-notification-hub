using System.Text;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;

namespace NotificationHub.Api.Infrastructure.OpenApi;

/// <summary>
/// Names the schemas of the published documents after the module that declares
/// the type, instead of after the bare type name.
/// <para>
/// The generator names a schema after the short name of the type, and a modular
/// monolith declares the same short names in every module: the request record of
/// one module and the request record of another both arrive as <c>Command</c>.
/// A document holds one entry per name, so all of them collapse into a single
/// schema and every operation that referenced any of them ends up pointing at
/// whichever type the generator reached first. A client generated from such a
/// document sends the body of another resource.
/// </para>
/// <para>
/// A type declared under a module is therefore named after its full CLR name
/// with the shared module root removed, which separates the types the bare name
/// had merged. Types declared outside the modules, such as the problem-details
/// payload and the framework payloads beside it, keep the name the generator
/// gives them: they are one type per name already, and renaming them would move
/// entries that carry no defect.
/// </para>
/// </summary>
public static class OpenApiSchemaNamingSetup
{
    private const string ModuleNamespaceRoot = "NotificationHub.Api.Modules.";

    public static OpenApiOptions UseModuleQualifiedSchemaNames(this OpenApiOptions options)
    {
        options.CreateSchemaReferenceId = CreateModuleQualifiedSchemaReferenceId;
        return options;
    }

    private static string? CreateModuleQualifiedSchemaReferenceId(JsonTypeInfo jsonTypeInfo)
    {
        var generatedName = OpenApiOptions.CreateDefaultSchemaReferenceId(jsonTypeInfo);

        // No name means the generator inlines the schema at every use instead
        // of referencing it, and an inlined schema shares no dictionary entry
        // with anything, so it cannot collide.
        if (generatedName is null)
        {
            return null;
        }

        Type type = jsonTypeInfo.Type;
        if (type.Namespace is not { } declaringNamespace
            || !declaringNamespace.StartsWith(ModuleNamespaceRoot, StringComparison.Ordinal))
        {
            return generatedName;
        }

        var qualifiedName = new StringBuilder(declaringNamespace[ModuleNamespaceRoot.Length..]);
        foreach (var enclosingName in EnclosingTypeNames(type))
        {
            qualifiedName.Append('.').Append(enclosingName);
        }

        return qualifiedName.Append('.').Append(generatedName).ToString();
    }

    /// <summary>
    /// Names of the types that enclose <paramref name="type"/>, outermost
    /// first. A feature declares its contract as a nested record, so without
    /// them two features of one module would still meet under the same name.
    /// </summary>
    private static string[] EnclosingTypeNames(Type type)
    {
        List<string> names = [];
        for (Type? enclosing = type.DeclaringType; enclosing is not null; enclosing = enclosing.DeclaringType)
        {
            names.Add(WithoutArityMarker(enclosing.Name));
        }

        names.Reverse();
        return [.. names];
    }

    /// <summary>
    /// A reference name is read as a URL fragment, and the arity marker that a
    /// generic type carries in its CLR name is not a character a fragment
    /// admits, so it is dropped rather than escaped.
    /// </summary>
    private static string WithoutArityMarker(string name)
    {
        var marker = name.IndexOf('`');
        return marker < 0 ? name : name[..marker];
    }
}
